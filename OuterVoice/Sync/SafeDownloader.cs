using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OuterVoice.Sync;

/// <summary>
/// Downloads a pack zip with every guard on: https only, Dropbox hosts only (checked on every
/// redirect, so a link can't bounce to your home network), a size cap while streaming, and a
/// check that what arrived is actually a zip.
/// </summary>
public static class SafeDownloader
{
    public enum Outcome { NotModified, Downloaded }

    public sealed record Result(Outcome Outcome, string? FilePath, string ETag, string Sha256);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OuterVoice/1.0");
        return client;
    }

    public static async Task<Result> DownloadAsync(Uri url, string? etag, string destFile, CancellationToken token)
    {
        var current = url;
        for (var hop = 0; hop < 6; hop++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || !DropboxLink.IsAllowedHost(current.Host))
                throw new InvalidOperationException($"Refused to download from {current.Host}: not a Dropbox address.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (!string.IsNullOrEmpty(etag))
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new InvalidOperationException("Dropbox sent a broken redirect.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new Result(Outcome.NotModified, null, etag ?? string.Empty, string.Empty);

            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
                throw new InvalidOperationException("The link no longer works (the file was removed or unshared).");
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > PackSharing.MaxZipBytes)
                throw new InvalidOperationException("That pack is too large (over 200 MB).");

            var newEtag = response.Headers.ETag?.ToString() ?? string.Empty;
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(destFile))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > PackSharing.MaxZipBytes)
                        throw new InvalidOperationException("That pack is too large (over 200 MB).");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }

            // A revoked or wrong link often returns a web page instead of the file.
            await using (var check = File.OpenRead(destFile))
            {
                var magic = new byte[2];
                if (await check.ReadAsync(magic, token) < 2 || magic[0] != (byte)'P' || magic[1] != (byte)'K')
                    throw new InvalidOperationException("The link didn't return a .zip file. Is it shared, and is it the pack's zip?");
            }

            string sha;
            await using (var hashStream = File.OpenRead(destFile))
                sha = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, token));

            return new Result(Outcome.Downloaded, destFile, newEtag, sha);
        }

        throw new InvalidOperationException("Too many redirects.");
    }
}
