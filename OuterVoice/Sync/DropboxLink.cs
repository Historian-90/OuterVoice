using System;
using System.Linq;

namespace OuterVoice.Sync;

/// <summary>Validates Dropbox share links and turns them into direct-download links.</summary>
public static class DropboxLink
{
    /// <summary>
    /// Accepts a Dropbox file share link (https://www.dropbox.com/scl/fi/... or /s/...) and returns
    /// the same link with dl=1 (direct download). Throws with a friendly message otherwise.
    /// </summary>
    public static Uri ToDirectDownload(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("That doesn't look like a link.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Only secure (https) Dropbox links are allowed.");
        if (!IsShareHost(uri.Host))
            throw new ArgumentException("Only Dropbox links are supported right now (dropbox.com).");

        var path = uri.AbsolutePath;
        if (path.StartsWith("/scl/fo/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/sh/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That's a link to a folder. Share the pack's .zip file itself instead.");
        if (!path.StartsWith("/scl/fi/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That isn't a Dropbox file share link.");

        // Keep every query parameter (Dropbox needs rlkey) except dl, then force dl=1.
        var query = uri.Query.TrimStart('?')
                       .Split('&', StringSplitOptions.RemoveEmptyEntries)
                       .Where(p => !p.StartsWith("dl=", StringComparison.OrdinalIgnoreCase))
                       .Append("dl=1");
        return new UriBuilder(uri) { Query = string.Join("&", query) }.Uri;
    }

    /// <summary>Hosts the share link itself may use.</summary>
    public static bool IsShareHost(string host) =>
        host.Equals("dropbox.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.dropbox.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hosts a download may be redirected to (Dropbox's file servers).</summary>
    public static bool IsAllowedHost(string host) =>
        IsShareHost(host) || host.EndsWith(".dropboxusercontent.com", StringComparison.OrdinalIgnoreCase);
}
