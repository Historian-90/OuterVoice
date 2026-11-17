using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace OuterVoice;

/// <summary>
/// Watches game state (read-only) and turns it into events for the Speaker, for you AND for
/// party members whose voice packs you've imported. Nothing is sent anywhere: every player's
/// plugin sees the same HP bars, statuses and cast bars, and works out the same result.
///
/// Personal events (a raise, a death, a Kardia swap, a big heal) are spoken by the character
/// who did them. Shared events (duty start, wipe, clear, tank death, clutch save, big pull)
/// are spoken by ONE voiced party member, chosen the same way on every player's machine.
/// </summary>
public sealed class EventWatcher : IDisposable
{
    private const float ClutchLow = 0.15f;
    private const float ClutchHigh = 0.50f;
    private static readonly TimeSpan ClutchWindow = TimeSpan.FromSeconds(3);
    private const int BigPullCount = 5;
    private const float BigPullRange = 30f;
    private static readonly TimeSpan BigPullHold = TimeSpan.FromSeconds(1.5);

    private readonly Speaker speaker;
    private readonly HashSet<uint> raiseIds;
    private readonly Dictionary<uint, int> limitBreakLevels;

    // LB1 and LB2 are shared by each role; every other Limit Break is a job's own LB3.
    private static readonly string[] LimitBreak1Names = ["Shield Wall", "Healing Wind", "Braver", "Big Shot", "Skyshard"];
    private static readonly string[] LimitBreak2Names = ["Stronghold", "Breath of the Earth", "Bladedance", "Desperado", "Starstorm"];

    // Tank Limit Breaks have no cast bar, but they put a buff of the same name on the tank.
    private static readonly string[] TankLimitBreakNames =
        ["Shield Wall", "Stronghold", "Last Bastion", "Land Waker", "Dark Force", "Gunmetal Soul"];

    private readonly Dictionary<uint, int> tankLimitBreakStatusLevels;
    private readonly HashSet<(uint Caster, uint Status)> tankLimitBreakSeen = new();

    /// <summary>Job triggers with their status/action names resolved to game IDs.</summary>
    private readonly List<(JobTrigger Def, HashSet<uint> Statuses, HashSet<uint> Casts)> triggers = new();

    private DateTime nextTick = DateTime.MinValue;
    private int deathsThisDuty;
    private bool wasBigPull;
    private DateTime? bigPullSince;

    // Per-entity memory, so each event fires once on the moment it happens.
    private readonly Dictionary<uint, uint> lastHp = new();
    private readonly Dictionary<uint, DateTime> lowHpSince = new();
    private readonly HashSet<(uint Caster, uint Target)> raisesSeen = new();
    private readonly Dictionary<(uint Caster, string Event), uint> partnerHolder = new();
    private readonly HashSet<(uint Caster, string Event)> partnersSeen = new();
    private readonly HashSet<(uint Caster, uint Target, uint Status)> givenSeen = new();
    private readonly HashSet<(uint Caster, uint Status)> selfSeen = new();
    private readonly HashSet<(uint Caster, string Event)> casting = new();

    // Characters we've seen at least once since they appeared. On first sight we only record their
    // current statuses (a tank stance that's already on shouldn't trigger a line at zone-in).
    private readonly HashSet<uint> baselined = new();

    // When each character was first seen. Moodles from sync plugins arrive a few seconds after a
    // character appears, so Moodle-based custom events wait out this grace period before they can
    // fire (otherwise a permanent Moodle would "appear" on every zone-in on friends' screens).
    private readonly Dictionary<uint, DateTime> firstSeen = new();
    private static readonly TimeSpan MoodleGrace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// True during cutscenes, NPC dialogue and loading screens, and for a few seconds after.
    /// The Speaker refuses every line (voice and text) while this is set.
    /// </summary>
    public static bool Suppressed { get; private set; }

    private DateTime suppressedUntil;
    private static readonly TimeSpan SceneGrace = TimeSpan.FromSeconds(3);

