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
        ("Home", "zone_home"),
        ("House", "zone_house"),
        ("Apartment", "zone_apartment"),
        ("Gold Saucer", "zone_saucer"),
        ("Island Sanctuary", "zone_island"),
        ("Duty", null), // duties have their own events (duty start, clears...)
    ];

    /// <summary>
    /// Cities with their own arrival lines: (label, event, territory place names). An arrival in one
    /// of these plays its own line, or the plain City line if the pack has none for it.
    /// </summary>
    public static readonly (string Label, string Event, string[] Names)[] Cities =
    [
        ("Limsa Lominsa", "zone_city_limsa", ["Limsa Lominsa"]),
        ("Gridania", "zone_city_gridania", ["Gridania"]),
        ("Ul'dah", "zone_city_uldah", ["Ul'dah"]),
        ("Ishgard", "zone_city_ishgard", ["Foundation", "The Pillars"]),
        ("Idyllshire", "zone_city_idyllshire", ["Idyllshire"]),
        ("Rhalgr's Reach", "zone_city_rhalgr", ["Rhalgr's Reach"]),
        ("Kugane", "zone_city_kugane", ["Kugane"]),
        ("Doman Enclave", "zone_city_doma", ["Doman Enclave"]),
        ("The Crystarium", "zone_city_crystarium", ["Crystarium"]),
        ("Eulmore", "zone_city_eulmore", ["Eulmore"]),
        ("Old Sharlayan", "zone_city_sharlayan", ["Old Sharlayan"]),
        ("Radz-at-Han", "zone_city_radz", ["Radz-at-Han"]),
        ("Tuliyollal", "zone_city_tuliyollal", ["Tuliyollal"]),
        ("Solution Nine", "zone_city_solution9", ["Solution Nine"]),
    ];

    /// <summary>Options for the Place condition: kinds, then each city ("City: Kugane").</summary>
    public static string[] PlaceLabels(IEnumerable<string> registered)
    {
        var list = new List<string>();
        foreach (var (label, _) in Places)
        {
            list.Add(label);
            if (label == "City")
                foreach (var c in Cities) list.Add($"City: {c.Label}");
        }
        foreach (var r in registered) list.Add($"Registered: {r}");
        return list.ToArray();
    }

    public static string? PlaceEvent(string label) => Array.Find(Places, p => p.Label == label).Event;

    /// <summary>The city a territory belongs to, or null.</summary>
    public static (string Label, string Event)? CityOf(uint territoryId)
    {
        var name = TerritoryName(territoryId);
        if (name.Length == 0) return null;
        foreach (var c in Cities)
            foreach (var n in c.Names)
                if (name.Contains(n, StringComparison.OrdinalIgnoreCase))
                    return (c.Label, c.Event);
        return null;
    }

    public static string TerritoryName(uint territoryId) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText()
        ?? string.Empty;

    /// <summary>
    /// The kind of place a territory is, from game data alone (no house/apartment/home split:
    /// interiors count as "House"). Used for where a friend came from.
    /// </summary>
    public static string? KindOfTerritory(uint territoryId)
    {
        var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
        if (territory == null) return null;
        return (FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse)territory.Value.TerritoryIntendedUse.RowId switch
        {
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.Town => "City",
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.Overworld => "Field",
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.Inn => "Inn",
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.HousingOutdoor => "Residential area",
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.HousingIndoor => "House",
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.GoldSaucer => "Gold Saucer",
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.IslandSanctuary => "Island Sanctuary",
            _ => null,
        };
    }

    /// <summary>"City: Kugane" for a city with its own lines, else the kind of place. For "same place?" checks.</summary>
    public static string? PlaceKeyOfTerritory(uint territoryId)
    {
        var kind = KindOfTerritory(territoryId);
        return kind == "City" && CityOf(territoryId) is { } c ? $"City: {c.Label}" : kind;
    }

    // ---- Houses and homes ------------------------------------------------------------------------

    /// <summary>The house or apartment you're inside, if any.</summary>
    public static unsafe HouseId? CurrentHouse()
    {
        var hm = HousingManager.Instance();
        if (hm == null || !hm->IsInside()) return null;
        var id = hm->GetCurrentIndoorHouseId();
        return id.Id == 0 || id.Id == ulong.MaxValue ? null : id;
    }

    /// <summary>Same estate: same world, district and ward, and the same plot (or apartment room).</summary>
    public static bool SameHome(HouseId a, HouseId b)
    {
        if (a.WorldId != 0 && b.WorldId != 0 && a.WorldId != b.WorldId) return false;
        if (a.TerritoryTypeId != b.TerritoryTypeId || a.WardIndex != b.WardIndex || a.IsApartment != b.IsApartment)
            return false;
        return a.IsApartment
            ? a.ApartmentDivision == b.ApartmentDivision && a.RoomNumber == b.RoomNumber
            : a.PlotIndex == b.PlotIndex;
    }

    /// <summary>Your own house, FC house, private chambers, apartment or shared estates.</summary>
    public static unsafe bool IsOwnedByMe(HouseId here)
    {
        var hm = HousingManager.Instance();
        if (hm == null) return false;
        var owned = new List<HouseId>
        {
            HousingManager.GetOwnedHouseId(EstateType.PersonalEstate, 0),
            HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate, 0),
            HousingManager.GetOwnedHouseId(EstateType.PersonalChambers, 0),
            HousingManager.GetOwnedHouseId(EstateType.ApartmentRoom, 0),
            HousingManager.GetOwnedHouseId(EstateType.SharedEstate, 0),
            HousingManager.GetOwnedHouseId(EstateType.SharedEstate, 1),
        };
        foreach (var o in owned)
            if (o.Id != 0 && o.Id != ulong.MaxValue && SameHome(o, here)) return true;
        return false;
    }

    /// <summary>A readable name for a house: "Shirogane, Ward 7, Plot 34".</summary>
    public static string HouseName(HouseId id)
    {
        var district = TerritoryName(id.TerritoryTypeId);
        if (district.Length == 0) district = "Housing";
        return id.IsApartment
            ? $"{district}, Ward {id.WardIndex + 1}{(id.ApartmentDivision == 1 ? " (Subdivision)" : "")}, Apartment {id.RoomNumber}"
            : $"{district}, Ward {id.WardIndex + 1}, Plot {id.PlotIndex + 1}";
    }

    /// <summary>The registered place you're inside, if any.</summary>
    public static RegisteredPlace? RegisteredHere(HouseId? here)
    {
        if (here is not { } h) return null;
        foreach (var r in Plugin.Instance.Configuration.RegisteredPlaces)
            if (SameHome((HouseId)r.HouseId, h)) return r;
        return null;
    }

    /// <summary>Is this house home: for you (owned or registered as home), or for a friend (registered "friends live here").</summary>
    public static bool IsHome(HouseId? here, bool forMe)
    {
        if (here is not { } h) return false;
        var r = RegisteredHere(h);
        if (forMe) return IsOwnedByMe(h) || r is { Home: true };
        return r is { FriendsHome: true };
    }

    /// <summary>The kind of place you're in right now, or null for anywhere else (PvP, events...).</summary>
    public static unsafe string? CurrentPlace()
    {
        // Some places that aren't duties still set the game's "bound by duty" flag (Island Sanctuary,
        // for one), so the kind of zone decides first, and the flag only counts for anything else.
        if (KindHere() is { } kind) return kind;
        return BoundByDutyFlag() ? "Duty" : null;
    }

    private static bool BoundByDutyFlag() =>
        Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] ||
        Plugin.Condition[ConditionFlag.BoundByDuty95];

    /// <summary>In a real duty (dungeon, trial, raid...), not just a zone that sets the duty flag.</summary>
    public static bool InDuty() => BoundByDutyFlag() && KindHere() == null;

    /// <summary>For Settings > Testing: where the plugin thinks you are, and why.</summary>
    public static string Describe()
    {
        var id = Plugin.ClientState.TerritoryType;
        var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(id);
        var use = territory?.TerritoryIntendedUse.RowId ?? 0;
        var city = CityOf(id)?.Label;
        return $"Place: {CurrentPlace() ?? "(none)"}{(city != null ? $" ({city})" : "")}  |  zone {id} \"{TerritoryName(id)}\", " +
               $"kind {(FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse)use} ({use})  |  duty flag {(BoundByDutyFlag() ? "on" : "off")}, " +
               $"in a duty: {(InDuty() ? "yes" : "no")}";
    }

    private static unsafe string? KindHere()
    {
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

    /// <summary>Every Place label that's true right now, for you or for a friend who's here.</summary>
    public static HashSet<string> CurrentPlaceLabels(bool forMe)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kind = CurrentPlace();
        if (kind == null) return set;
        set.Add(kind);
        if (kind == "City" && CityOf(Plugin.ClientState.TerritoryType) is { } c) set.Add($"City: {c.Label}");
        if (kind is "House" or "Apartment")
        {
            var here = CurrentHouse();
            if (IsHome(here, forMe)) set.Add("Home");
            if (RegisteredHere(here) is { } r) set.Add($"Registered: {r.Name}");
        }
        return set;
    }

    /// <summary>
    /// Where you (or a friend who's here) just arrived: the key for "is this a different place?"
    /// checks, and the arrival events to try in order (most specific first).
    /// </summary>
    public static (string Key, string[] Events)? Arrival(bool forMe)
    {
        var kind = CurrentPlace();
        if (kind == null || kind == "Duty") return null;
        if (kind == "City" && CityOf(Plugin.ClientState.TerritoryType) is { } c)
            return ($"City: {c.Label}", [c.Event, "zone_city"]);
        if (kind is "House" or "Apartment")
        {
            var houseEvent = kind == "House" ? "zone_house" : "zone_apartment";
            return IsHome(CurrentHouse(), forMe)
                ? ("Home", ["zone_home", houseEvent])
                : (kind, [houseEvent]);
        }
        return PlaceEvent(kind) is { } ev ? (kind, [ev]) : null;
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
    /// character visibly shows: swimming, mounted, crafting, gathering, performing, weapon drawn,
    /// in combat (not diving, flying or fishing).
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
            if (chara != null)
            {
                if (chara->IsSwimming) set.Add("Swimming");
                if (chara->IsMounted()) set.Add("Mounted");
                switch (chara->Mode)
                {
                    case FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Crafting: set.Add("Crafting"); break;
                    case FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Gathering: set.Add("Gathering"); break;
                    case FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Performance: set.Add("Performing"); break;
                }
            }
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

    // ---------- Posture ----------

    public enum Pose { Standing, Sitting, Lying, Swimming }

    /// <summary>For Settings > Testing: the pose and the raw values it came from.</summary>
    public static unsafe string DescribePose(Dalamud.Game.ClientState.Objects.Types.IGameObject? who)
    {
        if (who == null || who.Address == 0) return "Pose: -";
        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)who.Address;
        return $"Pose: {PoseOf(who)} (posture {chara->EmoteController.GetPosture()}, mode {chara->Mode}, " +
               $"emote {chara->EmoteController.EmoteId})";
    }

    private static HashSet<ushort>? sitEmotes, lyingEmotes;

    private static void LoadPoseEmotes()
    {
        if (sitEmotes != null) return;
        sitEmotes = new();
        lyingEmotes = new();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>())
        {
            var cmd = row.TextCommand.ValueNullable?.Command.ExtractText().TrimStart('/').ToLowerInvariant();
            if (cmd is "sit" or "groundsit") sitEmotes.Add((ushort)row.RowId);
            else if (cmd is "doze") lyingEmotes.Add((ushort)row.RowId);
        }
    }

    /// <summary>
    /// Sitting (/sit, /groundsit, chairs), lying down (/doze, beds), swimming or diving, or standing.
    /// </summary>
    public static unsafe Pose PoseOf(Dalamud.Game.ClientState.Objects.Types.IGameObject? who)
    {
        if (who == null || who.Address == 0) return Pose.Standing;
        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)who.Address;
        var isMe = who.EntityId == Plugin.ObjectTable.LocalPlayer?.EntityId;
        if (isMe ? Plugin.Condition[ConditionFlag.Swimming] || Plugin.Condition[ConditionFlag.Diving] : chara->IsSwimming)
            return Pose.Swimming;

        // The game's own posture: covers beds and couches (any /doze pose), chairs and the ground.
        switch (chara->EmoteController.GetPosture())
        {
            case FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Posture.Dozing:
                return Pose.Lying;
            case FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Posture.SittingInChair:
            case FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Posture.SittingOnGround:
                return Pose.Sitting;
            case FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Posture.Swimming:
            case FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Posture.Diving:
                return Pose.Swimming;
        }

        // Backup: the emote being held.
        if (chara->Mode is FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.InPositionLoop
                        or FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.EmoteLoop)
        {
            LoadPoseEmotes();
            var emote = chara->EmoteController.EmoteId;
            if (lyingEmotes!.Contains(emote)) return Pose.Lying;
            if (sitEmotes!.Contains(emote)) return Pose.Sitting;
        }
        return Pose.Standing;
    }
}
