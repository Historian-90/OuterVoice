using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Config;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;

namespace OuterVoice;

/// <summary>
/// Picks and plays lines for every voice (yours and friends'). There is ONE audio channel and
/// one shared gap between lines for all voices, so characters never talk over each other.
/// </summary>
public sealed class Speaker
{
    private readonly Plugin plugin;
    private readonly Dictionary<string, DateTime> lastEventTime = new();
    private DateTime lastAnyLine = DateTime.MinValue;

    // Repeat protection:
    //  - per trigger: the last line a character said for an event can't be picked for that event
    //    again until a different line for the same event has played;
    //  - if the event only has one line, it waits until the character has said anything else.
    private readonly Dictionary<string, string> lastLineByVoice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> lastLineByTrigger = new(StringComparer.OrdinalIgnoreCase);

    public Speaker(Plugin plugin) => this.plugin = plugin;

    /// <summary>Your own pack (the one you edit).</summary>
    public VoicePack? Pack { get; set; }

    /// <summary>Imported friends' packs, keyed by "First Last@World".</summary>
    public Dictionary<string, VoicePack> Friends { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VoicePack? FriendPack(string characterId) =>
        Friends.TryGetValue(characterId, out var p) && !plugin.Configuration.PrefsFor(characterId).Muted ? p : null;

    /// <summary>Whether a pack has anything to say on a job.</summary>
    public bool SpeaksOn(VoicePack pack, string job) =>
        pack.HasJob(job) || (plugin.Configuration.SharedLinesOnOtherJobs && pack.HasJob(VoicePack.SharedJob));

    public bool SpeaksOn(string job) => Pack != null && SpeaksOn(Pack, job);

    /// <summary>Name shown with a pack's text.</summary>
    public string DisplayName(VoicePack? pack)
    {
        if (pack == Pack && !string.IsNullOrWhiteSpace(plugin.Configuration.SpeakerName))
            return plugin.Configuration.SpeakerName;
        if (pack != null && !string.IsNullOrWhiteSpace(pack.Name))
            return pack.Name;
        var character = pack?.Character.Split('@')[0].Trim() ?? string.Empty;
        return character.Length > 0 ? character.Split(' ')[0] : "Voice";
    }

    public string DisplayName() => DisplayName(Pack);

    /// <summary>
    /// Try to say something. <paramref name="seed"/> makes every player's plugin pick the same line.
    /// Returns the line spoken, if any.
    /// </summary>
    /// <param name="initial">For partner events: true = first pick, false = swap, null = any line.</param>
    public Line? Trigger(VoicePack pack, string eventName, string job, long seed, bool force = false, bool? initial = null)
    {
        var cfg = plugin.Configuration;
        if (!force && !cfg.Enabled)
            return null;

        var def = pack.GetEvent(eventName);
        var now = DateTime.UtcNow;
        var cooldownKey = $"{pack.Character}|{eventName}|{initial}";

        if (!force)
        {
            var globalCd = cfg.GlobalCooldownOverride >= 0 ? cfg.GlobalCooldownOverride : (Pack ?? pack).GlobalCooldownSeconds;
            var player = plugin.Player;

            // One voice at a time: only a more urgent line may cut in (replacing, never overlapping).
            var interrupts = player.IsPlaying && def.Priority > player.CurrentPriority;
            if (player.IsPlaying && !interrupts)
                return null;
            if (!interrupts && (now - lastAnyLine).TotalSeconds < globalCd)
                return null;
            if (lastEventTime.TryGetValue(cooldownKey, out var last) && (now - last).TotalSeconds < def.CooldownSeconds)
                return null;
        }

        var voiceKey = pack.Character;
        var triggerKey = $"{voiceKey}|{job}|{eventName}|{initial}";
        var options = pack.LinesFor(job, eventName);
        if (initial.HasValue)
            options = options.Where(l => l.Initial == initial.Value).ToList();

        if (!force)
        {
            // Skip the line this character last said for this trigger...
            var fresh = lastLineByTrigger.TryGetValue(triggerKey, out var lastForTrigger)
                ? options.Where(l => LineKey(l) != lastForTrigger).ToList()
                : options;

            // ...unless it's the only line, in which case it may repeat once the character has
            // said something else in between.
            if (fresh.Count == 0 && options.Count > 0 &&
                (!lastLineByVoice.TryGetValue(voiceKey, out var lastAny) || lastAny != lastForTrigger))
                fresh = options;

            options = fresh;
        }

        var line = Pick(options, seed);
        if (line == null)
            return null;

        if (!force)
        {
            lastLineByVoice[voiceKey] = LineKey(line);
            lastLineByTrigger[triggerKey] = LineKey(line);
        }

        lastEventTime[cooldownKey] = now;
        lastAnyLine = now;

        if (line.AudioPath != null)
        {
            var prefs = pack == Pack ? null : cfg.PrefsFor(pack.Character);
            var volume = cfg.Volume * (prefs?.Volume ?? 100) / 100 * GameVolumePercent() / 100;
            plugin.Player.Play(line.AudioPath, volume, def.Priority);
        }

        if (line.AudioPath != null || cfg.TextWithoutAudio || force)
            ShowText(DisplayName(pack), line.Text);

        return line;
    }

    private static string LineKey(Line l) => $"{l.Job}/{l.Id}";

    /// <summary>Test helper: play a random line from your own pack.</summary>
    public Line? Test(string eventName, string job) =>
        Pack == null ? null : Trigger(Pack, eventName, job, Random.Shared.NextInt64(), force: true);

    private static Line? Pick(List<Line> options, long seed)
    {
        if (options.Count == 0)
            return null;

        // Prefer lines that have recorded audio; fall back to text-only lines.
        var voiced = options.Where(l => l.AudioPath != null).ToList();
        if (voiced.Count > 0)
            options = voiced;

        // Weighted pick driven by the shared seed, so everyone hears the same line.
        var total = options.Sum(l => Math.Max(1, l.Weight));
        var roll = (int)((ulong)seed % (ulong)total);
        foreach (var l in options)
        {
            roll -= Math.Max(1, l.Weight);
            if (roll < 0)
                return l;
        }

        return options[^1];
    }

    /// <summary>
    /// The game's Master x Voice volume (0-100), or 0 if either is muted. Voices play through
    /// Windows directly, not through the game's mixer, so the plugin applies these itself.
    /// </summary>
    public int GameVolumePercent()
    {
        if (!plugin.Configuration.FollowGameVolume)
            return 100;

        try
        {
            if ((Plugin.GameConfig.TryGet(SystemConfigOption.IsSndMaster, out uint masterMuted) && masterMuted != 0) ||
                (Plugin.GameConfig.TryGet(SystemConfigOption.IsSndVoice, out uint voiceMuted) && voiceMuted != 0))
                return 0;

            var master = Plugin.GameConfig.TryGet(SystemConfigOption.SoundMaster, out uint m) ? (int)m : 100;
            var voice = Plugin.GameConfig.TryGet(SystemConfigOption.SoundVoice, out uint v) ? (int)v : 100;
            return Math.Clamp(master, 0, 100) * Math.Clamp(voice, 0, 100) / 100;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Couldn't read the game's volume settings");
            return 100;
        }
    }

    public void ShowText(string name, string text)
    {
        var cfg = plugin.Configuration;
        switch (cfg.TextOutput)
        {
            case TextOutput.Chat:
                // Printed to your own chat log only; never sent to the server.
                var msg = new SeStringBuilder()
                          .AddUiForeground($"{name}: ", 45)
                          .AddItalics(text)
                          .Build();
                Plugin.ChatGui.Print(msg);
                break;
            case TextOutput.Toast:
                Plugin.ToastGui.ShowNormal($"{name}: {text}", new ToastOptions { Speed = ToastSpeed.Slow });
                break;
            case TextOutput.BattleTalk:
                BattleTalk.Show(name, text, cfg.BattleTalkSeconds, cfg.PortraitIconId);
                break;
        }
    }
}