    private static bool InScene() =>
        Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
        Plugin.Condition[ConditionFlag.WatchingCutscene] ||
        Plugin.Condition[ConditionFlag.WatchingCutscene78] ||
        Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
        Plugin.Condition[ConditionFlag.BetweenAreas] ||
        Plugin.Condition[ConditionFlag.BetweenAreas51];

    /// <summary>A party member (or you) who has a voice pack.</summary>
    private sealed record Actor(uint EntityId, string CharacterId, string Job, VoicePack Pack, IBattleChara? Obj);

    private sealed record Member(uint EntityId, uint Hp, uint MaxHp, bool IsTank, StatusList? Statuses);

    public EventWatcher(Speaker speaker)
    {
        this.speaker = speaker;
        raiseIds = StatusIdsNamed("Raise");

        // Every Limit Break action in the game (action category 9 = "Limit Break"), with its level
        // worked out from the action itself, so it works without a gauge (e.g. Explorer Mode).
        limitBreakLevels = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .Where(r => r.ActionCategory.RowId == 9)
            .ToDictionary(r => r.RowId, r => LimitBreakLevelOf(r.Name.ExtractText()));

        tankLimitBreakStatusLevels = Plugin.DataManager.GetExcelSheet<Status>()
            .Where(r => TankLimitBreakNames.Contains(r.Name.ExtractText(), StringComparer.OrdinalIgnoreCase))
            .ToDictionary(r => r.RowId, r => LimitBreakLevelOf(r.Name.ExtractText()));
        if (tankLimitBreakStatusLevels.Count == 0)
            Plugin.Log.Info("No tank Limit Break buffs found in game data; tank LBs won't trigger.");
        foreach (var t in JobTriggers.All)
        {
            var statuses = StatusIdsNamed(t.Statuses);
            var casts = ActionIdsNamed(t.Casts ?? []);
            var missing = t.Statuses.Where(n => !StatusIdsNamed(n).Any())
                                    .Concat((t.Casts ?? []).Where(n => !ActionIdsNamed(n).Any()))
                                    .ToList();
            if (missing.Count > 0)
                Plugin.Log.Info($"{t.Job} {t.Event}: no game data named {string.Join(", ", missing)}; that part won't trigger.");
            triggers.Add((t, statuses, casts));
        }

        Plugin.Framework.Update += OnUpdate;
        Plugin.DutyState.DutyStarted += OnDutyStarted;
        Plugin.DutyState.DutyWiped += OnDutyWiped;
        Plugin.DutyState.DutyCompleted += OnDutyCompleted;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.DutyState.DutyStarted -= OnDutyStarted;
        Plugin.DutyState.DutyWiped -= OnDutyWiped;
        Plugin.DutyState.DutyCompleted -= OnDutyCompleted;
    }

