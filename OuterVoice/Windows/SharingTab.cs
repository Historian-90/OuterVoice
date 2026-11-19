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
            "This uses the Dropbox or OneDrive desktop app on this PC (not the website). Pick a folder inside the " +
            "Dropbox or OneDrive folder the app syncs, for example C:\\Users\\you\\OneDrive\\OuterVoice. The plugin keeps " +
            "your pack's zip there, updated every time you save, and the app uploads it for you. Share that zip's " +
            "link with friends once; they add it on their Friends page and get your updates automatically.");
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
            ImGui.TextColored(Warn, "This folder doesn't look like it's inside your Dropbox or OneDrive folder, so the app " +
                                    "won't upload it and friends won't get updates.");

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
                       "1. Find this file in File Explorer (right-click > Share, from the Dropbox or OneDrive app),\n" +
                       "   or on the Dropbox / OneDrive website once the app has uploaded it.\n" +
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

    /// <summary>The friend shown on the right: a character name, or a pending link ("link:..."), or null.</summary>
    private string? selectedFriend;

    private void DrawFriends()
    {
        var cfg = plugin.Configuration;
        var friends = plugin.Speaker.Friends.Values.OrderBy(p => p.Character, StringComparer.OrdinalIgnoreCase).ToList();
        var waiting = cfg.Subscriptions.Where(s => s.Character.Length == 0).ToList();

        // A pending link that just delivered its first pack: follow it to the friend it became.
        if (selectedFriend != null && selectedFriend.StartsWith("link:", StringComparison.Ordinal) &&
            cfg.Subscriptions.FirstOrDefault(s => "link:" + s.Link == selectedFriend && s.Character.Length > 0) is { } arrived)
            selectedFriend = arrived.Character;

        // ---- Left: the list ----
        ImGui.BeginChild("##friendlist", new Vector2(ListWidth, 0), true);

        var hear = cfg.HearFriends;
        if (ImGui.Checkbox("Hear friends' voices", ref hear))
        {
            cfg.HearFriends = hear;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When a friend whose pack you have is in your party, their character speaks too.\nOnly one voice plays at a time.");

        var footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("##friendrows", new Vector2(0, -footer));
        if (friends.Count == 0 && waiting.Count == 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Muted, "No friends' packs yet. Click \"+ Add friend\" below.");
            ImGui.PopTextWrapPos();
        }
        if (friends.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Accent, "FRIENDS");
            foreach (var pack in friends)
            {
                var sub = SubscriptionFor(pack.Character);
                FriendRow(pack.Character, pack.Character, cfg.PrefsFor(pack.Character).Muted, sub);
            }
        }
        if (waiting.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Accent, "WAITING FOR FIRST DOWNLOAD");
            foreach (var sub in waiting)
                FriendRow("link:" + sub.Link, $"{sub.Service} link", false, sub);
        }
        ImGui.EndChild();

        ImGui.Separator();
        var half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        if (ImGui.Button("+ Add friend", new Vector2(half, 0)))
            ImGui.OpenPopup("##addfriend");
        DrawAddFriendPopup();

        ImGui.SameLine();
        var subs = plugin.Subscriptions;
        if (subs.Remaining > 0)
        {
            ImGui.BeginDisabled();
            ImGui.Button($"{subs.RoundTotal - subs.Remaining + 1} of {subs.RoundTotal}{Dots()}", new Vector2(half, 0));
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("Check now", new Vector2(half, 0)))
        {
            subs.CheckAllNow();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Check every friend's link for a newer pack now.");

        var limit = cfg.LimitFriendLoudness;
        if (ImGui.Checkbox("Tame loud clips", ref limit))
        {
            cfg.LimitFriendLoudness = limit;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs friends' clips through a limiter so nothing is ear-blastingly loud.\nFriends' clips over 30 seconds are always skipped.");

        ImGui.EndChild();

        // ---- Right: the selected friend ----
        ImGui.SameLine();
        ImGui.BeginChild("##frienddetail", Vector2.Zero, true);
        if (subs.ManualResult != null &&
            (subs.ManualResultIsError || DateTime.UtcNow - subs.ManualResultAt < TimeSpan.FromSeconds(20)))
            ImGui.TextColored(subs.ManualResultIsError ? Bad : Good, subs.ManualResult);
        DrawShareStatus();

        if (selectedFriend != null && selectedFriend.StartsWith("link:", StringComparison.Ordinal) &&
            waiting.FirstOrDefault(s => "link:" + s.Link == selectedFriend) is { } pending)
            DrawPendingLink(pending);
        else if (selectedFriend != null && friends.FirstOrDefault(f => f.Character == selectedFriend) is { } friend)
            DrawFriendDetail(friend);
        else
            DrawFriendsWelcome();
        ImGui.EndChild();
    }

    private Sync.Subscription? SubscriptionFor(string character) =>
        plugin.Configuration.Subscriptions.FirstOrDefault(s => string.Equals(s.Character, character, StringComparison.OrdinalIgnoreCase));

    /// <summary>One row: name, and a dot for the link's state (green ok, yellow update/busy, red error).</summary>
    private void FriendRow(string key, string label, bool muted, Sync.Subscription? sub)
    {
        if (ImGui.Selectable($"  {label}{(muted ? " (muted)" : "")}##f_{key}", selectedFriend == key))
            selectedFriend = key;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = new Vector2(max.X - 9, (min.Y + max.Y) / 2);
        var color = sub == null ? Muted
            : sub.StatusIsError ? Bad
            : sub.Activity != null || sub.UpdateAvailable ? Warn
            : Good;
        ImGui.GetWindowDrawList().AddCircleFilled(center, 4f, ImGui.GetColorU32(color));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(sub == null ? "Imported from a file (no link)" : $"{sub.Service}: {sub.Activity ?? sub.Status}");
    }

    private void DrawAddFriendPopup()
    {
        if (!ImGui.BeginPopup("##addfriend")) return;

        ImGui.TextUnformatted("Paste the link your friend sent you:");
        ImGui.SetNextItemWidth(360);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##sublink", "Dropbox or OneDrive link...", ref subscribeLink, 512);
        ImGui.SameLine();
        if (ImGui.Button("Subscribe") && subscribeLink.Trim().Length > 0)
        {
            try
            {
                var sub = plugin.Subscriptions.Add(subscribeLink);
                selectedFriend = "link:" + sub.Link;
                subscribeLink = string.Empty;
                SetStatus("Subscribed. The download starts in a moment.", false);
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, true);
            }
        }
        ImGui.TextColored(Muted, "The pack updates itself when your friend publishes a new version.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Import a .zip file instead..."))
        {
            ImGui.CloseCurrentPopup();
            plugin.FileDialogs.OpenFileDialog("Import a friend's voice pack", ".zip", (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path)) return;
                try
                {
                    var who = PackSharing.Import(path, plugin.Speaker);
                    selectedFriend = who;
                    SetStatus($"Imported the voice pack for {who}.", false);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Import failed");
                    SetStatus($"Couldn't import: {ex.Message}", true);
                }
            });
        }
        ImGui.SameLine();
        ImGui.TextColored(Muted, "A one-off copy: it won't update by itself.");
        ImGui.EndPopup();
    }

    private void DrawFriendsWelcome()
    {
        ImGui.PushTextWrapPos();
        ImGui.TextUnformatted("Friends' voice packs");
        ImGui.Spacing();
        ImGui.TextColored(Muted, "When a friend whose pack you have is in your party, their character speaks with their own lines. " +
                                 "Everything happens on your machine: nothing is sent while you play.");
        ImGui.Spacing();
        ImGui.TextColored(Muted, "+ Add friend: paste the Dropbox or OneDrive link your friend shares (it updates by itself), " +
                                 "or import a .zip they sent you.");
        ImGui.TextColored(Muted, "Pick a friend on the left for their volume, mute and update settings.");
        ImGui.Spacing();
        ImGui.TextColored(Muted, "Links are checked when you log in and every 30 minutes, and only downloaded when they changed. " +
                                 "Only Dropbox and personal OneDrive links are accepted, and a link can never switch to voicing " +
                                 "a different character than the one it first delivered.");
        ImGui.PopTextWrapPos();
    }

    private void DrawPendingLink(Sync.Subscription sub)
    {
        ImGui.SetWindowFontScale(1.3f);
        ImGui.TextUnformatted($"New {sub.Service} link");
        ImGui.SetWindowFontScale(1f);
        ImGui.TextColored(Muted, "Waiting for the first download. The character's name shows once it arrives.");
        ImGui.Spacing();
        DrawSubscriptionStatus(sub);
        ImGui.Spacing();
        if (ImGui.Button("Remove this link"))
        {
            plugin.Subscriptions.Remove(sub);
            selectedFriend = null;
        }
    }

    private void DrawFriendDetail(VoicePack pack)
    {
        var cfg = plugin.Configuration;
        var prefs = cfg.PrefsFor(pack.Character);

        ImGui.SetWindowFontScale(1.3f);
        ImGui.TextUnformatted(pack.Character);
        ImGui.SetWindowFontScale(1f);
        var lines = pack.AllLines.Count();
        var rec = pack.AllLines.Count(l => l.AudioPath != null);
        ImGui.TextColored(Muted, $"{pack.Name}: {lines} lines, {rec} recorded");
        var jobs = pack.Jobs.Where(j => j.Value.Count > 0).Select(j => j.Key).OrderBy(j => j).ToList();
        if (jobs.Count > 0)
            ImGui.TextColored(Muted, $"Jobs: {string.Join(", ", jobs)}");

        ImGui.Spacing();
        ImGui.TextColored(Accent, "LISTENING");
        var muted = prefs.Muted;
        if (ImGui.Checkbox("Mute this friend", ref muted))
        {
            prefs.Muted = muted;
            cfg.Save();
        }
        ImGui.SetNextItemWidth(220);
        var vol = prefs.Volume;
        if (ImGui.SliderInt("Volume", ref vol, 0, 100, "%d%%"))
        {
            prefs.Volume = vol;
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(Accent, "UPDATES");
        var sub = SubscriptionFor(pack.Character);
        if (sub == null)
        {
            ImGui.TextColored(Muted, "Imported from a file, so it doesn't update by itself. Ask your friend for their link.");
        }
        else
        {
            var auto = sub.AutoUpdate;
            if (ImGui.Checkbox("Install updates automatically", ref auto))
            {
                sub.AutoUpdate = auto;
                cfg.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Off (ask first): new versions wait for you to click Update.");
            DrawSubscriptionStatus(sub);
        }

        ImGui.Spacing();
        ImGui.Separator();
        var ctrl = ImGui.GetIO().KeyCtrl;
        if (!ctrl) ImGui.BeginDisabled();
        var remove = ImGui.Button("Remove this friend's pack");
        if (!ctrl) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl to remove this pack (and unsubscribe from its link).");
        if (remove)
        {
            var who = pack.Character;
            plugin.Player.Stop();
            foreach (var s in cfg.Subscriptions.Where(s => string.Equals(s.Character, who, StringComparison.OrdinalIgnoreCase)).ToList())
                plugin.Subscriptions.Remove(s); // unsubscribing removes the pack too
            PackSharing.RemoveFriend(plugin.Speaker, who);
            selectedFriend = null;
            SetStatus($"Removed {who}'s pack.", false);
        }
    }

    /// <summary>Where a link stands: progress while busy, an Update button, or its last status.</summary>
    private void DrawSubscriptionStatus(Sync.Subscription sub)
    {
        if (sub.UpdateAvailable)
        {
            ImGui.TextColored(Warn, "A new version is ready.");
            ImGui.SameLine();
            if (ImGui.Button("Update"))
                _ = plugin.Subscriptions.ApplyPendingAsync(sub);
        }
        else if (sub.Activity is { } activity)
        {
            if (sub.Progress >= 0)
                ImGui.ProgressBar(sub.Progress, new Vector2(260, 0), activity);
            else
                ImGui.TextColored(Good, activity + Dots());
        }
        else
        {
            ImGui.TextColored(sub.StatusIsError ? Bad : Muted, $"{sub.Service}: {sub.Status}");
            if (sub.LastChecked != default)
                ImGui.TextColored(Muted, $"Last checked {sub.LastChecked:g}");
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private DateTime shareStatusAt;

    /// <summary>Animated "..." so a waiting state visibly ticks.</summary>
    private static string Dots() => new('.', (int)(ImGui.GetTime() * 3 % 4));

    private void SetStatus(string text, bool isError)
    {
        shareStatus = text;
        shareStatusIsError = isError;
        shareStatusAt = DateTime.UtcNow;
    }

    private void DrawShareStatus()
    {
        if (shareStatus == null) return;
        // Success notes fade after a few seconds; errors stay until the next action.
        if (!shareStatusIsError && DateTime.UtcNow - shareStatusAt > TimeSpan.FromSeconds(8))
        {
            shareStatus = null;
            return;
        }
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
