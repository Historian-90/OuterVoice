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
            partySettleUntil = now + PartySettle;
            // Forget what we'd seen, so changes that happened during the scene aren't treated as
            // new events afterwards (no backlog of lines when it ends).
            ResetTransientState();
            return;
        }
        if (Suppressed && now >= suppressedUntil)
            Suppressed = false;

        var busy = Plugin.Condition[ConditionFlag.InCombat] || InDuty();
        if (busy || !StandingStill()) lastBusy = now;

        var me = Plugin.ObjectTable.LocalPlayer;
        pendingLines.Clear();
        currentWeatherLabel = CurrentWeatherLabel();
        currentPeriod = WorldState.CurrentPeriod().Label;
        currentPlace = WorldState.CurrentPlace();
        CheckParty(now, me);
        var actors = me == null ? new List<Actor>() : Actors(me);
        if (me == null || actors.Count == 0)
        {
            ResetTransientState();
            if (me == null) placeTerritory = 0; // logged out: the next zone is a fresh start
            return;
        }

        var members = Members(me).ToList();
        currentMembers = members;
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

        CheckChests(actors, now);

        foreach (var actor in actors)
        {
            TrackStillness(actor, now, busy);
            CheckCustomEvents(actor);
            CheckLimitBreak(actor);
            CheckSelfTriggers(actor);
            if (baselined.Add(actor.EntityId))
                firstSeen[actor.EntityId] = DateTime.UtcNow;
        }

        CheckBigPull(me);
        FlushPendingLines();
        CheckWeather(now, busy);
        CheckTimeOfDay(busy);
        var meActor = actors.FirstOrDefault(a => a.EntityId == me.EntityId);
        CheckArrivals(now, me, meActor, actors);
        CheckStates(meActor, me, busy);
        CheckFriendStates(actors, me, busy);
        CheckIdle(now, busy, actors);
    }

    // ---- Joining a party ------------------------------------------------------------------

    private sealed record PartyMember(string CharacterId, string? Job, uint EntityId);
    private sealed record PartySnapshot(Dictionary<string, PartyMember> Others, bool IAmLeader)
    {
        public bool Same(PartySnapshot o) =>
            IAmLeader == o.IAmLeader && Others.Count == o.Others.Count && Others.Keys.All(o.Others.ContainsKey);
    }

    private PartySnapshot? partyCommitted;
    private PartySnapshot? partyPending;
    private DateTime partyPendingSince;
    private static readonly TimeSpan PartyHold = TimeSpan.FromSeconds(2);
    private DateTime partySettleUntil;
    private static readonly TimeSpan PartySettle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Someone joins a party, and the joiner says the line: you, when you join someone else's
    /// party; a friend with a voice pack, when they join yours (or the one you're in). Each player's
    /// plugin sees the same change, so everyone hears the joiner once. Duties are ignored (Duty
    /// Finder fills the party), and the party is frozen while you're in one, so coming back out
    /// to the party you queued with isn't a "join". A change must hold for 2 seconds to count.
    /// </summary>
    private void CheckParty(DateTime now, IPlayerCharacter? me)
    {
        if (me == null)
        {
            partyCommitted = partyPending = null;
            partySettleUntil = now + PartySettle;
            return;
        }
        if (InDuty()) return;

        var snap = ReadParty(me);
        if (partyCommitted == null || now < partySettleUntil)
        {
            // Just logged in or came out of a loading screen: the party list can fill in a few
            // seconds late, so only record it for now.
            partyCommitted = snap;
            partyPending = null;
            return;
        }
        if (partyPending == null || !partyPending.Same(snap))
        {
            partyPending = snap;
            partyPendingSince = now;
            return;
        }
        if (now - partyPendingSince < PartyHold || partyCommitted.Same(snap)) return;

        var before = partyCommitted;
        partyCommitted = snap;

        // You joined someone's party: you weren't in one, and you're not the leader of this one.
        if (before.Others.Count == 0 && snap.Others.Count > 0 && !snap.IAmLeader)
        {
            var myJob = JobAbbrev(me.ClassJob.RowId);
            if (speaker.Pack is { } mine && myJob != null && speaker.SpeaksOn(mine, myJob))
                Moment(new Actor(me.EntityId, Plugin.CurrentCharacterId() ?? "me", myJob, mine, me), "party_join", MomentJoin);
            return; // everyone else was already there
        }

        // Friends who joined the party you're in (or formed with you as leader).
        if (!Plugin.Instance.Configuration.HearFriends) return;
        foreach (var (id, m) in snap.Others)
        {
            if (before.Others.ContainsKey(id) || m.Job == null) continue;
            if (speaker.FriendPack(id) is not { } pack || !speaker.SpeaksOn(pack, m.Job)) continue;
            var obj = Plugin.ObjectTable.FirstOrDefault(o => o.EntityId == m.EntityId && m.EntityId != 0) as IBattleChara;
            Moment(new Actor(m.EntityId, id, m.Job, pack, obj), "party_join", MomentJoin);
        }
    }

    /// <summary>Everyone else in your party (normal or cross-world), and whether you lead it.</summary>
    private static unsafe PartySnapshot ReadParty(IPlayerCharacter me)
    {
        var others = new Dictionary<string, PartyMember>(StringComparer.OrdinalIgnoreCase);
        var leader = false;

        if (Plugin.PartyList.Length > 0)
        {
            for (var i = 0; i < Plugin.PartyList.Length; i++)
            {
                var m = Plugin.PartyList[i];
                if (m == null) continue;
                if (m.EntityId == me.EntityId)
                {
                    leader = Plugin.PartyList.PartyLeaderIndex == i;
                    continue;
                }
                var world = m.World.ValueNullable?.Name.ExtractText();
                var id = string.IsNullOrEmpty(world) ? m.Name.TextValue : $"{m.Name.TextValue}@{world}";
                others[id] = new PartyMember(id, JobAbbrev(m.ClassJob.RowId), m.EntityId);
            }
        }
        else if (FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm.IsCrossRealmParty())
        {
            // Cross-world parties (Party Finder) out in the world aren't in the normal party list.
            var proxy = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm.Instance();
            if (proxy != null)
            {
                leader = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm.IsLocalPlayerPartyLeader();
                var group = proxy->LocalPlayerGroupIndex;
                var count = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm.GetGroupMemberCount(group);
                for (uint i = 0; i < count; i++)
                {
                    var m = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm.GetGroupMember(i, group);
                    if (m == null || m->EntityId == me.EntityId) continue;
                    var name = m->NameString;
                    if (string.IsNullOrEmpty(name) || name == me.Name.TextValue && m->EntityId == 0) continue;
                    var world = Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault((uint)m->HomeWorld)?.Name.ExtractText();
                    var id = string.IsNullOrEmpty(world) ? name : $"{name}@{world}";
                    others[id] = new PartyMember(id, JobAbbrev(m->ClassJobId), m->EntityId);
                }
            }
        }

        return new PartySnapshot(others, leader);
    }

    // ---- Moments (things that just happened) for custom events -------------------------------

    private const string MomentCoffer = "coffer";
    private const string MomentCofferMap = "coffer_map";
    private const string MomentJoin = "join";
    private static readonly TimeSpan MomentWindow = TimeSpan.FromSeconds(3);

    /// <summary>When each character last opened a coffer / joined a party.</summary>
    private readonly Dictionary<(uint Entity, string Kind), DateTime> moments = new();

    /// <summary>
    /// Built-in lines for moments, held until the end of the tick so a custom event using the same
    /// moment (say, "opens a coffer" + a Moodle) can speak instead of the generic line.
    /// </summary>
    private readonly List<(Actor Actor, string Event, string Kind)> pendingLines = new();

    private void Moment(Actor actor, string eventName, string kind)
    {
        moments[(actor.EntityId, kind)] = DateTime.UtcNow;
        pendingLines.Add((actor, eventName, kind));
    }

    private bool Recent(Actor actor, string kind) =>
        moments.TryGetValue((actor.EntityId, kind), out var at) && DateTime.UtcNow - at <= MomentWindow;

    private void FlushPendingLines()
    {
        foreach (var (actor, ev, _) in pendingLines)
            Say(actor, ev);
        pendingLines.Clear();
    }

    private string? currentWeatherLabel;

    private static unsafe string? CurrentWeatherLabel()
    {
        var wm = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
        if (wm == null) return null;
        var w = wm->GetCurrentWeather();
        if (w == 0) return null;
        var ev = WeatherEventFor(w);
        return ev == null ? null : KnownEvents.Get(ev)?.Label;
    }

    /// <summary>Since when each friend has been standing still out of combat (you use lastBusy).</summary>
    private readonly Dictionary<uint, (System.Numerics.Vector3 Pos, DateTime Since)> stillSince = new();

    private void TrackStillness(Actor actor, DateTime now, bool busy)
    {
        if (actor.Obj == null || actor.EntityId == Plugin.ObjectTable.LocalPlayer?.EntityId) return;
        var pos = actor.Obj.Position;
        var active = busy || actor.Obj.StatusFlags.HasFlag(StatusFlags.InCombat) ||
                     !stillSince.TryGetValue(actor.EntityId, out var prev) ||
                     System.Numerics.Vector3.Distance(prev.Pos, pos) > MoveThreshold;
        stillSince[actor.EntityId] = (pos, active ? now : stillSince[actor.EntityId].Since);
    }

    private TimeSpan StillFor(Actor actor)
    {
        var now = DateTime.UtcNow;
        if (actor.EntityId == Plugin.ObjectTable.LocalPlayer?.EntityId) return now - lastBusy;
        return stillSince.TryGetValue(actor.EntityId, out var s) ? now - s.Since : TimeSpan.Zero;
    }

    // ---- Treasure coffers ------------------------------------------------------------------

    private const float ChestReach = 8f; // yalms; you have to be next to a coffer to open it
    private static readonly TimeSpan ChestTargetMemory = TimeSpan.FromSeconds(3);

    /// <summary>Coffers we've seen, and whether each one was already open.</summary>
    private readonly Dictionary<ulong, bool> chestOpened = new();

    /// <summary>What each voiced character last had targeted, and when.</summary>
    private readonly Dictionary<uint, (ulong Target, DateTime At)> lastTarget = new();

    /// <summary>
    /// A treasure coffer opens, and whoever opened it says the line. Opening a coffer means
    /// targeting it and interacting from close by, so the opener is the voiced character (you, or
    /// a friend with a pack) who had it targeted in the last few seconds and is standing next to it.
    /// Coffers other people open stay silent. Coffers already open when they come into view are
    /// only recorded.
    /// </summary>
    private unsafe void CheckChests(List<Actor> actors, DateTime now)
    {
        foreach (var a in actors)
            if (a.Obj is { TargetObjectId: not 0 and not 0xE0000000 } o)
                lastTarget[a.EntityId] = (o.TargetObjectId, now);

        var seen = new HashSet<ulong>();
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.Treasure) continue;
            var t = (FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure*)obj.Address;
            if (t == null) continue;

            var id = obj.GameObjectId;
            seen.Add(id);
            var open = t->State != FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureState.Unopened ||
                       t->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags.Opened);

            if (chestOpened.TryGetValue(id, out var wasOpen) && !wasOpen && open)
            {
                var opener = actors.FirstOrDefault(a =>
                    a.Obj != null &&
                    lastTarget.TryGetValue(a.EntityId, out var lt) && (lt.Target == id || (obj.EntityId != 0 && (uint)lt.Target == obj.EntityId)) && now - lt.At <= ChestTargetMemory &&
                    System.Numerics.Vector3.Distance(a.Obj.Position, obj.Position) <= ChestReach);
                if (opener != null)
                {
                    var map = t->CofferKind == FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureKind.TreasureHunt;
                    Moment(opener, map ? "chest_map" : "chest_open", map ? MomentCofferMap : MomentCoffer);
                }
            }
            chestOpened[id] = open;
        }

        if (chestOpened.Count > seen.Count)
            foreach (var gone in chestOpened.Keys.Where(k => !seen.Contains(k)).ToList())
                chestOpened.Remove(gone);
    }

    // ---- Time of day, places, states ------------------------------------------------------

    private string currentPeriod = string.Empty;
    private string? currentPlace;
    private string? lastPeriod;

    /// <summary>
    /// The Eorzean time of day moves into a new period (dawn, morning...). Everyone sees the same
    /// clock, so one voice in the party comments. Not in duties or combat.
    /// </summary>
    private void CheckTimeOfDay(bool busy)
    {
        var (label, _, ev) = WorldState.CurrentPeriod();
        if (lastPeriod == null || label == lastPeriod)
        {
            lastPeriod = label;
            return;
        }
        lastPeriod = label;
        if (!busy) SayShared(ev);
    }

    private uint placeTerritory;

    /// <summary>Where you were last (place key), for "is this a different place?". Null right after login.</summary>
    private string? lastPlaceKey;
    private bool placeSeenOnce;

    /// <summary>Friends' last known zone (from the party list) and the place key they came from.</summary>
    private readonly Dictionary<uint, uint> friendTerritory = new();
    private readonly Dictionary<uint, string?> friendCameFrom = new();

    /// <summary>
    /// Arrivals close together (you and friends loading in together) are one round: when it's due,
    /// one of the arrivals speaks, picked the same way on every player's screen.
    /// </summary>
    private DateTime? arrivalDue;
    private readonly HashSet<uint> arrivals = new();

    /// <summary>
    /// Arriving in a different kind of place (city, inn, your home...). Your own arrival, and a
    /// friend's when they arrive where you are (they must be visible, so a friend across the world,
    /// or in another house in the same district, doesn't count). City to the same city is silent,
    /// and so is the first zone after logging in.
    /// </summary>
    private void CheckArrivals(DateTime now, IPlayerCharacter me, Actor? meActor, List<Actor> actors)
    {
        var territory = Plugin.ClientState.TerritoryType;
        if (territory != placeTerritory)
        {
            placeTerritory = territory;
            OpenArrival(now, me.EntityId);
        }

        // Friends: watch their zone in the party list.
        for (var i = 0; i < Plugin.PartyList.Length; i++)
        {
            var m = Plugin.PartyList[i];
            if (m == null || m.EntityId == me.EntityId || m.EntityId == 0) continue;
            var t = m.Territory.RowId;
            if (friendTerritory.TryGetValue(m.EntityId, out var was) && was != t)
            {
                friendCameFrom[m.EntityId] = WorldState.PlaceKeyOfTerritory(was);
                if (t == territory) OpenArrival(now, m.EntityId);
            }
            friendTerritory[m.EntityId] = t;
        }

        // After a loading screen or NPC talk the speaker stays quiet for a moment; wait it out
        // instead of spending the arrival on a line that would be dropped.
        if (Suppressed) return;
        RetryArrival(now);
        if (arrivalDue is not { } due || now < due) return;
        arrivalDue = null;

        var candidates = new List<(Actor Actor, string Event)>();
        foreach (var id in arrivals)
        {
            var isMe = id == me.EntityId;
            var who = isMe ? "you" : actors.FirstOrDefault(x => x.EntityId == id)?.CharacterId ?? $"party member {id}";
            var arrival = WorldState.Arrival(isMe);
            string? before;
            if (isMe)
            {
                before = lastPlaceKey;
                lastPlaceKey = arrival?.Key;
                if (!placeSeenOnce)
                {
                    placeSeenOnce = true; // the first place after logging in isn't an arrival
                    NoteArrival($"{who} in {arrival?.Key ?? "an unlisted place"} (first place after login, silent)");
                    continue;
                }
            }
            else
            {
                friendCameFrom.TryGetValue(id, out before);
            }

            if (arrival is not { } a)
            {
                NoteArrival($"{who}: {WorldState.CurrentPlace() ?? "this zone"} has no arrival lines (duty or unlisted place)");
                continue;
            }
            if (a.Key == before)
            {
                NoteArrival($"{who} in {a.Key}, same kind of place as before, silent");
                continue;
            }
            var actor = isMe ? meActor : actors.FirstOrDefault(x => x.EntityId == id && x.Obj != null);
            if (actor == null)
            {
                NoteArrival($"{who} arrived in {a.Key}, but {(isMe ? "your pack has no lines for this job" : "they aren't visible")}");
                continue;
            }
            var ev = a.Events.FirstOrDefault(e => HasLines(actor, e) || FindVariant(actor, e) != null);
            if (ev != null) candidates.Add((actor, ev));
            NoteArrival(ev != null
                ? $"{who} arrived in {a.Key} (from {before ?? "nowhere known"}), playing {ev}"
                : $"{who} arrived in {a.Key}, but there are no lines for {string.Join(" / ", a.Events)}");
        }
        arrivals.Clear();
        if (candidates.Count == 0) return;

        candidates.Sort((x, y) => string.CompareOrdinal(x.Actor.CharacterId, y.Actor.CharacterId));
        var seconds = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.GetServerTime();
        var pick = candidates[(int)(Fnv1a($"arrive|{candidates[0].Event}|{seconds / 10}") % (ulong)candidates.Count)];
        pendingArrival = null;
        if (!Say(pick.Actor, pick.Event))
        {
            // Something else was talking (or the pause between lines): keep trying for a little while.
            pendingArrival = (pick.Actor, pick.Event, now + ArrivalRetryFor, territory);
            NoteArrival($"{pick.Event} held back ({speaker.LastDrop ?? "unknown reason"}), will retry");
        }
    }

    /// <summary>An arrival line that couldn't play yet: who, what, until when, and in which zone.</summary>
    private (Actor Actor, string Event, DateTime Until, uint Territory)? pendingArrival;
    private static readonly TimeSpan ArrivalRetryFor = TimeSpan.FromSeconds(30);
    private DateTime nextArrivalRetry;

    private void RetryArrival(DateTime now)
    {
        if (pendingArrival is not { } p || now < nextArrivalRetry) return;
        nextArrivalRetry = now + TimeSpan.FromSeconds(1);
        if (p.Territory != Plugin.ClientState.TerritoryType)
        {
            pendingArrival = null; // moved on already
            return;
        }
        if (Say(p.Actor, p.Event))
        {
            pendingArrival = null;
            NoteArrival($"{p.Event} played on retry");
        }
        else if (now >= p.Until)
        {
            pendingArrival = null;
            NoteArrival($"{p.Event} gave up after {ArrivalRetryFor.TotalSeconds:0} s ({speaker.LastDrop ?? "unknown reason"})");
        }
    }

    private void OpenArrival(DateTime now, uint entityId)
    {
        arrivals.Add(entityId);
        arrivalDue ??= now + ZoneInDelay;
    }

    private static bool HasLines(Actor actor, string eventName) =>
        actor.Pack.LinesFor(actor.Job, eventName).Any(l => !string.IsNullOrWhiteSpace(l.Text) || l.AudioPath != null);

    /// <summary>Friends' states last tick (only what's visible on your screen).</summary>
    private readonly Dictionary<uint, HashSet<string>> friendStates = new();

    /// <summary>
    /// A friend near you starts swimming, mounts up, starts crafting... their own line plays.
    /// Diving, flying and fishing can't be seen on other players. Not in duties or combat.
    /// </summary>
    private void CheckFriendStates(List<Actor> actors, IPlayerCharacter me, bool busy)
    {
        foreach (var actor in actors)
        {
            if (actor.EntityId == me.EntityId || actor.Obj == null) continue;
            var states = WorldState.CurrentStates(actor.Obj, isMe: false);
            var seen = friendStates.TryGetValue(actor.EntityId, out var before);
            friendStates[actor.EntityId] = states;
            if (!seen || busy || states.Contains("In combat")) continue;

            foreach (var (label, ev) in WorldState.States)
            {
                if (ev == null || !states.Contains(label) || before!.Contains(label)) continue;
                Say(actor, ev);
                break;
            }
        }
    }

    private HashSet<string>? previousStates;

    /// <summary>
    /// You start swimming, mount up, cast a line... Your own line only (only your client knows what
    /// you're doing), and not in duties or combat. States already true after a loading screen
    /// don't count.
    /// </summary>
    private void CheckStates(Actor? meActor, IPlayerCharacter me, bool busy)
    {
        var states = WorldState.CurrentStates(me, isMe: true);
        var before = previousStates;
        previousStates = states;
        if (before == null || busy || meActor == null) return;

        foreach (var (label, ev) in WorldState.States)
        {
            if (ev == null || !states.Contains(label) || before.Contains(label)) continue;
            Say(meActor, ev);
            break; // one at a time
        }
    }

    // ---- Weather and idle ------------------------------------------------------------------

    private static bool InDuty() => WorldState.InDuty();

    /// <summary>What the last arrival check decided, for Settings > Testing.</summary>
    public static string LastArrival { get; private set; } = "No arrivals checked yet.";

    private static void NoteArrival(string text)
    {
        LastArrival = $"{DateTime.Now:t}: {text}";
        Plugin.Log.Warning($"Arrival: {text}"); // warning level so it shows with Dalamud's default log filter
    }

    private uint weatherTerritory;
    private byte lastWeather;
    private DateTime weatherSettleUntil;
    private static readonly TimeSpan WeatherSettle = TimeSpan.FromSeconds(10);

    // Zone-in: wait out the loading screen and scene grace, then look at the weather once.
    private DateTime? zoneInCheckAt;
    private static readonly TimeSpan ZoneInDelay = TimeSpan.FromSeconds(5);

    /// <summary>The weather you were last out in (outside duties), as a weather event name.</summary>
    private string? lastWeatherGroup;

    /// <summary>When each weather event last fired from a zone-in.</summary>
    private readonly Dictionary<string, DateTime> zoneInSpoken = new();
    private static readonly TimeSpan ZoneInRepeat = TimeSpan.FromMinutes(30);

    /// <summary>Weather worth remarking on when you arrive in it. Clear skies and clouds aren't.</summary>
    private static bool IsInclement(string ev) => ev is not ("weather_clear" or "weather_clouds");

    /// <summary>
    /// Weather lines. A change while you stay in a zone plays a line (if the kind of weather changed).
    /// Arriving in a zone also plays one, but only for inclement weather, only if you weren't already
    /// out in that kind of weather (teleporting from rain to rain is silent), and at most once per
    /// 30 minutes per kind. Nothing in duties or combat. Everyone in the zone sees the same weather,
    /// so one voice comments for the party.
    /// </summary>
    private unsafe void CheckWeather(DateTime now, bool busy)
    {
        var wm = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
        if (wm == null) return;
        var weather = wm->GetCurrentWeather();
        var territory = Plugin.ClientState.TerritoryType;
        var inDuty = InDuty();

        if (territory != weatherTerritory)
        {
            weatherTerritory = territory;
            lastWeather = weather;
            weatherSettleUntil = now + WeatherSettle;
            zoneInCheckAt = now + ZoneInDelay;
            return;
        }

        if (zoneInCheckAt is { } at && now >= at)
        {
            zoneInCheckAt = null;
            var ev = weather == 0 ? null : WeatherEventFor(weather);
            if (ev != null && !inDuty)
            {
                var already = ev == lastWeatherGroup ||
                              (zoneInSpoken.TryGetValue(ev, out var when) && now - when < ZoneInRepeat);
                if (!busy && !already && IsInclement(ev))
                {
                    zoneInSpoken[ev] = now;
                    SayShared(ev);
                }
                lastWeatherGroup = ev;
            }
        }

        if (weather == lastWeather) return;

        var before = lastWeather;
        lastWeather = weather;
        if (weather == 0) return;
        var changed = WeatherEventFor(weather);
        if (!inDuty && zoneInCheckAt == null) lastWeatherGroup = changed;
        if (busy || now < weatherSettleUntil || before == 0) return;

        // Fair Skies to Clear Skies (and the like) isn't worth a comment.
        if (changed != null && changed != WeatherEventFor(before))
            SayShared(changed);
    }

    private static string? WeatherEventFor(byte weatherId) =>
        KnownEvents.WeatherEvent(Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Weather>()
                                       .GetRowOrDefault(weatherId)?.Name.ExtractText() ?? string.Empty);

    private DateTime lastBusy = DateTime.UtcNow;
    private System.Numerics.Vector3? lastPosition;
    private const float MoveThreshold = 0.3f; // yalms; turning on the spot or emoting doesn't count

    /// <summary>
    /// True while you're standing around: not moving, not crafting or gathering. Any of those
    /// restarts the idle wait.
    /// </summary>
    private bool StandingStill()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null)
        {
            lastPosition = null;
            return false;
        }

        var pos = me.Position;
        var moved = lastPosition is not { } prev || System.Numerics.Vector3.Distance(prev, pos) > MoveThreshold;
        lastPosition = pos;

        return !moved &&
               !Plugin.Condition[ConditionFlag.Crafting] &&
               !Plugin.Condition[ConditionFlag.Gathering] &&
               !Plugin.Condition[ConditionFlag.Jumping];
    }
    private long lastIdleMinute = -1;

    private static int IdleChance(IdleFrequency f) => f switch
    {
        IdleFrequency.Rare => 3,
        IdleFrequency.Normal => 7,
        IdleFrequency.Frequent => 15,
        _ => 0,
    };

    /// <summary>
    /// Idle lines are personal: once a server minute, each voiced character who has been standing
    /// still long enough (you, or a friend near you) rolls for an idle line. The roll uses their name
    /// and the minute, and friends' packs carry their owner's idle settings, so your screen and
    /// theirs agree on when their character speaks. At most one idle line a minute.
    /// </summary>
    private unsafe void CheckIdle(DateTime now, bool busy, List<Actor> actors)
    {
        var minute = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.GetServerTime() / 60;
        if (minute == lastIdleMinute) return;
        lastIdleMinute = minute;
        if (busy) return;

        var cfg = Plugin.Instance.Configuration;
        if (cfg.IdleFrequency == IdleFrequency.Off) return; // you've switched idle chatter off
        var myId = Plugin.ObjectTable.LocalPlayer?.EntityId;

        foreach (var actor in actors.OrderBy(a => a.CharacterId, StringComparer.OrdinalIgnoreCase))
        {
            var isMe = actor.EntityId == myId;
            var freq = isMe ? cfg.IdleFrequency : actor.Pack.SharedIdleFrequency ?? cfg.IdleFrequency;
            var minutes = isMe ? cfg.IdleMinutes : actor.Pack.SharedIdleMinutes ?? cfg.IdleMinutes;
            var chance = IdleChance(freq);
            if (chance == 0) continue;
            if (!isMe && (actor.Obj == null || actor.Obj.StatusFlags.HasFlag(StatusFlags.InCombat))) continue;
            if (StillFor(actor) < TimeSpan.FromMinutes(Math.Max(1, minutes))) continue;
            if (Fnv1a($"idle|{actor.CharacterId.ToLowerInvariant()}|{minute}") % 100 >= (ulong)chance) continue;
            if (Say(actor, "idle")) return;
        }
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
    /// <summary>Whether one custom condition is true for this character right now.</summary>
    private bool ConditionTrue(Actor actor, CustomCondition c, ref IReadOnlyList<string>? moodles)
    {
        var isMe = actor.EntityId == Plugin.ObjectTable.LocalPlayer?.EntityId;
        var obj = actor.Obj;
        switch (c.Type)
        {
            case ConditionType.BuiltIn: return true; // checked by whoever is saying the built-in line
            case ConditionType.OpenCoffer:
                return c.Value switch
                {
                    CustomCondition.CofferMap => Recent(actor, MomentCofferMap),
                    CustomCondition.CofferRegular => Recent(actor, MomentCoffer),
                    _ => Recent(actor, MomentCoffer) || Recent(actor, MomentCofferMap),
                };
            case ConditionType.Weather:
                return currentWeatherLabel != null &&
                       string.Equals(currentWeatherLabel, c.Value.Trim(), StringComparison.OrdinalIgnoreCase);
            case ConditionType.Idle: return StillFor(actor) >= TimeSpan.FromMinutes(CustomCondition.IdleMinutes(c.Value));
            case ConditionType.JoinParty: return Recent(actor, MomentJoin);
            case ConditionType.TimeOfDay: return WorldState.Picks(c.Value).Contains(currentPeriod);
            case ConditionType.Place: return WorldState.Picks(c.Value).Overlaps(WorldState.CurrentPlaceLabels(isMe));
        }

        if (obj == null) return false; // the rest need the character on screen
        return c.Type switch
        {
            ConditionType.Cast => obj.IsCasting && NameMatches(ActionName(obj.CastActionId), c.Value),
            ConditionType.Status => c.Scope == ConditionScope.Target
                ? GaveStatus(actor, c.Value)
                : obj.StatusList.Any(st => NameMatches(StatusName(st.StatusId), c.Value)),
            ConditionType.Emote => EmoteNamesOf(obj).Any(n => NameMatches(n, c.Value)),
            ConditionType.Moodle => (moodles ??= MoodlesIpc.TitlesOn(obj.Address)).Any(t => NameMatches(t, MoodlesIpc.Clean(c.Value))),
            ConditionType.State => WorldState.Picks(c.Value).Overlaps(WorldState.CurrentStates(obj, isMe)),
            _ => false,
        };
    }

    /// <summary>
    /// A variant of a built-in event (a custom event with a "Built-in event" condition) whose other
    /// conditions are true right now and that this character has lines for on this job.
    /// </summary>
    private CustomEvent? FindVariant(Actor actor, string builtInEvent)
    {
        IReadOnlyList<string>? moodles = null;
        foreach (var ev in actor.Pack.CustomEvents)
        {
            if (!ev.Enabled || !ev.IsUsable || ev.VariantOf != builtInEvent) continue;
            if (!HasLines(actor, ev.Id)) continue;
            var a = actor;
            if (ev.Conditions.All(c => ConditionTrue(a, c, ref moodles))) return ev;
        }
        return null;
    }

    private void CheckCustomEvents(Actor actor)
    {
        if (actor.Obj == null || actor.Pack.CustomEvents.Count == 0)
            return;

        IReadOnlyList<string>? moodles = null; // fetched once per character per tick, only if needed
        var settled = firstSeen.TryGetValue(actor.EntityId, out var seenAt) && DateTime.UtcNow - seenAt >= MoodleGrace;

        foreach (var ev in actor.Pack.CustomEvents)
        {
            if (!ev.IsUsable || !ev.Enabled || ev.VariantOf != null) continue; // variants play from Say()

            var allTrue = ev.Conditions.All(c => ConditionTrue(actor, c, ref moodles));

            // During the grace period the current state is still recorded (so an already-active
            // Moodle never fires later), it just doesn't speak.
            var usesMoodles = ev.Conditions.Any(c => c.Type == ConditionType.Moodle);
            var maySpeak = baselined.Contains(actor.EntityId) && (!usesMoodles || settled);

            var key = (actor.EntityId, ev.Id);
            if (allTrue && customActive.Add(key) && maySpeak)
            {
                Say(actor, ev.Id);
                // A custom event built on a coffer or party join replaces the built-in line for it.
                foreach (var c in ev.Conditions)
                    if (c.Type is ConditionType.OpenCoffer or ConditionType.JoinParty)
                        pendingLines.RemoveAll(p => p.Actor.EntityId == actor.EntityId &&
                                                    (c.Type == ConditionType.JoinParty) == (p.Kind == MomentJoin));
            }
            else if (!allTrue)
                customActive.Remove(key);
        }
    }

    private List<Member> currentMembers = new();

    /// <summary>
    /// The actor put this status on someone else: any party member (or Duty Support NPC), or
    /// whoever the actor has targeted. Only statuses from the actor count.
    /// </summary>
    private bool GaveStatus(Actor actor, string wanted)
    {
        bool On(StatusList? list) =>
            list != null && list.Any(st => st.SourceId == actor.EntityId && NameMatches(StatusName(st.StatusId), wanted));

        foreach (var m in currentMembers)
            if (m.EntityId != actor.EntityId && On(m.Statuses))
                return true;

        var targetId = actor.Obj?.TargetObjectId ?? 0;
        if (targetId is 0 or 0xE0000000) return false;
        var target = Plugin.ObjectTable.SearchById(targetId) as IBattleChara;
        return target != null && target.EntityId != actor.EntityId && On(target.StatusList);
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

    private bool Say(Actor actor, string eventName, bool? initial = null)
    {
        // A variant of this built-in event whose conditions hold plays instead of it.
        if (!eventName.StartsWith("custom_", StringComparison.Ordinal) && FindVariant(actor, eventName) is { } variant)
            return speaker.Trigger(actor.Pack, variant.Id, actor.Job, Seed(actor.CharacterId, variant.Id),
                                   speakerEntityId: actor.EntityId) != null;
        return speaker.Trigger(actor.Pack, eventName, actor.Job, Seed(actor.CharacterId, eventName), initial: initial,
                               speakerEntityId: actor.EntityId) != null;
    }

    /// <summary>
    /// Shared events get exactly one speaker. Every player's plugin sorts the same voiced party
    /// members the same way and uses the same server-time window, so they all pick the same one.
    /// </summary>
    private void SayShared(string eventName, bool healersOnly = false)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return;

        var candidates = Actors(me)
            .Where(a => a.Pack.LinesFor(a.Job, eventName).Count > 0 || FindVariant(a, eventName) != null)
            .Where(a => !healersOnly || IsHealerJob(a.Job))
            .OrderBy(a => a.CharacterId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return;

        var seed = Seed("shared", eventName);
        var chosen = candidates[(int)((ulong)seed % (ulong)candidates.Count)];
        Say(chosen, eventName);
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

            // Party members elsewhere (another zone, or out of view) can't speak here: only
            // characters actually around you do, which is also what their own screen sees.
            if (m.GameObject is not IBattleChara obj || m.Territory.RowId != Plugin.ClientState.TerritoryType)
                continue;

            list.Add(new Actor(m.EntityId, id, job, pack, obj));
        }

        // Friends standing near you count too, party or not.
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter p || p.EntityId == me.EntityId || p.EntityId == 0) continue;
            if (System.Numerics.Vector3.Distance(p.Position, me.Position) > NearbyFriendRange) continue;
            if (list.Any(a => a.EntityId == p.EntityId)) continue;

            var world = p.HomeWorld.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrEmpty(world)) continue;
            var id = $"{p.Name.TextValue}@{world}";
            var job = JobAbbrev(p.ClassJob.RowId);
            if (job == null || speaker.FriendPack(id) is not { } pack || !speaker.SpeaksOn(pack, job))
                continue;

            list.Add(new Actor(p.EntityId, id, job, pack, p));
        }

        return list;
    }

    /// <summary>How close a friend outside your party has to be to count (yalms).</summary>
    private const float NearbyFriendRange = 40f;

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
        chestOpened.Clear();
        moments.Clear();
        stillSince.Clear();
        lastTarget.Clear();
        baselined.Clear();
        firstSeen.Clear();
        wasBigPull = false;
        bigPullSince = null;
        zoneInCheckAt = null;
        friendStates.Clear();
        previousStates = null;
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
