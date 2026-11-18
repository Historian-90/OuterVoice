using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Ipc.Exceptions;

namespace OuterVoice;

/// <summary>
/// Puts each voice pack's portrait into the game's own battle-talk pop-up.
///
/// The pop-up shows a portrait by icon number, so each portrait gets a spare icon number, its image
/// is converted to the game's texture format, and Penumbra (through its public API) is asked to
/// serve that file whenever the game loads that icon. Nothing is installed: the swap is temporary
/// and disappears when the plugin unloads. Without Penumbra, pop-ups simply have no portrait.
/// </summary>
public sealed class Portraits : IDisposable
{
    private const string Tag = "Outer Voice portraits";
    private const int Priority = 99;
    // The pop-up stretches the picture to fill a portrait area about 5:4 (wide), so the canvas has the
    // same shape; the picture itself keeps its own proportions inside it.
    private const int Width = 640;
    private const int Height = 512;

    /// <summary>Icon numbers well away from anything the game uses.</summary>
    private const uint IconBase = 290000;

    private readonly string folder;
    private readonly Dictionary<string, (string Key, uint Icon)> ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> building = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> redirects = new();
    private uint nextIcon;
    private bool registered;
    private DateTime nextCheck;

    /// <summary>What the Voice Pack page shows under the portrait.</summary>
    public string Status { get; private set; } = string.Empty;
    public bool StatusIsError { get; private set; }

    public Portraits()
    {
        folder = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "portraits");
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch
        {
            // leftovers from last time are harmless
        }
        Directory.CreateDirectory(folder);

