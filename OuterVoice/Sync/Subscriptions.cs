using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OuterVoice.Sync;

/// <summary>A friend's pack you follow through their Dropbox link.</summary>
[Serializable]
public class Subscription
{
    public string Link { get; set; } = string.Empty;

    public bool IsOneDrive => Uri.TryCreate(Link, UriKind.Absolute, out var u) && OneDriveLink.IsShareLink(u);
    public string Service => IsOneDrive ? "OneDrive" : "Dropbox";

    /// <summary>Locked to the character the link first delivered (impersonation guard).</summary>
    public string Character { get; set; } = string.Empty;

    /// <summary>True: install updates automatically. False: wait for you to click Update.</summary>
    public bool AutoUpdate { get; set; } = true;

    public string ETag { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTime LastChecked { get; set; }
    public string Status { get; set; } = "Not checked yet";

    /// <summary>What's happening right now (not saved): "Checking...", "Downloading...", or null when idle.</summary>
    [Newtonsoft.Json.JsonIgnore] public string? Activity { get; set; }

    /// <summary>Download progress 0-1, or -1 when the size isn't known (not saved).</summary>
    [Newtonsoft.Json.JsonIgnore] public float Progress { get; set; } = -1;
    public bool StatusIsError { get; set; }

    /// <summary>A downloaded update waiting for your OK (Ask first mode).</summary>
    public string PendingFile { get; set; } = string.Empty;

    public bool UpdateAvailable => PendingFile.Length > 0 && File.Exists(PendingFile);
}

/// <summary>
/// Checks subscribed Dropbox links at startup, at login and every 30 minutes, downloading only when
/// the file actually changed. Network work happens in the background; installing a pack happens on
/// the game's main thread.
/// </summary>
public sealed class SubscriptionManager : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly Plugin plugin;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim busy = new(1, 1);

    public SubscriptionManager(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.ClientState.Login += OnLogin;
        _ = Task.Run(LoopAsync);
    }

    public void Dispose()
    {
        Plugin.ClientState.Login -= OnLogin;
        cts.Cancel();
    }

    public bool Checking { get; private set; }

