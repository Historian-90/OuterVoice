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

    // Per-job "big heal" triggers: statuses that appear on the caster (from the caster), and casts.
    // Names are English game names, looked up at load. Add jobs here as they get voice lines.
    private static readonly Dictionary<string, (string[] Statuses, string[] Casts)> BigHealNames = new()
    {
        ["SGE"] = (["Kerachole", "Holos", "Panhaima"], ["Pneuma"]),
        ["WHM"] = (["Temperance", "Liturgy of the Bell"], []),
        ["SCH"] = (["Seraphism"], []),
        ["AST"] = (["Neutral Sect", "Macrocosmos"], []),
    };

    private readonly Speaker speaker;
    private readonly HashSet<uint> kardionIds;
    private readonly HashSet<uint> raiseIds;
    private readonly Dictionary<string, (HashSet<uint> Statuses, HashSet<uint> Casts)> bigHealIds = new();

    private DateTime nextTick = DateTime.MinValue;
    private int deathsThisDuty;
    private bool wasBigPull;

    // Per-entity memory, so each event fires once on the moment it happens.
    private readonly Dictionary<uint, uint> lastHp = new();
    private readonly Dictionary<uint, DateTime> lowHpSince = new();
    private readonly HashSet<(uint Caster, uint Target)> raisesSeen = new();
    private readonly Dictionary<uint, uint> kardionHolder = new();      // caster -> holder
    private readonly HashSet<(uint Caster, uint Status)> bigHealsSeen = new();
    private readonly HashSet<uint> castingBigHeal = new();

    /// <summary>A party member (or you) who has a voice pack.</summary>
    private sealed record Actor(uint EntityId, string CharacterId, string Job, VoicePack Pack, IBattleChara? Obj);

    private sealed record Member(uint EntityId, uint Hp, uint MaxHp, bool IsTank, StatusList? Statuses);

    public EventWatcher(Speaker speaker)
    {
        this.speaker = speaker;
        kardionIds = StatusIdsNamed("Kardion");
        raiseIds = StatusIdsNamed("Raise");
        foreach (var (job, names) in BigHealNames)
            bigHealIds[job] = (StatusIdsNamed(names.Statuses), ActionIdsNamed(names.Casts));

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

        var me = Plugin.ObjectTable.LocalPlayer;
        var actors = me == null ? new List<Actor>() : Actors(me);
        if (me == null || actors.Count == 0)
        {
            ResetTransientState();
            return;
        }

        // Don't talk over cutscenes.
        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Plugin.Condition[ConditionFlag.WatchingCutscene])
            return;

        var members = Members(me).ToList();
        var byId = actors.ToDictionary(a => a.EntityId);

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

                // Kardion: track who holds each Sage's Kardion.
                if (kardionIds.Contains(s.StatusId))
                {
                    if (kardionHolder.TryGetValue(caster.EntityId, out var holder) && holder != m.EntityId)
                        Say(caster, "kardia_swap");
                    kardionHolder[caster.EntityId] = m.EntityId;
                }
            }

            // Forget raises that have been accepted/expired so the next one fires again.
            raisesSeen.RemoveWhere(r => r.Target == m.EntityId &&
                                        !m.Statuses.Any(s => raiseIds.Contains(s.StatusId) && s.SourceId == r.Caster));
        }

        foreach (var actor in actors)
            CheckBigHeal(actor);

        CheckBigPull(me);
    }

    private void CheckBigHeal(Actor actor)
    {
        if (actor.Obj == null || !bigHealIds.TryGetValue(actor.Job, out var ids))
            return;

        var casting = actor.Obj.IsCasting && ids.Casts.Contains(actor.Obj.CastActionId);
        if (casting && castingBigHeal.Add(actor.EntityId))
            Say(actor, "big_heal");
        else if (!casting)
            castingBigHeal.Remove(actor.EntityId);

        foreach (var statusId in ids.Statuses)
        {
            var has = actor.Obj.StatusList.Any(s => s.StatusId == statusId && s.SourceId == actor.EntityId);
            if (has && bigHealsSeen.Add((actor.EntityId, statusId)))
                Say(actor, "big_heal");
            else if (!has)
                bigHealsSeen.Remove((actor.EntityId, statusId));
        }
    }

    private void CheckBigPull(IPlayerCharacter me)
    {
        if (!Plugin.Condition[ConditionFlag.InCombat])
        {
            wasBigPull = false;
            return;
        }

        var count = Plugin.ObjectTable
            .OfType<IBattleNpc>()
            .Count(n => n.BattleNpcKind == BattleNpcSubKind.Combatant
                        && n.CurrentHp > 0
                        && (n.StatusFlags & StatusFlags.InCombat) != 0
                        && (n.Position - me.Position).Length() <= BigPullRange);

        var big = count >= BigPullCount;
        if (big && !wasBigPull)
            SayShared("big_pull");
        wasBigPull = big;
    }

    // ---- Who speaks ---------------------------------------------------------------------

    private void Say(Actor actor, string eventName) =>
        speaker.Trigger(actor.Pack, eventName, actor.Job, Seed(actor.CharacterId, eventName));

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
        speaker.Trigger(chosen.Pack, eventName, chosen.Job, Seed(chosen.CharacterId, eventName));
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
        kardionHolder.Clear();
        bigHealsSeen.Clear();
        castingBigHeal.Clear();
        wasBigPull = false;
    }

    // ---- Game data lookups -----------------------------------------------------------------

    private static string? JobAbbrev(uint classJobId) =>
        Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId)?.Abbreviation.ExtractText();

    private static bool IsTankJob(uint classJobId) =>
        Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId)?.Role == 1;

    private static bool IsHealerJob(string job) => job is "WHM" or "SCH" or "AST" or "SGE" or "CNJ";

    private static HashSet<uint> StatusIdsNamed(params string[] names) =>
        Plugin.DataManager.GetExcelSheet<Status>()
              .Where(r => names.Contains(r.Name.ExtractText()))
              .Select(r => r.RowId)
              .ToHashSet();

    private static HashSet<uint> ActionIdsNamed(params string[] names) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
              .Where(r => r.IsPlayerAction && names.Contains(r.Name.ExtractText()))
              .Select(r => r.RowId)
              .ToHashSet();
}
