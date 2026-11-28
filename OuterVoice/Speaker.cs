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

    /// <summary>Who started the current line, when, and whether it was a custom event.</summary>

    /// <summary>
    /// A custom event is more specific than a built-in one, so if both fire for the same moment
    /// (Panhaima's big-heal line and a custom "Panhaimatinon" event, say), the custom line wins
    /// as long as it comes within this long of the built-in one starting.
    /// </summary>
    /// <summary>
    /// The line the dice chose last time, per character + event, whether or not it actually played
    /// (blocked by another line, a cooldown...). Everyone who saw the event records the same choice,
    /// so skipping it next time keeps friends in sync while stopping back-to-back repeats.
    /// </summary>
    private readonly Dictionary<string, (string Line, DateTime At)> chosen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ChosenMemory = TimeSpan.FromMinutes(5);

    private static bool IsCustomId(string eventName) => eventName.StartsWith("custom_", StringComparison.Ordinal);

    // Repeat protection:
    //  - per trigger: the last line a character said for an event can't be picked for that event
    //    again until a different line for the same event has played;
    //  - if the event only has one line, it waits until the character has said anything else.

    /// <summary>Friends' clips longer than this are skipped.</summary>
    public static readonly TimeSpan MaxFriendClip = TimeSpan.FromSeconds(30);

    public Speaker(Plugin plugin) => this.plugin = plugin;

    /// <summary>Your own pack (the one you edit).</summary>
    public VoicePack? Pack { get; set; }

    /// <summary>Imported friends' packs, keyed by "First Last@World".</summary>
    public Dictionary<string, VoicePack> Friends { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VoicePack? FriendPack(string characterId) =>
        Friends.TryGetValue(characterId, out var p) && !plugin.Configuration.PrefsFor(characterId).Muted ? p : null;

    /// <summary>Whether a pack has anything to say on a job.</summary>
    public bool SpeaksOn(VoicePack pack, string job) =>
        pack.HasJob(job) || pack.HasJob(VoicePack.SharedJob); // Every job lines play on any job

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
    /// <param name="speakerEntityId">The character speaking, so their mouth can move (0 = none).</param>
    /// <summary>Why the last Trigger call said nothing (null when it spoke).</summary>
    public string? LastDrop { get; private set; }

    /// <summary>
    /// A line just started (not test plays): who, which line, the event, how long the clip is, and
    /// the shared seed. Replies listen to this.
    /// </summary>
    public event Action<VoicePack, Line, string, TimeSpan, long>? LineSpoken;

    public Line? Trigger(VoicePack pack, string eventName, string job, long seed, bool force = false, bool? initial = null,
                         uint speakerEntityId = 0, Func<Line, bool>? lineFilter = null, bool isReply = false)
    {
        var cfg = plugin.Configuration;
        LastDrop = null;
        if (!force && (!cfg.Enabled || EventWatcher.Suppressed))
        {
            LastDrop = !cfg.Enabled ? "the plugin is off" : "a cutscene, dialogue or loading screen just ended";
            return null; // off, or a cutscene / dialogue / loading screen: drop it, don't queue it
        }
        if (!force && cfg.OffInPvp && Plugin.ClientState.IsPvP)
        {
            LastDrop = "PvP (Settings > Playback)";
            return null;
        }

        var def = pack.GetEvent(eventName);
        var now = DateTime.UtcNow;

        // A line with no text and no recording has nothing to say; never pick it.
        var options = pack.LinesFor(job, eventName)
                          .Where(l => !string.IsNullOrWhiteSpace(l.Text) || l.AudioPath != null)
                          .ToList();
        if (initial.HasValue)
            options = options.Where(l => l.Initial == initial.Value).ToList();
        if (lineFilter != null)
            options = options.Where(lineFilter).ToList();

        // Choose the line first (before anything below can block it), so every PC that saw this
        // event records the same choice. The line chosen last time (within a few minutes) is skipped.
        var memoryKey = $"{pack.Character}|{eventName}|{initial}";
        if (!force && options.Count > 1 && chosen.TryGetValue(memoryKey, out var prev) && now - prev.At <= ChosenMemory)
        {
            var fresh = options.Where(l => LineKey(l) != prev.Line).ToList();
            if (fresh.Count > 0) options = fresh;
        }
        var line = Pick(options, seed);
        if (line == null)
        {
            LastDrop = "no line to pick";
            return null;
        }
        if (!force)
            chosen[memoryKey] = (LineKey(line), now);

        // Settings > Chatter: categories can be switched off, or made less / more frequent.
        var chatter = cfg.ChatterFor(KnownEvents.ChatterCategory(pack, eventName));
        if (!force && chatter == Chattiness.Off && KnownEvents.ChatterCategory(pack, eventName) != "Idle")
        {
            LastDrop = "that chatter category is off";
            return null;
        }
        var cooldownSeconds = chatter switch
        {
            Chattiness.Less => def.CooldownSeconds * 3,
            Chattiness.More => def.CooldownSeconds / 2,
            _ => def.CooldownSeconds,
        };
        var cooldownKey = $"{pack.Character}|{eventName}|{initial}";

        if (!force)
        {
            var globalCd = cfg.GlobalCooldownOverride >= 0 ? cfg.GlobalCooldownOverride : (Pack ?? pack).GlobalCooldownSeconds;
            var player = plugin.Player;

            // One voice at a time: only a more urgent line may cut in (replacing, never overlapping).
            // Custom events count as urgent only when "Urgent" is ticked (higher priority).
            var interrupts = player.IsPlaying && def.Priority > player.CurrentPriority;
            if (player.IsPlaying && !interrupts)
            {
                LastDrop = "another line was playing";
                return null;
            }
            // A reply answers the line just said, so it doesn't wait for the usual pause.
            if (!interrupts && !isReply && (now - lastAnyLine).TotalSeconds < globalCd)
            {
                LastDrop = $"the {globalCd}-second pause between lines";
                return null;
            }
            if (lastEventTime.TryGetValue(cooldownKey, out var last) && (now - last).TotalSeconds < cooldownSeconds)
            {
                LastDrop = "this event's cooldown";
                return null;
            }
        }


        lastEventTime[cooldownKey] = now;
        lastAnyLine = now;

        var length = TimeSpan.Zero;
        if (line.AudioPath != null)
        {
            var prefs = pack == Pack ? null : cfg.PrefsFor(pack.Character);
            var volume = cfg.Volume * (prefs?.Volume ?? 100) / 100 * GameVolumePercent() / 100;
            // Friends' clips: capped length, optional loudness limiter.
            var isFriend = pack != Pack;
            length = plugin.Player.Play(line.AudioPath, volume, def.Priority,
                                            isFriend ? MaxFriendClip : null,
                                            isFriend && cfg.LimitFriendLoudness);
            if (cfg.LipSync && length > TimeSpan.Zero && volume > 0)
                plugin.Lips.Start(speakerEntityId, length);
        }

        if (!string.IsNullOrWhiteSpace(line.Text) && (line.AudioPath != null || cfg.TextWithoutAudio || force))
            ShowText(VoicePack.CleanText(DisplayName(pack)), VoicePack.CleanText(line.Text), pack);

        if (!force)
            LineSpoken?.Invoke(pack, line, eventName, length, seed);
        return line;
    }

    private static string LineKey(Line l) => $"{l.Job}/{l.Id}";

    /// <summary>Stops any line that's playing (and the speaker's mouth).</summary>
    public void Silence()
    {
        plugin.Player.Stop();
    }

    /// <summary>Plays one specific line of any pack (Replies page preview).</summary>
    public void PreviewLine(VoicePack pack, Line line) =>
        Trigger(pack, line.Event, line.Job, 0, force: true, lineFilter: l => ReferenceEquals(l, line));

    /// <summary>Test helper: play a random line from your own pack.</summary>
    public Line? Test(string eventName, string job) =>
        Pack == null ? null : Trigger(Pack, eventName, job, Random.Shared.NextInt64(), force: true,
                                      speakerEntityId: Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0);

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

    public void ShowText(string name, string text, VoicePack? pack = null)
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
                // The speaker's own portrait (from their pack) if it's ready; 0 = a pop-up without a picture.
                var portrait = plugin.Portraits.IconFor(pack ?? Pack) ?? 0u;
                BattleTalk.Show(name, text, cfg.BattleTalkSeconds, portrait, (byte)Math.Clamp(cfg.BattleTalkStyle, 0, 255));
                break;
        }
    }
}
