using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OuterVoice;

/// <summary>
/// Manual sharing: export your pack as a .zip, import friends' .zip packs.
/// Imported packs are stored in the plugin's own config folder, one folder per character.
/// </summary>
public static class PackSharing
{
    public const long MaxZipBytes = 200L * 1024 * 1024;
    private const long MaxUnpackedBytes = 400L * 1024 * 1024;   // zip-bomb guard
    private const long MaxJsonBytes = 2L * 1024 * 1024;
    private const int MaxEntries = 5000;
    private const int ShareBitrate = 48000; // mono speech at 48 kbps is clear and ~3x smaller
    private static readonly string[] AllowedExtensions = [".json", ".mp3", ".wav", ".png", ".jpg", ".jpeg"];

    public static string FriendsRoot =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "packs");

    // ---- Loading installed friend packs --------------------------------------------------

    public static void LoadFriends(Speaker speaker)
    {
        speaker.Friends.Clear();
        if (!Directory.Exists(FriendsRoot))
            return;

        foreach (var dir in Directory.GetDirectories(FriendsRoot))
        {
            try
            {
                var pack = VoicePack.Load(dir);
                if (!string.IsNullOrWhiteSpace(pack.Character))
                    speaker.Friends[pack.Character] = pack;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Skipping unreadable friend pack in {dir}");
            }
        }
    }

    public static void RemoveFriend(Speaker speaker, string characterId)
    {
        if (!speaker.Friends.TryGetValue(characterId, out var pack))
            return;
        speaker.Friends.Remove(characterId);
        if (Directory.Exists(pack.Folder) && pack.Folder.StartsWith(FriendsRoot, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(pack.Folder, recursive: true);
    }

    // ---- Import -------------------------------------------------------------------------------

    /// <summary>Installs a friend's pack from a .zip. Returns the character it voices.</summary>
    /// <param name="expectedCharacter">
    /// For subscriptions: the character this link has always been. A pack that suddenly claims a
    /// different character is refused (impersonation guard). Null = accept any.
    /// </param>
    public static string Import(string zipPath, Speaker speaker, string? expectedCharacter = null)
    {
        var info = new FileInfo(zipPath);
        if (!info.Exists) throw new FileNotFoundException("That file doesn't exist.");
        if (info.Length > MaxZipBytes) throw new InvalidDataException("That pack is too large (over 200 MB).");

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            if (zip.Entries.Count > MaxEntries)
                throw new InvalidDataException("That pack has too many files.");
            var bad = zip.Entries.FirstOrDefault(e => e.Name.Length > 0 &&
                                                       !AllowedExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()));
            if (bad != null)
                throw new InvalidDataException($"That pack contains a file type that isn't allowed: {bad.Name}");

            // Zip bombs: a small zip that unpacks into gigabytes.
            long unpacked = 0;
            foreach (var e in zip.Entries)
            {
                unpacked += e.Length;
                if (unpacked > MaxUnpackedBytes)
                    throw new InvalidDataException("That pack unpacks to more than 400 MB, so it was refused.");
                if (e.Name.Equals(VoicePack.FileName, StringComparison.OrdinalIgnoreCase) && e.Length > MaxJsonBytes)
                    throw new InvalidDataException("That pack's voicepack.json is suspiciously large, so it was refused.");
                if (VoicePack.PortraitExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()) && e.Length > VoicePack.MaxPortraitBytes)
                    throw new InvalidDataException("That pack's portrait image is over 8 MB, so it was refused.");
            }
        }

        var temp = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "import-temp");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);

        try
        {
            // .NET refuses entries that would land outside the target folder.
            ZipFile.ExtractToDirectory(zipPath, temp);

            // voicepack.json may be at the top of the zip or inside one folder.
            var root = File.Exists(Path.Combine(temp, VoicePack.FileName))
                ? temp
                : Directory.GetDirectories(temp).FirstOrDefault(d => File.Exists(Path.Combine(d, VoicePack.FileName)))
                  ?? throw new InvalidDataException("That zip doesn't contain a voicepack.json.");

            var pack = VoicePack.Load(root);
            if (string.IsNullOrWhiteSpace(pack.Character))
                throw new InvalidDataException("That pack doesn't say which character it belongs to.");
            if (speaker.Pack != null && string.Equals(pack.Character, speaker.Pack.Character, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("That's your own character's pack. Load it from the Voice Pack tab instead.");
            if (!string.IsNullOrEmpty(expectedCharacter) &&
                !string.Equals(pack.Character, expectedCharacter, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"This link used to be {expectedCharacter}'s pack but now claims to be {pack.Character}'s, so the update was refused.");

            var dest = Path.Combine(FriendsRoot, SafeFolderName(pack.Character));
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(FriendsRoot);
            Directory.Move(root, dest);

            speaker.Friends[pack.Character] = VoicePack.Load(dest);
            return pack.Character;
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    // ---- Export ---------------------------------------------------------------------------------

    public record ExportResult(int Clips, long OriginalBytes, long ZipBytes);

    /// <summary>Writes your pack (text + recorded clips only) to a .zip, optionally compressing audio.</summary>
    public static Task<ExportResult> ExportAsync(VoicePack pack, string zipPath, bool compress) =>
        Task.Run(() => Export(pack, zipPath, compress));

    private static ExportResult Export(VoicePack pack, string zipPath, bool compress)
    {
        if (string.IsNullOrWhiteSpace(pack.Character))
            throw new InvalidDataException("Set the pack's character on the Voice Pack tab first, so friends' plugins know whose voice it is.");

        var staging = Path.Combine(Path.GetTempPath(), "OuterVoiceExport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            var copy = pack.CloneForExport();
            var clips = 0;
            long original = 0;

            // Walk the live pack (which knows where each clip is) alongside the copy we export.
            foreach (var (job, lines) in pack.Jobs)
            {
                var copyLines = copy.Jobs[job];
                for (var i = 0; i < lines.Count; i++)
                {
                    var src = lines[i].AudioPath;
                    if (src == null || !File.Exists(src))
                    {
                        copyLines[i].Audio = string.Empty;
                        continue;
                    }

                    var dir = Path.Combine(staging, job.ToUpperInvariant());
                    Directory.CreateDirectory(dir);
                    original += new FileInfo(src).Length;

                    var outName = lines[i].Id + ".mp3";
                    var outPath = Path.Combine(dir, outName);
                    if (!(compress && TryCompress(src, outPath)))
                    {
                        outName = lines[i].Id + Path.GetExtension(src).ToLowerInvariant();
                        outPath = Path.Combine(dir, outName);
                        File.Copy(src, outPath, overwrite: true);
                    }

                    copyLines[i].Audio = outName;
                    clips++;
                }
            }

            // The portrait travels with the pack, so friends see it in their pop-ups.
            copy.Portrait = string.Empty;
            if (pack.PortraitPath is { } portrait)
            {
                var name = "portrait" + Path.GetExtension(portrait).ToLowerInvariant();
                File.Copy(portrait, Path.Combine(staging, name), overwrite: true);
                copy.Portrait = name;
            }

            // Friends use your idle settings for your idle lines.
            copy.SharedIdleFrequency = Plugin.Instance.Configuration.IdleFrequency;
            copy.SharedIdleMinutes = Plugin.Instance.Configuration.IdleMinutes;

            copy.SaveTo(staging);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return new ExportResult(clips, original, new FileInfo(zipPath).Length);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>Re-encodes a clip to mono 48 kbps MP3. Keeps the original if that isn't smaller.</summary>
    private static bool TryCompress(string src, string dest)
    {
        try
        {
            MediaFoundationApi.Startup();
            using var reader = new AudioFileReader(src);
            ISampleProvider samples = reader;
            if (samples.WaveFormat.Channels == 2)
                samples = new StereoToMonoSampleProvider(samples);
            if (samples.WaveFormat.SampleRate is not (44100 or 48000))
                samples = new WdlResamplingSampleProvider(samples, 44100);

            MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(samples), dest, ShareBitrate);

            if (new FileInfo(dest).Length < new FileInfo(src).Length)
                return true;
            File.Delete(dest);
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Couldn't compress {Path.GetFileName(src)}; including it as-is.");
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            return false;
        }
    }

    private static string SafeFolderName(string characterId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(characterId.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return name.Length == 0 ? "unknown" : name;
    }
}
