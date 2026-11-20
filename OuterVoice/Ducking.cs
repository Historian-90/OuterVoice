using System;
using System.Collections.Generic;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;

namespace OuterVoice;

/// <summary>
/// Lowers the game's music, sound effects, ambience, system sounds and performance sounds while a
/// line plays, so quiet deliveries (whispers, growls) stay audible, then brings them back. Both ways
/// are gradual fades. The game's Voice volume is left alone (it's what our own volume follows).
/// </summary>
public sealed class Ducking : IDisposable
{
    private static readonly SystemConfigOption[] Channels =
    [
        SystemConfigOption.SoundBgm,
        SystemConfigOption.SoundSe,
        SystemConfigOption.SoundEnv,
        SystemConfigOption.SoundSystem,
        SystemConfigOption.SoundPerform,
    ];

    private const double FadeDownSeconds = 0.35;
    private const double FadeUpSeconds = 0.9;
    private static readonly TimeSpan HoldAfterLine = TimeSpan.FromSeconds(0.5);

    private readonly Plugin plugin;
    private readonly Dictionary<SystemConfigOption, uint> original = new();
    private readonly Dictionary<SystemConfigOption, uint> written = new();
    private double level; // 0 = normal, 1 = fully lowered
    private DateTime lastPlaying = DateTime.MinValue;
    private DateTime lastTick = DateTime.UtcNow;

    public Ducking(Plugin plugin)
    {
        this.plugin = plugin;
        RestoreLeftovers();
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        RestoreNow();
    }

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - lastTick).TotalSeconds, 0, 0.25);
        lastTick = now;

        var cfg = plugin.Configuration;
        if (plugin.Player.IsPlaying) lastPlaying = now;
        var want = cfg.DuckGameAudio && now - lastPlaying < HoldAfterLine ? 1.0 : 0.0;
        if (want == 0 && level == 0) return;

        if (level == 0 && want > 0)
            Capture();

        level = want > level
            ? Math.Min(want, level + dt / FadeDownSeconds)
            : Math.Max(want, level - dt / FadeUpSeconds);

        // Smoothstep, so the start and end of each fade are gentle.
        var eased = level * level * (3 - 2 * level);
        var keep = Math.Clamp(cfg.DuckKeepPercent, 0, 100) / 100.0;
        var factor = 1 - eased * (1 - keep);

        foreach (var ch in Channels)
        {
            if (!original.TryGetValue(ch, out var orig)) continue;
            if (!Plugin.GameConfig.TryGet(ch, out uint current)) continue;

            // You moved this slider yourself while it was lowered: your value wins, hands off.
            if (written.TryGetValue(ch, out var last) && current != last)
            {
                original.Remove(ch);
                written.Remove(ch);
                continue;
            }

            var value = level <= 0 ? orig : (uint)Math.Round(orig * factor);
            if (value != current)
                Plugin.GameConfig.Set(ch, value);
            written[ch] = value;
        }

        if (level <= 0)
        {
            original.Clear();
            written.Clear();
            SaveLeftovers(null);
        }
    }

    private void Capture()
    {
        original.Clear();
        written.Clear();
        foreach (var ch in Channels)
            if (Plugin.GameConfig.TryGet(ch, out uint v))
            {
                original[ch] = v;
                written[ch] = v;
            }
        SaveLeftovers(original);
    }

    /// <summary>Put every lowered slider straight back (plugin unloading).</summary>
    private void RestoreNow()
    {
        foreach (var (ch, v) in original)
            if (!Plugin.GameConfig.TryGet(ch, out uint current) ||
                !written.TryGetValue(ch, out var last) || current == last)
                Plugin.GameConfig.Set(ch, v);
        original.Clear();
        written.Clear();
        level = 0;
        SaveLeftovers(null);
    }

    /// <summary>
    /// The original volumes are also kept in the plugin settings while lowered, so a game crash
    /// mid-line doesn't leave the music quiet: the next start puts them back.
    /// </summary>
    private void SaveLeftovers(Dictionary<SystemConfigOption, uint>? values)
    {
        var cfg = plugin.Configuration;
        if (values == null && cfg.DuckedVolumes.Count == 0) return;
        cfg.DuckedVolumes = values == null ? new() : new(values.Count);
        if (values != null)
            foreach (var (ch, v) in values) cfg.DuckedVolumes[ch.ToString()] = v;
        cfg.Save();
    }

    private void RestoreLeftovers()
    {
        var cfg = plugin.Configuration;
        if (cfg.DuckedVolumes.Count == 0) return;
        foreach (var (name, v) in cfg.DuckedVolumes)
            if (Enum.TryParse<SystemConfigOption>(name, out var ch))
                Plugin.GameConfig.Set(ch, v);
        Plugin.Log.Info("Put back game volumes that were still lowered from last time.");
        cfg.DuckedVolumes = new();
        cfg.Save();
    }
}