    /// <summary>Your current job's abbreviation ("SGE", "WHM", ...), or null when not logged in.</summary>
    public static string? CurrentJob()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        return me == null ? null : JobAbbrev(me.ClassJob.RowId);
    }

    // ---- Duty events (the same moment for everyone in the duty) -------------------

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        deathsThisDuty = 0;
        SayShared("duty_start");
    }

    private void OnDutyWiped(IDutyStateEventArgs args) => SayShared("wipe");

    private void OnDutyCompleted(IDutyStateEventArgs args) =>
        SayShared(deathsThisDuty == 0 ? "duty_complete_clean" : "duty_complete");

    // ---- Polling ----------------------------------------------------------------------

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddMilliseconds(150);

        // Cutscenes, NPC dialogue and loading screens: stay silent, and stop anything playing.
        if (InScene())
        {
            if (!Suppressed)
            {
                Suppressed = true;
                speaker.Silence();
            }
            suppressedUntil = now + SceneGrace;
            // Forget what we'd seen, so changes that happened during the scene aren't treated as
            // new events afterwards (no backlog of lines when it ends).
            ResetTransientState();
            return;
        }
        if (Suppressed && now >= suppressedUntil)
            Suppressed = false;

        var busy = Plugin.Condition[ConditionFlag.InCombat] || InDuty();
        if (busy || Plugin.ObjectTable.LocalPlayer == null) lastBusy = now;

        var me = Plugin.ObjectTable.LocalPlayer;
        var actors = me == null ? new List<Actor>() : Actors(me);
        if (me == null || actors.Count == 0)
        {
            ResetTransientState();
            return;
        }

        var members = Members(me).ToList();
        var byId = actors.ToDictionary(a => a.EntityId);

        partnersSeen.Clear();
        foreach (var m in members)
        {
            if (m.MaxHp == 0) continue;

            // Deaths: the dead character voices their own death; tank deaths are a shared event.
            var prevHp = lastHp.TryGetValue(m.EntityId, out var p) ? p : m.Hp;
            if (prevHp > 0 && m.Hp == 0)
            {
                deathsThisDuty++;
                if (byId.TryGetValue(m.EntityId, out var dead))
                    Say(dead, "self_death");
                else if (m.IsTank)
                    SayShared("tank_death");
            }
            lastHp[m.EntityId] = m.Hp;

            // Clutch save: dipped under 15% and came back above 50% within 3s.
            var frac = (float)m.Hp / m.MaxHp;
            if (m.Hp > 0 && frac < ClutchLow)
            {
                lowHpSince.TryAdd(m.EntityId, now);
            }
            else if (lowHpSince.TryGetValue(m.EntityId, out var since))
            {
                if (m.Hp > 0 && frac > ClutchHigh && now - since <= ClutchWindow)
                    SayShared("clutch_save", healersOnly: true);
                if (m.Hp == 0 || frac > ClutchHigh || now - since > ClutchWindow)
                    lowHpSince.Remove(m.EntityId);
            }

            if (m.Statuses == null) continue;

            foreach (var s in m.Statuses)
            {
                if (s.SourceId == 0 || !byId.TryGetValue(s.SourceId, out var caster)) continue;

                // Raise: someone gets a Raise from a voiced character.
                if (raiseIds.Contains(s.StatusId) && raisesSeen.Add((caster.EntityId, m.EntityId)))
                    Say(caster, "raise");

                foreach (var (def, ids, _) in triggers)
                {
                    if (def.Job != caster.Job || !ids.Contains(s.StatusId)) continue;
                    var speak = baselined.Contains(caster.EntityId);

                    if (def.Kind == TriggerKind.PartnerSwap)
                    {
                        // Partner chosen for the first time, or moved to someone else (Kardia, Dance Partner).
                        var key = (caster.EntityId, def.Event);
                        partnersSeen.Add(key);
                        if (!partnerHolder.TryGetValue(key, out var holder))
                        {
                            if (speak) Say(caster, def.Event, initial: true);
                        }
                        else if (holder != m.EntityId && speak)
                        {
                            Say(caster, def.Event, initial: false);
                        }
                        partnerHolder[key] = m.EntityId;
                    }
                    else if (def.Kind == TriggerKind.GiveStatus)
                    {
                        if (givenSeen.Add((caster.EntityId, m.EntityId, s.StatusId)) && speak)
                            Say(caster, def.Event);
                    }
                }
            }

            // Forget raises that have been accepted/expired so the next one fires again.
            raisesSeen.RemoveWhere(r => r.Target == m.EntityId &&
                                        !m.Statuses.Any(s => raiseIds.Contains(s.StatusId) && s.SourceId == r.Caster));
            givenSeen.RemoveWhere(g => g.Target == m.EntityId &&
                                       !m.Statuses.Any(s => s.StatusId == g.Status && s.SourceId == g.Caster));
        }

        // A partner buff that's gone from everyone was dropped; the next one counts as a first pick.
        foreach (var key in partnerHolder.Keys.Where(k => !partnersSeen.Contains(k)).ToList())
            partnerHolder.Remove(key);

        foreach (var actor in actors)
        {
            CheckCustomEvents(actor);
            CheckLimitBreak(actor);
            CheckSelfTriggers(actor);
            if (baselined.Add(actor.EntityId))
                firstSeen[actor.EntityId] = DateTime.UtcNow;
        }

        CheckBigPull(me);
        CheckWeather(now, busy);
        CheckIdle(now, busy);
    }

    // ---- Weather and idle ------------------------------------------------------------------

    private static bool InDuty() =>
        Plugin.Condition[ConditionFlag.BoundByDuty] ||
        Plugin.Condition[ConditionFlag.BoundByDuty56] ||
        Plugin.Condition[ConditionFlag.BoundByDuty95];

    private uint weatherTerritory;
    private byte lastWeather;
    private DateTime weatherSettleUntil;
    private static readonly TimeSpan WeatherSettle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The weather changed while you stayed in the same zone. Zoning in only records the weather
    /// (arriving somewhere rainy isn't a change), and nothing is said in duties or combat.
    /// Everyone in the zone sees the same weather, so one voice comments for the party.
    /// </summary>
    private unsafe void CheckWeather(DateTime now, bool busy)
    {
        var wm = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
        if (wm == null) return;
        var weather = wm->GetCurrentWeather();
        var territory = Plugin.ClientState.TerritoryType;

        if (territory != weatherTerritory)
        {
            weatherTerritory = territory;
            lastWeather = weather;
            weatherSettleUntil = now + WeatherSettle;
            return;
        }
        if (weather == lastWeather) return;

        var before = lastWeather;
        lastWeather = weather;
        if (busy || now < weatherSettleUntil || before == 0 || weather == 0) return;

        var ev = WeatherEventFor(weather);
        // Fair Skies to Clear Skies (and the like) isn't worth a comment.
        if (ev != null && ev != WeatherEventFor(before))
            SayShared(ev);
    }

    private static string? WeatherEventFor(byte weatherId) =>
        KnownEvents.WeatherEvent(Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Weather>()
                                       .GetRowOrDefault(weatherId)?.Name.ExtractText() ?? string.Empty);

    private DateTime lastBusy = DateTime.UtcNow;
    private long lastIdleMinute = -1;

    /// <summary>
    /// Once a minute, after a long enough quiet stretch, roll for an idle line. The roll uses the
    /// server minute, so friends who are idle too get the same result and the same single speaker.
    /// </summary>
    private unsafe void CheckIdle(DateTime now, bool busy)
    {
        var minute = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.GetServerTime() / 60;
        if (minute == lastIdleMinute) return;
        lastIdleMinute = minute;

        var cfg = Plugin.Instance.Configuration;
        var chance = cfg.IdleFrequency switch
        {
            IdleFrequency.Rare => 3,
            IdleFrequency.Normal => 7,
            IdleFrequency.Frequent => 15,
            _ => 0,
        };
        if (chance == 0 || busy || now - lastBusy < TimeSpan.FromMinutes(Math.Max(1, cfg.IdleMinutes))) return;

        if (Fnv1a($"idle|{minute}") % 100 < (ulong)chance)
            SayShared("idle");
    }

    /// <summary>A voiced character starts casting a Limit Break (level from the action's cast bar).</summary>
    private void CheckLimitBreak(Actor actor)
    {
        if (actor.Obj == null) return;

        // Tanks: the Limit Break buff appears on the tank, from the tank.
        foreach (var (statusId, tankLevel) in tankLimitBreakStatusLevels)
        {
            var has = actor.Obj.StatusList.Any(s => s.StatusId == statusId && s.SourceId == actor.EntityId);
            if (has && tankLimitBreakSeen.Add((actor.EntityId, statusId)) && baselined.Contains(actor.EntityId))
                Say(actor, $"limit_break_{tankLevel}");
            else if (!has)
                tankLimitBreakSeen.Remove((actor.EntityId, statusId));
        }

        // Everyone else: the Limit Break cast bar.
        var key = (actor.EntityId, "limit_break");
        if (!actor.Obj.IsCasting || !limitBreakLevels.TryGetValue(actor.Obj.CastActionId, out var level))
        {
            casting.Remove(key);
            return;
        }

        if (casting.Add(key) && baselined.Contains(actor.EntityId))
            Say(actor, $"limit_break_{level}");
    }

    // ---- Custom events ------------------------------------------------------------------------

    private readonly HashSet<(uint Caster, string Event)> customActive = new();
    private readonly Dictionary<uint, string> actionNames = new();
    private readonly Dictionary<uint, string> statusNames = new();
    private readonly Dictionary<ushort, string[]> emoteNames = new();

    /// <summary>Plays a custom event's line the moment all of its conditions are true together.</summary>
    private void CheckCustomEvents(Actor actor)
    {
        if (actor.Obj == null || actor.Pack.CustomEvents.Count == 0)
            return;

        IReadOnlyList<string>? moodles = null; // fetched once per character per tick, only if needed
        var settled = firstSeen.TryGetValue(actor.EntityId, out var seenAt) && DateTime.UtcNow - seenAt >= MoodleGrace;

        foreach (var ev in actor.Pack.CustomEvents)
        {
            if (!ev.IsUsable || !ev.Enabled) continue;

            var allTrue = ev.Conditions.All(c => c.Type switch
            {
                ConditionType.Cast => actor.Obj.IsCasting && NameMatches(ActionName(actor.Obj.CastActionId), c.Value),
                ConditionType.Status => actor.Obj.StatusList.Any(st => NameMatches(StatusName(st.StatusId), c.Value)),
                ConditionType.Emote => EmoteNamesOf(actor.Obj).Any(n => NameMatches(n, c.Value)),
                ConditionType.Moodle => (moodles ??= MoodlesIpc.TitlesOn(actor.Obj.Address))
                                        .Any(t => NameMatches(t, MoodlesIpc.Clean(c.Value))),
                _ => false,
            });

            // During the grace period the current state is still recorded (so an already-active
            // Moodle never fires later), it just doesn't speak.
            var usesMoodles = ev.Conditions.Any(c => c.Type == ConditionType.Moodle);
            var maySpeak = baselined.Contains(actor.EntityId) && (!usesMoodles || settled);

            var key = (actor.EntityId, ev.Id);
            if (allTrue && customActive.Add(key) && maySpeak)
                Say(actor, ev.Id);
            else if (!allTrue)
                customActive.Remove(key);
        }
    }

    private static bool NameMatches(string actual, string wanted) =>
        string.Equals(actual.Trim(), wanted.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    private string ActionName(uint id)
    {
        if (!actionNames.TryGetValue(id, out var name))
            actionNames[id] = name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                                           .GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;
        return name;
    }

    private string StatusName(uint id)
    {
        if (!statusNames.TryGetValue(id, out var name))
            statusNames[id] = name = Plugin.DataManager.GetExcelSheet<Status>()
                                           .GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty;
        return name;
    }

    /// <summary>The emote a character is doing, by name ("Wave") and command ("wave").</summary>
    private unsafe string[] EmoteNamesOf(IBattleChara obj)
    {
        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
        if (chara == null) return [];
        var emoteId = chara->EmoteController.EmoteId;
        if (emoteId == 0) return [];

        if (!emoteNames.TryGetValue(emoteId, out var names))
        {
            var row = Plugin.DataManager.GetExcelSheet<Emote>().GetRowOrDefault(emoteId);
            var command = row?.TextCommand.ValueNullable?.Command.ExtractText().TrimStart('/') ?? string.Empty;
            emoteNames[emoteId] = names = new[] { row?.Name.ExtractText() ?? string.Empty, command }
                                          .Where(n => n.Length > 0).ToArray();
        }
        return names;
    }

    private static int LimitBreakLevelOf(string name)
    {
        if (LimitBreak1Names.Contains(name, StringComparer.OrdinalIgnoreCase)) return 1;
        if (LimitBreak2Names.Contains(name, StringComparer.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    /// <summary>Self-status and cast triggers for one voiced character.</summary>
    private void CheckSelfTriggers(Actor actor)
    {
        if (actor.Obj == null) return;
        var speak = baselined.Contains(actor.EntityId);

        foreach (var (def, ids, casts) in triggers)
        {
            if (def.Job != actor.Job || def.Kind != TriggerKind.SelfStatus) continue;

            if (casts.Count > 0)
            {
                var key = (actor.EntityId, def.Event);
                var isCasting = actor.Obj.IsCasting && casts.Contains(actor.Obj.CastActionId);
                if (isCasting && casting.Add(key) && speak)
                    Say(actor, def.Event);
                else if (!isCasting)
                    casting.Remove(key);
            }

            foreach (var statusId in ids)
            {
                var has = actor.Obj.StatusList.Any(s => s.StatusId == statusId && s.SourceId == actor.EntityId);
                if (has && selfSeen.Add((actor.EntityId, statusId)) && speak)
                    Say(actor, def.Event);
                else if (!has)
                    selfSeen.Remove((actor.EntityId, statusId));
            }
        }
    }

    private void CheckBigPull(IPlayerCharacter me)
    {
        if (!Plugin.Condition[ConditionFlag.InCombat])
        {
            wasBigPull = false;
            bigPullSince = null;
            return;
        }

        // Only real enemies count. Boss fights spawn many invisible helper objects for mechanics
        // (hit areas, markers, bubbles to hide in...) that the game also marks as combatants, so
        // each one must be targetable, hostile, alive, named, and actually have HP.
        var count = Plugin.ObjectTable
            .OfType<IBattleNpc>()
            .Count(n => n.BattleNpcKind == BattleNpcSubKind.Combatant
                        && n.IsTargetable
                        && !n.IsDead
                        && n.CurrentHp > 0
                        && n.MaxHp > 1
                        && (n.StatusFlags & StatusFlags.Hostile) != 0
                        && (n.StatusFlags & StatusFlags.InCombat) != 0
                        && n.Name.TextValue.Length > 0
                        && (n.Position - me.Position).Length() <= BigPullRange);

        // Require the pull to stay big for a moment, so a brief spike doesn't trigger it.
        var now = DateTime.UtcNow;
        if (count >= BigPullCount)
            bigPullSince ??= now;
        else
            bigPullSince = null;

        var big = bigPullSince.HasValue && now - bigPullSince.Value >= BigPullHold;
        if (big && !wasBigPull)
            SayShared("big_pull");
        wasBigPull = big;
    }

    // ---- Who speaks ---------------------------------------------------------------------

    private void Say(Actor actor, string eventName, bool? initial = null) =>
        speaker.Trigger(actor.Pack, eventName, actor.Job, Seed(actor.CharacterId, eventName), initial: initial,
                        speakerEntityId: actor.EntityId);

    /// <summary>
    /// Shared events get exactly one speaker. Every player's plugin sorts the same voiced party
    /// members the same way and uses the same server-time window, so they all pick the same one.
    /// </summary>
    private void SayShared(string eventName, bool healersOnly = false)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return;

        var candidates = Actors(me)
            .Where(a => a.Pack.LinesFor(a.Job, eventName).Count > 0)
            .Where(a => !healersOnly || IsHealerJob(a.Job))
            .OrderBy(a => a.CharacterId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return;

        var seed = Seed("shared", eventName);
        var chosen = candidates[(int)((ulong)seed % (ulong)candidates.Count)];
        speaker.Trigger(chosen.Pack, eventName, chosen.Job, Seed(chosen.CharacterId, eventName),
                        speakerEntityId: chosen.EntityId);
    }

    /// <summary>Same number on every player's machine for the same thing in the same 5-second window.</summary>
    private static unsafe long Seed(string who, string eventName)
    {
        var serverTime = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.GetServerTime();
        return (long)Fnv1a($"{who.ToLowerInvariant()}|{eventName}|{serverTime / 5}");
    }

    private static ulong Fnv1a(string s)
    {
        // string.GetHashCode() differs per process, so use a fixed hash everyone agrees on.
        var hash = 14695981039346656037UL;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash & 0x7FFFFFFFFFFFFFFF;
    }

    /// <summary>You (with your own pack) plus party members whose packs you've imported.</summary>
    private List<Actor> Actors(IPlayerCharacter me)
    {
        var list = new List<Actor>();
        var myJob = JobAbbrev(me.ClassJob.RowId);
        if (speaker.Pack is { } mine && myJob != null && speaker.SpeaksOn(mine, myJob))
            list.Add(new Actor(me.EntityId, Plugin.CurrentCharacterId() ?? "me", myJob, mine, me));

        if (!Plugin.Instance.Configuration.HearFriends)
            return list;

        for (var i = 0; i < Plugin.PartyList.Length; i++)
        {
            var m = Plugin.PartyList[i];
            if (m == null || m.EntityId == me.EntityId) continue;

            var world = m.World.ValueNullable?.Name.ExtractText();
            var id = string.IsNullOrEmpty(world) ? m.Name.TextValue : $"{m.Name.TextValue}@{world}";
            var job = JobAbbrev(m.ClassJob.RowId);
            if (job == null || speaker.FriendPack(id) is not { } pack || !speaker.SpeaksOn(pack, job))
                continue;

            list.Add(new Actor(m.EntityId, id, job, pack, m.GameObject as IBattleChara));
        }

        return list;
    }

    private static IEnumerable<Member> Members(IPlayerCharacter me)
    {
        if (Plugin.PartyList.Length == 0)
        {
            yield return new Member(me.EntityId, me.CurrentHp, me.MaxHp, IsTankJob(me.ClassJob.RowId), me.StatusList);

            // Duty Support / Trust NPCs aren't in the party list, but they are in the object table.
            foreach (var npc in Plugin.ObjectTable.OfType<IBattleNpc>()
                                      .Where(n => n.BattleNpcKind == BattleNpcSubKind.NpcPartyMember))
                yield return new Member(npc.EntityId, npc.CurrentHp, npc.MaxHp, IsTankJob(npc.ClassJob.RowId), npc.StatusList);
            yield break;
        }

        for (var i = 0; i < Plugin.PartyList.Length; i++)
        {
            var m = Plugin.PartyList[i];
            if (m == null) continue;
            var statuses = (m.GameObject as IBattleChara)?.StatusList ?? m.Statuses;
            yield return new Member(m.EntityId, m.CurrentHP, m.MaxHP, IsTankJob(m.ClassJob.RowId), statuses);
        }
    }

    private void ResetTransientState()
    {
        lastHp.Clear();
        lowHpSince.Clear();
        raisesSeen.Clear();
        partnerHolder.Clear();
        givenSeen.Clear();
        selfSeen.Clear();
        casting.Clear();
        tankLimitBreakSeen.Clear();
        customActive.Clear();
        baselined.Clear();
        firstSeen.Clear();
        wasBigPull = false;
        bigPullSince = null;
        weatherTerritory = 0; // re-record the weather instead of commenting on a change we didn't watch
    }

    // ---- Game data lookups -----------------------------------------------------------------

    private static string? JobAbbrev(uint classJobId) =>
        Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId)?.Abbreviation.ExtractText();

    private static bool IsTankJob(uint classJobId) =>
        Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId)?.Role == 1;

    private static bool IsHealerJob(string job) => job is "WHM" or "SCH" or "AST" or "SGE" or "CNJ";

    private static HashSet<uint> StatusIdsNamed(params string[] names) =>
        Plugin.DataManager.GetExcelSheet<Status>()
              .Where(r => names.Contains(r.Name.ExtractText(), StringComparer.OrdinalIgnoreCase))
              .Select(r => r.RowId)
              .ToHashSet();

    private static HashSet<uint> ActionIdsNamed(params string[] names) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
              .Where(r => r.IsPlayerAction && names.Contains(r.Name.ExtractText(), StringComparer.OrdinalIgnoreCase))
              .Select(r => r.RowId)
              .ToHashSet();
}
