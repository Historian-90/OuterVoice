using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace OuterVoice;

/// <summary>
/// Time of day (Eorzea time), the kind of place you're in, and what you're doing (swimming,
/// mounted...). Used by the Time / Places / State events and by the matching custom conditions.
/// </summary>
public static class WorldState
{
    // ---- Time of day ----------------------------------------------------------------------------

    /// <summary>Periods of the Eorzean day, in order, with the hour each one starts.</summary>
    public static readonly (string Label, int StartHour, string Event)[] Periods =
    [
        ("Dawn", 5, "time_dawn"),       // 5:00-6:59
        ("Morning", 7, "time_morning"), // 7:00-11:59
        ("Day", 12, "time_day"),        // 12:00-16:59
        ("Dusk", 17, "time_dusk"),      // 17:00-18:59
        ("Evening", 19, "time_evening"),// 19:00-21:59
        ("Night", 22, "time_night"),    // 22:00-4:59
    ];

    public static string[] PeriodLabels => Array.ConvertAll(Periods, p => p.Label);

    /// <summary>The Eorzean hour (0-23), from the server clock. One Eorzean hour is 175 real seconds.</summary>
    public static int EorzeaHour()
    {
        var server = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.GetServerTime();
        return (int)(server * 3600 / 175 / 3600 % 24);
    }

    public static (string Label, int StartHour, string Event) CurrentPeriod()
    {
        var hour = EorzeaHour();
        var current = Periods[^1]; // Night wraps past midnight
        foreach (var p in Periods)
            if (hour >= p.StartHour) current = p;
        return current;
    }

    // ---- Places -----------------------------------------------------------------------------------

    /// <summary>Kinds of place, with their arrival event (null = condition only).</summary>
    public static readonly (string Label, string? Event)[] Places =
    [
        ("City", "zone_city"),
        ("Field", "zone_field"),
        ("Inn", "zone_inn"),
        ("Residential area", "zone_residential"),
        ("House", "zone_house"),
        ("Apartment", "zone_apartment"),
        ("Gold Saucer", "zone_saucer"),
        ("Island Sanctuary", "zone_island"),
        ("Duty", null), // duties have their own events (duty start, clears...)
    ];

    public static string[] PlaceLabels => Array.ConvertAll(Places, p => p.Label);

    public static string? PlaceEvent(string label) => Array.Find(Places, p => p.Label == label).Event;

    /// <summary>The kind of place you're in right now, or null for anywhere else (PvP, events...).</summary>
    public static unsafe string? CurrentPlace()
    {
        if (Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] ||
            Plugin.Condition[ConditionFlag.BoundByDuty95])
            return "Duty";

        var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType);
        if (territory == null) return null;

        switch ((FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse)territory.Value.TerritoryIntendedUse.RowId)
        {
            case FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.Town: return "City";
            case FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.Overworld: return "Field";
            case FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.Inn: return "Inn";
            case FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.HousingOutdoor: return "Residential area";
            case FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.HousingIndoor:
                var housing = HousingManager.Instance();
                return housing != null && housing->IsInside() && housing->GetCurrentIndoorHouseId().IsApartment
                    ? "Apartment"
                    : "House";
            case FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.GoldSaucer: return "Gold Saucer";
            case FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.IslandSanctuary: return "Island Sanctuary";
            default: return null;
        }
    }

    // ---- What you're doing ----------------------------------------------------------------------

    /// <summary>States, with their "starts doing it" event (null = condition only).</summary>
    public static readonly (string Label, string? Event)[] States =
    [
        ("Swimming", "state_swim"),
        ("Diving", "state_dive"),
        ("Mounted", "state_mount"),
        ("Flying", "state_fly"),
        ("Crafting", "state_craft"),
        ("Gathering", "state_gather"),
        ("Fishing", "state_fish"),
        ("Performing", "state_perform"),
        ("Weapon drawn", "state_weapon"),
        ("In combat", null), // combat has its own events
    ];

    public static string[] StateLabels => Array.ConvertAll(States, s => s.Label);

    /// <summary>
    /// What you're doing now. Your own client knows everything; for a friend, only what their
    /// character visibly shows (mounted, weapon drawn, in combat) can be read.
    /// </summary>
    public static unsafe HashSet<string> CurrentStates(IBattleChara? who, bool isMe)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (who == null) return set;

        if (isMe)
        {
            var c = Plugin.Condition;
            if (c[ConditionFlag.Diving]) set.Add("Diving");
            else if (c[ConditionFlag.Swimming]) set.Add("Swimming");
            if (c[ConditionFlag.InFlight]) set.Add("Flying");
            else if (c[ConditionFlag.Mounted]) set.Add("Mounted");
            if (c[ConditionFlag.Crafting] || c[ConditionFlag.PreparingToCraft]) set.Add("Crafting");
            if (c[ConditionFlag.Fishing]) set.Add("Fishing");
            else if (c[ConditionFlag.Gathering]) set.Add("Gathering");
            if (c[ConditionFlag.Performing]) set.Add("Performing");
            if (c[ConditionFlag.InCombat]) set.Add("In combat");
        }
        else
        {
            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)who.Address;
            if (chara != null && chara->IsMounted()) set.Add("Mounted");
            if (who.StatusFlags.HasFlag(StatusFlags.InCombat)) set.Add("In combat");
        }

        if (who.StatusFlags.HasFlag(StatusFlags.WeaponOut) && !set.Contains("Crafting") &&
            !set.Contains("Gathering") && !set.Contains("Fishing"))
            set.Add("Weapon drawn");
        return set;
    }

    /// <summary>Splits a multi-pick condition value ("Dawn,Dusk").</summary>
    public static HashSet<string> Picks(string value) =>
        new(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
}