        // The game keeps icons it has loaded, so never reuse a number from an earlier load of the plugin.
        nextIcon = IconBase + 1 + (uint)(Environment.TickCount & 0x1FFF) * 4;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (registered)
        {
            try
            {
                RemoveAll().InvokeFunc(Tag, Priority);
            }
            catch
            {
                // Penumbra already gone
            }
        }
    }

    /// <summary>The icon number showing this pack's portrait, once it's ready (null = no portrait yet).</summary>
    public uint? IconFor(VoicePack? pack)
    {
        if (pack?.PortraitPath is not { } src) return null;
        var key = $"{KeyOf(src)}|{pack.PortraitSize}|{pack.PortraitX}|{pack.PortraitY}";
        if (ready.TryGetValue(pack.Character, out var r) && r.Key == key)
            return r.Icon != 0 && registered ? r.Icon : null;
        Prepare(pack.Character, src, key, pack.PortraitSize, pack.PortraitX, pack.PortraitY);
        return null;
    }

    /// <summary>Every few seconds: get portraits ready for your pack and friends' packs before they're needed.</summary>
    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (DateTime.UtcNow < nextCheck) return;
        nextCheck = DateTime.UtcNow.AddSeconds(5);

        var speaker = Plugin.Instance.Speaker;
        IconFor(speaker.Pack);
        foreach (var friend in speaker.Friends.Values) IconFor(friend);

        if (!registered && redirects.Count > 0) Register();
    }

    private static string KeyOf(string path)
    {
        var info = new FileInfo(path);
        return $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
    }

    private void Prepare(string character, string src, string key, int size, int posX, int posY)
    {
        if (!building.Add(character)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var pixels = await Compose(src, size, posX, posY);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    var icon = nextIcon++;
                    var file = Path.Combine(folder, $"{icon}.tex");
                    WriteTex(file, pixels, Width, Height);
                    redirects[IconPath(icon, false)] = file;
                    redirects[IconPath(icon, true)] = file;
                    ready[character] = (key, icon);
                    Register();
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Couldn't prepare the portrait for {character}");
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    SetStatus($"Couldn't read {character}'s portrait: {ex.Message}", true);
                    ready[character] = (key, 0); // don't retry until the file changes
                });
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() => building.Remove(character));
            }
        });
    }

    /// <summary>Ask Penumbra to serve our portrait files for their icon numbers.</summary>
    private void Register()
    {
        try
        {
            if (registered) RemoveAll().InvokeFunc(Tag, Priority);
            var result = AddAll().InvokeFunc(Tag, new Dictionary<string, string>(redirects), string.Empty, Priority);
            registered = result == 0; // PenumbraApiEc.Success
            if (registered)
                SetStatus("Ready: your portrait shows in battle-talk pop-ups.", false);
            else
                SetStatus($"Penumbra refused the portrait (code {result}).", true);
        }
        catch (IpcNotReadyError)
        {
            registered = false;
            SetStatus("Portraits need Penumbra, which isn't running. Pop-ups show without a picture.", true);
        }
        catch (Exception ex)
        {
            registered = false;
            Plugin.Log.Warning(ex, "Couldn't register portraits with Penumbra");
            SetStatus($"Couldn't hand the portrait to Penumbra: {ex.Message}", true);
        }
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }

    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<string, Dictionary<string, string>, string, int, int> AddAll() =>
        Plugin.PluginInterface.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5");

    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<string, int, int> RemoveAll() =>
        Plugin.PluginInterface.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5");

    /// <summary>"ui/icon/290000/290123.tex", or the high-resolution "_hr1" version.</summary>
    private static string IconPath(uint icon, bool highRes) =>
        $"ui/icon/{icon / 1000 * 1000:D6}/{icon:D6}{(highRes ? "_hr1" : "")}.tex";

    // ---- Image to texture ---------------------------------------------------------------------

    /// <summary>
    /// Places the whole image (no cropping, proportions kept) on a transparent canvas the shape of the
    /// pop-up's portrait area: at 100% it fits the area exactly, smaller sizes shrink it, and it sits
    /// at the chosen position. The game stretches the canvas to the area, so the shapes must match.
    /// </summary>
    private static async Task<byte[]> Compose(string path, int sizePercent, int posX, int posY)
    {
        using var wrap = await Plugin.TextureProvider.GetFromFileAbsolute(path).RentAsync();
        var (spec, data) = await Plugin.TextureReadback.GetRawImageAsync(
            wrap, new TextureModificationArgs { DxgiFormat = 87 }, leaveWrapOpen: true); // 87 = B8G8R8A8

        int w = spec.Width, h = spec.Height, pitch = spec.Pitch;
        var scale = Math.Clamp(sizePercent, 10, 100) / 100f * Math.Min(Width / (float)w, Height / (float)h);
        var dw = Math.Clamp((int)MathF.Round(w * scale), 1, Width);
        var dh = Math.Clamp((int)MathF.Round(h * scale), 1, Height);
        var left = (int)MathF.Round((Width - dw) * Math.Clamp(posX, 0, 100) / 100f);
        var top = (int)MathF.Round((Height - dh) * Math.Clamp(posY, 0, 100) / 100f);

        var output = new byte[Width * Height * 4]; // fully transparent
        var inv = 1f / scale;
        for (var y = 0; y < dh; y++)
        {
            var sy = (y + 0.5f) * inv - 0.5f;
            for (var x = 0; x < dw; x++)
            {
                var sx = (x + 0.5f) * inv - 0.5f;
                Bilinear(data, pitch, w, h, sx, sy, output, ((top + y) * Width + left + x) * 4);
            }
        }
        return output;
    }

    private static void Bilinear(byte[] src, int pitch, int w, int h, float x, float y, byte[] dst, int at)
    {
        var x1 = Math.Clamp((int)MathF.Floor(x), 0, w - 1);
        var y1 = Math.Clamp((int)MathF.Floor(y), 0, h - 1);
        var x2 = Math.Min(x1 + 1, w - 1);
        var y2 = Math.Min(y1 + 1, h - 1);
        var fx = Math.Clamp(x - x1, 0f, 1f);
        var fy = Math.Clamp(y - y1, 0f, 1f);
        for (var c = 0; c < 4; c++)
        {
            float a = src[y1 * pitch + x1 * 4 + c], b = src[y1 * pitch + x2 * 4 + c];
            float d = src[y2 * pitch + x1 * 4 + c], e = src[y2 * pitch + x2 * 4 + c];
            var top = a + (b - a) * fx;
            var bottom = d + (e - d) * fx;
            dst[at + c] = (byte)Math.Clamp(top + (bottom - top) * fy + 0.5f, 0, 255);
        }
    }

    /// <summary>Writes an uncompressed B8G8R8A8 .tex (the game's texture format) with one mip level.</summary>
    private static void WriteTex(string path, byte[] bgra, int width, int height)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(0x00800000u);   // attribute: 2D texture
        w.Write(0x1450u);       // format: B8G8R8A8
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((ushort)1);     // depth
        w.Write((byte)1);       // mip levels
        w.Write((byte)0);       // array size
        w.Write(0u); w.Write(1u); w.Write(2u); // LOD offsets
        w.Write(80u);           // first surface right after the 80-byte header
        for (var i = 1; i < 13; i++) w.Write(0u);
        w.Write(bgra);
    }
}
