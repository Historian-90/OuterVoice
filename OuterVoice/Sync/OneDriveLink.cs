using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OuterVoice.Sync;

/// <summary>
/// Resolves personal OneDrive share links (1drv.ms / onedrive.live.com) to a short-lived direct
/// download URL, anonymously (no sign-in), the same way OneDrive's own web page opens a shared file:
///  - Newer accounts (link expands to "?redeem=..."): get an anonymous session token, then ask the
///    personal-content API for the shared item.
///  - Classic accounts (link expands to "?cid=...&amp;resid=...&amp;authkey=..."): ask the classic API.
///  - Otherwise: the classic "shares" API with the encoded link.
/// Also returns the file's content tag, so unchanged files are never re-downloaded.
/// </summary>
public static class OneDriveLink
{
    private const string BadgerTokenUrl = "https://api-badgerp.svc.ms/v1.0/token";
    private const string ClassicApi = "https://api.onedrive.com/v1.0";
    private const string PersonalApi = "https://my.microsoftpersonalcontent.com/_api/v2.0";
    // The app identity OneDrive's web client uses for anonymous visitors.
    private const string BadgerAppId = "1141147648";
    private const string BadgerAppUuid = "5cbed6ac-a083-4e14-b191-b4ba07653de2";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OuterVoice/1.0");
        return client;
    }

    public static bool IsShareLink(Uri uri) =>
        uri.Host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hosts the link resolution and the final download may touch.</summary>
    public static bool IsAllowedHost(string host)
    {
        host = host.ToLowerInvariant();
        return host is "1drv.ms" or "onedrive.live.com" or "api.onedrive.com" or "api-badgerp.svc.ms"
                   or "my.microsoftpersonalcontent.com"
               || host.EndsWith(".1drv.com")
               || host.EndsWith(".livefilestore.com")
               || host.EndsWith(".storage.live.com")
               || host.EndsWith(".microsoftpersonalcontent.com");
    }

    /// <summary>Basic validation when a link is first added.</summary>
    public static Uri Validate(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("That doesn't look like a link.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Only secure (https) OneDrive links are allowed.");
        if (!IsShareLink(uri))
            throw new ArgumentException("That isn't a personal OneDrive share link (1drv.ms or onedrive.live.com).");
        if (uri.AbsolutePath.StartsWith("/f/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That's a link to a folder. Share the pack's .zip file itself instead.");
        return uri;
    }

    public sealed record Resolved(Uri DownloadUrl, string ContentTag, string Name);

    public static async Task<Resolved> ResolveAsync(Uri shareLink, CancellationToken token)
    {
        var expanded = shareLink.Host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase)
            ? shareLink
            : await ExpandAsync(shareLink, token);

        var query = System.Web.HttpUtility.ParseQueryString(expanded.Query);
        var redeem = query["redeem"];
        var resid = query["resid"];
        if (string.IsNullOrEmpty(resid) && query["id"] is { } id && id.Contains('!'))
            resid = id;
        var authkey = query["authkey"];
        var cid = query["cid"];
        if (string.IsNullOrEmpty(cid) && resid?.Split('!') is [var c, { Length: > 0 }])
            cid = c;

        JsonElement item;
        if (!string.IsNullOrEmpty(redeem))
        {
            var badger = await BadgerTokenAsync(token);
            item = await GetJsonAsync(new Uri($"{PersonalApi}/shares/u!{Uri.EscapeDataString(redeem)}/driveitem"),
                                      token, ("Authorization", "Badger " + badger), ("Prefer", "autoredeem"));
        }
        else if (!string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(resid))
        {
            var url = $"{ClassicApi}/drives/{Uri.EscapeDataString(cid)}/items/{Uri.EscapeDataString(resid)}";
            if (!string.IsNullOrEmpty(authkey)) url += "?authkey=" + Uri.EscapeDataString(authkey);
            item = await GetJsonAsync(new Uri(url), token);
        }
        else
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(shareLink.ToString()))
                                 .TrimEnd('=').Replace('/', '_').Replace('+', '-');
            item = await GetJsonAsync(new Uri($"{ClassicApi}/shares/u!{encoded}/root"), token);
        }

        if (item.TryGetProperty("folder", out _))
            throw new InvalidOperationException("That link points to a folder. Share the pack's .zip file itself.");
        if (!item.TryGetProperty("@content.downloadUrl", out var dl) || dl.GetString() is not { Length: > 0 } download)
            throw new InvalidOperationException("OneDrive didn't give a download link. Is it shared with \"Anyone with the link\"?");

        var tag = item.TryGetProperty("cTag", out var ctag) ? ctag.GetString() ?? string.Empty
                : item.TryGetProperty("eTag", out var etag) ? etag.GetString() ?? string.Empty
                : string.Empty;
        var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        return new Resolved(new Uri(download), tag, name);
    }

    /// <summary>Follows a 1drv.ms short link to onedrive.live.com, refusing any other destination.</summary>
    private static async Task<Uri> ExpandAsync(Uri link, CancellationToken token)
    {
        var current = link;
        for (var hop = 0; hop < 6; hop++)
        {
            Check(current);
            using var response = await Http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Host.Contains("login.", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("That link asks for sign-in. Share it as \"Anyone with the link\" instead.");
                current = next;
                if (current.Host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase))
                    return current;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("The link no longer works (the file was removed or unshared).");
            return current;
        }
        throw new InvalidOperationException("Too many redirects.");
    }

    private static async Task<string> BadgerTokenAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BadgerTokenUrl)
        {
            Content = new StringContent($"{{\"appId\":\"{BadgerAppUuid}\"}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("AppId", BadgerAppId);
        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneDrive refused an anonymous session ({(int)response.StatusCode}).");
        using var doc = JsonDocument.Parse(await ReadLimitedAsync(response, token));
        return doc.RootElement.TryGetProperty("token", out var t) && t.GetString() is { Length: > 0 } s
            ? s
            : throw new InvalidOperationException("OneDrive returned an empty anonymous session.");
    }

    private static async Task<JsonElement> GetJsonAsync(Uri url, CancellationToken token, params (string Name, string Value)[] headers)
    {
        Check(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await Http.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new InvalidOperationException("The link no longer works (the file was removed or unshared).");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("OneDrive wants a sign-in for that file. Share it as \"Anyone with the link\".");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneDrive returned an error ({(int)response.StatusCode}).");
        using var doc = JsonDocument.Parse(await ReadLimitedAsync(response, token));
        return doc.RootElement.Clone();
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Metadata replies are small; refuse anything huge.
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[256 * 1024];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total), token)) > 0)
        {
            total += read;
            if (total == buffer.Length)
                throw new InvalidOperationException("OneDrive sent an unexpectedly large reply.");
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static void Check(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(url.Host))
            throw new InvalidOperationException($"Refused to contact {url.Host}: not a OneDrive address.");
    }
}
