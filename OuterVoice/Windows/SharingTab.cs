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
    private string subscribeLink = string.Empty;

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
        DrawPublish(pack);
    }

    // ---- Publish to Dropbox --------------------------------------------------------------------

    private void DrawPublish(VoicePack pack)
    {
        var cfg = plugin.Configuration;
        Section("Share through Dropbox or OneDrive");

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted,
            "Pick a folder inside your Dropbox or OneDrive folder. The plugin keeps your pack's zip there, updated every time " +
            "you save. Share that zip's link with friends once; they subscribe on their Friends tab and get your " +
            "updates automatically.");
        ImGui.PopTextWrapPos();

        var folder = string.IsNullOrWhiteSpace(cfg.PublishFolder) ? "(no folder chosen)" : cfg.PublishFolder;
        ImGui.TextUnformatted(folder);
        ImGui.SameLine();
        if (ImGui.SmallButton("Browse...##publish"))
        {
            plugin.FileDialogs.OpenFolderDialog("Choose a folder inside Dropbox or OneDrive", (ok, path) =>
            {
                if (!ok) return;
                cfg.PublishFolder = path;
                cfg.Save();
            }, Directory.Exists(cfg.PublishFolder) ? cfg.PublishFolder : null, false);
        }

        if (string.IsNullOrWhiteSpace(cfg.PublishFolder))
            return;

        if (!cfg.PublishFolder.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) &&
            !cfg.PublishFolder.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            ImGui.TextColored(Warn, "This folder doesn't look like it's inside Dropbox or OneDrive, so it won't sync for friends.");

        var onSave = cfg.PublishOnSave;
        if (ImGui.Checkbox("Publish automatically when I save", ref onSave))
        {
            cfg.PublishOnSave = onSave;
            cfg.Save();
        }

        ImGui.SameLine();
        if (plugin.Publisher.Running) ImGui.BeginDisabled();
        if (ImGui.Button("Publish now"))
        {
            if (plugin.PackDirty) plugin.SavePack(); // saving also publishes when auto-publish is on
            if (!plugin.Publisher.Running) plugin.Publisher.Publish();
        }
        if (plugin.Publisher.Running) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Open folder"))
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{cfg.PublishFolder}\""); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't open folder"); }
        }

        if (plugin.Publisher.Status.Length > 0)
            ImGui.TextColored(plugin.Publisher.StatusIsError ? Bad : Good, plugin.Publisher.Status);

        if (plugin.Publisher.TargetPath(pack) is { } target)
        {
            ImGui.TextColored(Muted, $"File: {Path.GetFileName(target)}");
            InfoMarker("To share it:\n" +
                       "1. Find this file in Dropbox or OneDrive (File Explorer right-click works too).\n" +
                       "   Share the FILE, not the folder.\n" +
                       "2. Share -> \"Anyone with the link\" can view -> Copy link.\n" +
                       "3. Send the link to friends once. The file name never changes, so the link keeps working.\n\n" +
                       "Privacy: anyone with the link can download your pack, and the share page may show your\n" +
                       "account name to people who open it in a browser. You can turn the link off at any time.");
        }
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

        var limit = cfg.LimitFriendLoudness;
        if (ImGui.Checkbox("Tame loud clips", ref limit))
        {
            cfg.LimitFriendLoudness = limit;
            cfg.Save();
        }
        HelpMarker("Runs friends' clips through a limiter so nothing is ear-blastingly loud. Friends' clips over 30 seconds are always skipped.");

        // Subscribe to a Dropbox link
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("##sublink", "Paste a friend's Dropbox or OneDrive link...", ref subscribeLink, 512);
        ImGui.SameLine();
        if (ImGui.Button("Subscribe") && subscribeLink.Trim().Length > 0)
        {
            try
            {
                plugin.Subscriptions.Add(subscribeLink);
                subscribeLink = string.Empty;
                SetStatus("Subscribed. Downloading their pack...", false);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, true);
            }
        }
        InfoMarker("Your friend shares their pack's zip from Dropbox or OneDrive (Share -> Copy link) and sends you the link.\n" +
                   "The plugin checks it when you log in and every 30 minutes, and only downloads when it changed.\n" +
                   "Only Dropbox and personal OneDrive links are accepted, downloads must be zip packs, and a link\n" +
                   "can never switch to voicing a different character than the one it first delivered.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Check now"))
            plugin.Subscriptions.CheckAll();

        DrawShareStatus();

        var friends = plugin.Speaker.Friends.Values.OrderBy(p => p.Character, StringComparer.OrdinalIgnoreCase).ToList();
        var waiting = cfg.Subscriptions.Where(s => s.Character.Length == 0).ToList();
        if (friends.Count == 0 && waiting.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Muted, "No friends' packs yet. Subscribe to a friend's Dropbox or OneDrive link, or import a .zip they sent you.");
            return;
        }

        // Links that haven't delivered a pack yet
        foreach (var sub in waiting)
        {
            ImGui.PushID(sub.Link);
            ImGui.TextColored(sub.StatusIsError ? Bad : Muted, $"Pending link: {sub.Status}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
                plugin.Subscriptions.Remove(sub);
            ImGui.PopID();
        }

        ImGui.Spacing();
        if (!ImGui.BeginTable("friends", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Character");
        ImGui.TableSetupColumn("Jobs");
        ImGui.TableSetupColumn("Updates");
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
            var sub = cfg.Subscriptions.FirstOrDefault(s => string.Equals(s.Character, pack.Character, StringComparison.OrdinalIgnoreCase));
            if (sub == null)
            {
                ImGui.TextColored(Muted, "Imported file");
            }
            else
            {
                var auto = sub.AutoUpdate;
                if (ImGui.Checkbox("Auto##auto", ref auto))
                {
                    sub.AutoUpdate = auto;
                    cfg.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("On: updates install automatically.\nOff (ask first): you'll see an Update button and choose when.");
                ImGui.SameLine();
                if (sub.UpdateAvailable)
                {
                    if (ImGui.SmallButton("Update"))
                        _ = plugin.Subscriptions.ApplyPendingAsync(sub);
                }
                else
                {
                    ImGui.TextColored(sub.StatusIsError ? Bad : Muted, $"{sub.Service}: {sub.Status}");
                    if (ImGui.IsItemHovered() && sub.LastChecked != default)
                        ImGui.SetTooltip($"Last checked {sub.LastChecked:g}");
                }
            }

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
                ImGui.SetTooltip("Hold Ctrl to remove this pack (and unsubscribe from its link).");

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (toRemove != null)
        {
            plugin.Player.Stop();
            foreach (var sub in cfg.Subscriptions.Where(s => string.Equals(s.Character, toRemove, StringComparison.OrdinalIgnoreCase)).ToList())
                plugin.Subscriptions.Remove(sub); // unsubscribing removes the pack too
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