    private static string PendingDir => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "pending");

    private void OnLogin() => CheckAll();

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cts.Token); // let the game settle after load
            while (!cts.IsCancellationRequested)
            {
                await CheckAllAsync(manual: false);
                await Task.Delay(Interval, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Adds a link (validated) and checks it right away.</summary>
    public Subscription Add(string link)
    {
        // Validate and normalize (throws with a friendly message if invalid).
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var parsed))
            throw new ArgumentException("That doesn't look like a link.");
        var stored = OneDriveLink.IsShareLink(parsed)
            ? OneDriveLink.Validate(link).ToString()           // resolved fresh on every check
            : DropboxLink.ToDirectDownload(link).ToString();   // Dropbox links are already direct
        if (plugin.Configuration.Subscriptions.Any(s => s.Link == stored))
            throw new ArgumentException("You're already subscribed to that link.");

        var sub = new Subscription { Link = stored };
        plugin.Configuration.Subscriptions.Add(sub);
        plugin.Configuration.Save();
        _ = CheckAsync(sub);
        return sub;
    }

    public void Remove(Subscription sub)
    {
        plugin.Configuration.Subscriptions.Remove(sub);
        DeletePending(sub);
        if (sub.Character.Length > 0 && !plugin.Configuration.Subscriptions.Any(s => s.Character == sub.Character))
        {
            plugin.Player.Stop();
            PackSharing.RemoveFriend(plugin.Speaker, sub.Character);
        }
        plugin.Configuration.Save();
    }

    public void CheckAll() => _ = CheckAllAsync(manual: false);

    /// <summary>The "Check now" button: like CheckAll, but reports a summary when it's done.</summary>
    public void CheckAllNow() => _ = CheckAllAsync(manual: true);

    /// <summary>Packs left to check in the current round (for the progress line), 0 when idle.</summary>
    public int Remaining { get; private set; }
    public int RoundTotal { get; private set; }

    /// <summary>Result of the last manual check, for the Friends tab (and when it finished).</summary>
    public string? ManualResult { get; private set; }
    public bool ManualResultIsError { get; private set; }
    public DateTime ManualResultAt { get; private set; }

    private async Task CheckAllAsync(bool manual)
    {
        if (!plugin.Configuration.HearFriends)
        {
            if (manual) Report("Friends' voices are turned off, so nothing was checked.", true);
            return;
        }

        if (Remaining > 0) return; // a round is already running
        var subs = plugin.Configuration.Subscriptions.ToList();
        if (subs.Count == 0)
        {
            if (manual) Report("No friends' links to check yet.", false);
            return;
        }

        if (manual) ManualResult = null;
        RoundTotal = subs.Count;
        Remaining = subs.Count;
        foreach (var s in subs) s.Activity = "Waiting to check";
        try
        {
            foreach (var sub in subs)
            {
                await CheckAsync(sub);
                Remaining--;
            }
        }
        finally
        {
            Remaining = 0;
            foreach (var s in subs) s.Activity = null;
        }

        if (!manual) return;
        var errors = subs.Count(s => s.StatusIsError);
        var updated = subs.Count(s => s.Status.StartsWith("Updated") || s.Status == "Installed");
        var waiting = subs.Count(s => s.UpdateAvailable);
        var parts = new System.Collections.Generic.List<string>();
        if (updated > 0) parts.Add($"{updated} updated");
        if (waiting > 0) parts.Add($"{waiting} waiting for you to click Update");
        if (errors > 0) parts.Add($"{errors} couldn't be checked (see the list)");
        Report(parts.Count == 0
                   ? $"Checked {subs.Count} pack{(subs.Count == 1 ? "" : "s")} at {DateTime.Now:t}: everything is up to date."
                   : $"Checked {subs.Count} pack{(subs.Count == 1 ? "" : "s")} at {DateTime.Now:t}: {string.Join(", ", parts)}.",
               errors > 0);
    }

    private void Report(string text, bool isError)
    {
        ManualResult = text;
        ManualResultIsError = isError;
        ManualResultAt = DateTime.UtcNow;
    }

    public async Task CheckAsync(Subscription sub)
    {
        await busy.WaitAsync(cts.Token);
        Checking = true;
        sub.Activity = $"Checking {sub.Service}";
        sub.Progress = -1;
        var temp = Path.Combine(Path.GetTempPath(), $"OuterVoice-{Guid.NewGuid():N}.zip");
        try
        {
            SafeDownloader.Result result;
            if (sub.IsOneDrive)
            {
                // OneDrive: look up the file first; its content tag tells us if it changed,
                // so an unchanged pack is never downloaded.
                var resolved = await OneDriveLink.ResolveAsync(new Uri(sub.Link), cts.Token);
                var tag = "ctag:" + resolved.ContentTag;
                if (resolved.ContentTag.Length > 0 && tag == sub.ETag && sub.Character.Length > 0)
                {
                    sub.LastChecked = DateTime.Now;
                    SetStatus(sub, $"Up to date (checked {DateTime.Now:t})", false);
                    return;
                }

                result = await SafeDownloader.DownloadAsync(resolved.DownloadUrl, null, temp, cts.Token,
                                                            OneDriveLink.IsAllowedHost, "OneDrive",
                                                            (done, total) => ReportDownload(sub, done, total));
                result = result with { ETag = tag };
            }
            else
            {
                result = await SafeDownloader.DownloadAsync(new Uri(sub.Link), sub.ETag, temp, cts.Token,
                                                            DropboxLink.IsAllowedHost, "Dropbox",
                                                            (done, total) => ReportDownload(sub, done, total));
            }
            sub.LastChecked = DateTime.Now;

            if (result.Outcome == SafeDownloader.Outcome.NotModified || result.Sha256 == sub.Sha256)
            {
                if (result.ETag.Length > 0) sub.ETag = result.ETag;
                SetStatus(sub, $"Up to date (checked {DateTime.Now:t})", false);
                return;
            }

            var firstTime = sub.Character.Length == 0;
            if (firstTime || sub.AutoUpdate)
            {
                sub.Activity = "Installing";
                sub.Progress = -1;
                await InstallOnMainThread(sub, temp, result.Sha256, result.ETag);
            }
            else
            {
                // Ask first: keep the download and let the player decide.
                Directory.CreateDirectory(PendingDir);
                DeletePending(sub);
                var pending = Path.Combine(PendingDir, $"{result.Sha256}.zip");
                File.Move(temp, pending, overwrite: true);
                sub.PendingFile = pending;
                sub.ETag = result.ETag;
                SetStatus(sub, "Update available", false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Couldn't check {sub.Character} ({sub.Link})");
            SetStatus(sub, ex is System.Net.Http.HttpRequestException
                ? $"Couldn't reach {sub.Service} (will try again later)"
                : ex.Message, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup is best-effort */ }
            Checking = false;
            sub.Activity = null;
            sub.Progress = -1;
            busy.Release();
            _ = Plugin.Framework.RunOnFrameworkThread(() => plugin.Configuration.Save());
        }
    }

    private static void ReportDownload(Subscription sub, long done, long? total)
    {
        sub.Progress = total is > 0 ? Math.Clamp((float)done / total.Value, 0f, 1f) : -1;
        sub.Activity = total is > 0
            ? $"Downloading {done / 1048576.0:0.0} of {total.Value / 1048576.0:0.0} MB"
            : $"Downloading {done / 1048576.0:0.0} MB";
    }

    /// <summary>Installs a waiting update (Ask first mode).</summary>
    public async Task ApplyPendingAsync(Subscription sub)
    {
        if (!sub.UpdateAvailable) return;
        var file = sub.PendingFile;
        var sha = Path.GetFileNameWithoutExtension(file);
        try
        {
            await InstallOnMainThread(sub, file, sha, sub.ETag);
        }
        catch (Exception ex)
        {
            SetStatus(sub, ex.Message, true);
        }
        finally
        {
            DeletePending(sub);
            _ = Plugin.Framework.RunOnFrameworkThread(() => plugin.Configuration.Save());
        }
    }

    private async Task InstallOnMainThread(Subscription sub, string zip, string sha, string etag)
    {
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            plugin.Player.Stop(); // release any of this friend's clips that might be playing
            var character = PackSharing.Import(zip, plugin.Speaker,
                                               expectedCharacter: sub.Character.Length > 0 ? sub.Character : null);
            var first = sub.Character.Length == 0;
            sub.Character = character;
            sub.Sha256 = sha;
            sub.ETag = etag;
            SetStatus(sub, first ? "Installed" : $"Updated {DateTime.Now:t}", false);
        });
    }

    private static void SetStatus(Subscription sub, string status, bool isError)
    {
        sub.Status = status;
        sub.StatusIsError = isError;
    }

    private static void DeletePending(Subscription sub)
    {
        try { if (sub.PendingFile.Length > 0 && File.Exists(sub.PendingFile)) File.Delete(sub.PendingFile); } catch { }
        sub.PendingFile = string.Empty;
    }
}
