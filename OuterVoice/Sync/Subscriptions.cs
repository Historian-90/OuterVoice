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

    /// <summary>Locked to the character the link first delivered (impersonation guard).</summary>
    public string Character { get; set; } = string.Empty;

    /// <summary>True: install updates automatically. False: wait for you to click Update.</summary>
    public bool AutoUpdate { get; set; } = true;

    public string ETag { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTime LastChecked { get; set; }
    public string Status { get; set; } = "Not checked yet";
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
                await CheckAllAsync();
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
        var direct = DropboxLink.ToDirectDownload(link); // throws with a friendly message if invalid
        if (plugin.Configuration.Subscriptions.Any(s => s.Link == direct.ToString()))
            throw new ArgumentException("You're already subscribed to that link.");

        var sub = new Subscription { Link = direct.ToString() };
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

    public void CheckAll() => _ = CheckAllAsync();

    private async Task CheckAllAsync()
    {
        if (!plugin.Configuration.HearFriends) return;
        foreach (var sub in plugin.Configuration.Subscriptions.ToList())
            await CheckAsync(sub);
    }

    public async Task CheckAsync(Subscription sub)
    {
        await busy.WaitAsync(cts.Token);
        Checking = true;
        var temp = Path.Combine(Path.GetTempPath(), $"OuterVoice-{Guid.NewGuid():N}.zip");
        try
        {
            var result = await SafeDownloader.DownloadAsync(new Uri(sub.Link), sub.ETag, temp, cts.Token);
            sub.LastChecked = DateTime.Now;

            if (result.Outcome == SafeDownloader.Outcome.NotModified || result.Sha256 == sub.Sha256)
            {
                if (result.ETag.Length > 0) sub.ETag = result.ETag;
                SetStatus(sub, "Up to date", false);
                return;
            }

            var firstTime = sub.Character.Length == 0;
            if (firstTime || sub.AutoUpdate)
            {
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
                ? "Couldn't reach Dropbox (will try again later)"
                : ex.Message, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup is best-effort */ }
            Checking = false;
            busy.Release();
            _ = Plugin.Framework.RunOnFrameworkThread(() => plugin.Configuration.Save());
        }
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
