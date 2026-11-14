using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace OuterVoice.Windows;

/// <summary>Friends tab (imported packs) and the export section of the Voice Pack tab.</summary>
public sealed partial class ConfigWindow
{
    private string? shareStatus;
    private bool shareStatusIsError;
    private bool exporting;

    // ---- Export (drawn at the bottom of the Voice Pack tab) -----------------------------

    private void DrawExport(VoicePack pack)
    {
        Section("Share");
        var cfg = plugin.Configuration;

        var compress = cfg.CompressOnExport;
        if (ImGui.Checkbox("Compress audio for sharing", ref compress))
        {
            cfg.CompressOnExport = compress;
            cfg.Save();
        }
        HelpMarker("Re-encodes clips to mono 48 kbps for the shared copy, about a third of the size. Your own files aren't touched.");

        if (exporting) ImGui.BeginDisabled();
        if (ImGui.Button(exporting ? "Exporting..." : "Export pack for friends..."))
        {
            if (plugin.PackDirty)
                plugin.SavePack();

            var fileName = SafeName(string.IsNullOrWhiteSpace(pack.Name) ? "voicepack" : pack.Name) + " voice pack.zip";
            plugin.FileDialogs.SaveFileDialog("Save voice pack", ".zip", fileName, ".zip", (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path)) return;
                Export(pack, path, cfg.CompressOnExport);
            });
        }
        if (exporting) ImGui.EndDisabled();
        HelpMarker("Makes one .zip with your lines and recordings. Send it to friends; they import it on their Friends tab.");

        DrawShareStatus();
    }

    private void Export(VoicePack pack, string path, bool compress)
    {
        exporting = true;
        SetStatus("Exporting...", false);
        PackSharing.ExportAsync(pack, path, compress).ContinueWith(t =>
        {
            exporting = false;
            if (t.IsFaulted)
            {
                var msg = t.Exception?.InnerException?.Message ?? "unknown error";
                Plugin.Log.Error(t.Exception!, "Export failed");
                SetStatus($"Export failed: {msg}", true);
                return;
            }

            var r = t.Result;
            SetStatus($"Exported {r.Clips} clips: {Mb(r.ZipBytes)} (recordings were {Mb(r.OriginalBytes)}). Saved to {Path.GetFileName(path)}.", false);
        });
    }

    // ---- Friends tab ------------------------------------------------------------------------

    private void DrawFriends()
    {
        var cfg = plugin.Configuration;

        var hear = cfg.HearFriends;
        if (ImGui.Checkbox("Hear friends' voices", ref hear))
        {
            cfg.HearFriends = hear;
            cfg.Save();
        }
        HelpMarker("When a friend whose pack you've imported is in your party, their character speaks too. Only one voice plays at a time.");

        ImGui.SameLine();
        if (ImGui.Button("Import a pack..."))
        {
            plugin.FileDialogs.OpenFileDialog("Import a friend's voice pack", ".zip", (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path)) return;
                try
                {
                    var who = PackSharing.Import(path, plugin.Speaker);
                    SetStatus($"Imported the voice pack for {who}.", false);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Import failed");
                    SetStatus($"Couldn't import: {ex.Message}", true);
                }
            });
        }

        DrawShareStatus();

        var friends = plugin.Speaker.Friends.Values.OrderBy(p => p.Character, StringComparer.OrdinalIgnoreCase).ToList();
        if (friends.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Muted, "No friends' packs yet. Ask a friend to use \"Export pack for friends\" and send you the .zip.");
            return;
        }

        ImGui.Spacing();
        if (!ImGui.BeginTable("friends", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Jobs");
        ImGui.TableSetupColumn("Mute", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Volume", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        string? toRemove = null;
        foreach (var pack in friends)
        {
            var prefs = cfg.PrefsFor(pack.Character);
            ImGui.PushID(pack.Character);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(pack.Character);
            if (ImGui.IsItemHovered())
            {
                var rec = pack.AllLines.Count(l => l.AudioPath != null);
                ImGui.SetTooltip($"{pack.Name}: {pack.AllLines.Count()} lines, {rec} recorded");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.Join(", ", pack.Jobs.Where(j => j.Value.Count > 0).Select(j => j.Key).OrderBy(j => j)));

            ImGui.TableNextColumn();
            var muted = prefs.Muted;
            if (ImGui.Checkbox("##mute", ref muted))
            {
                prefs.Muted = muted;
                cfg.Save();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var vol = prefs.Volume;
            if (ImGui.SliderInt("##vol", ref vol, 0, 100, "%d%%"))
            {
                prefs.Volume = vol;
                cfg.Save();
            }

            ImGui.TableNextColumn();
            var ctrl = ImGui.GetIO().KeyCtrl;
            if (!ctrl) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Remove"))
                toRemove = pack.Character;
            if (!ctrl) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl to remove this pack.");

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (toRemove != null)
        {
            plugin.Player.Stop();
            PackSharing.RemoveFriend(plugin.Speaker, toRemove);
            SetStatus($"Removed {toRemove}'s pack.", false);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private void SetStatus(string text, bool isError)
    {
        shareStatus = text;
        shareStatusIsError = isError;
    }

    private void DrawShareStatus()
    {
        if (shareStatus == null) return;
        ImGui.PushTextWrapPos();
        ImGui.TextColored(shareStatusIsError ? Bad : Good, shareStatus);
        ImGui.PopTextWrapPos();
    }

    private static string Mb(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";

    private static string SafeName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
