using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OuterVoice.Sync;

/// <summary>
/// Writes your pack as one zip into a folder inside Dropbox, always under the same file name, so
/// the share link you gave friends keeps pointing at the newest version.
/// </summary>
public sealed class Publisher
{
    private readonly Plugin plugin;
    private bool running;
    private bool again;

    public Publisher(Plugin plugin) => this.plugin = plugin;

    public string Status { get; private set; } = string.Empty;
    public bool StatusIsError { get; private set; }
    public bool Running => running;

    /// <summary>The zip's full path for the current pack, or null if publishing isn't set up.</summary>
    public string? TargetPath(VoicePack pack)
    {
        var folder = plugin.Configuration.PublishFolder;
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(pack.Character))
            return null;
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(pack.Character.Select(c => invalid.Contains(c) || c == '@' ? ' ' : c).ToArray()).Trim();
        return Path.Combine(folder, $"Outer Voice - {name}.zip");
    }

    /// <summary>Publishes in the background. Saves made while publishing trigger one more run.</summary>
    public void Publish()
    {
        if (running)
        {
            again = true;
            return;
        }

        var pack = plugin.Speaker.Pack;
        if (pack == null) return;
        var target = TargetPath(pack);
        if (target == null)
        {
            SetStatus(string.IsNullOrWhiteSpace(pack.Character)
                ? "Set your character on the Voice Pack tab first."
                : "Choose a publish folder inside Dropbox first.", true);
            return;
        }

        running = true;
        SetStatus("Publishing...", false);
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Build next to the target, then swap in one step, so Dropbox never uploads a half file.
                var temp = target + ".partial";
                var result = await PackSharing.ExportAsync(pack, temp, plugin.Configuration.CompressOnExport);
                File.Move(temp, target, overwrite: true);
                SetStatus($"Published {DateTime.Now:t}: {result.Clips} clips, {result.ZipBytes / 1024d / 1024d:0.0} MB", false);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Publish failed");
                SetStatus($"Couldn't publish: {ex.Message}", true);
            }
            finally
            {
                running = false;
                if (again)
                {
                    again = false;
                    Publish();
                }
            }
        });
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }
}
