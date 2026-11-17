using System.Collections.Generic;
using System.Linq;

namespace OuterVoice;

/// <summary>The events the plugin can detect, with default timing. Packs may override timing.</summary>
public static class KnownEvents
{
    public record Info(string Name, string Label, int Priority, int CooldownSeconds, string Trigger,
                       string? Category = null, bool IsCustom = false, string? Group = null)
    {
        /// <summary>Jobs this event exists for, or null if every job has it.</summary>
        public string[]? Jobs => JobTriggers.JobsFor(Name);

        /// <summary>
        /// Whether the event is offered when editing this job's lines. Job abilities aren't offered
        /// on ALL (each job has its own); the editor still shows any ALL lines written for them earlier.
        /// </summary>
        public bool AppliesTo(string job) =>
            Jobs == null ? true : job != VoicePack.SharedJob && Jobs.Contains(job);

        /// <summary>Partner events can have separate lines for first choosing a partner.</summary>
        public bool SupportsInitial => JobTriggers.All.Any(t => t.Event == Name && t.Kind == TriggerKind.PartnerSwap);
    }

    public static readonly IReadOnlyList<Info> All =
    [
        // General events: combat and duties
        new("duty_start", "Duty start", 2, 0, "A duty commences.", "Combat & duties"),
        new("clutch_save", "Clutch save", 4, 45, "Someone drops under 15% HP and is back above 50% within 3 seconds.", "Combat & duties"),
        new("tank_death", "Tank death", 4, 30, "A tank in your party dies.", "Combat & duties"),
        new("raise", "Raise", 3, 30, "Someone gets a Raise from you.", "Combat & duties"),
        new("self_death", "You die", 4, 0, "You die.", "Combat & duties"),
        new("wipe", "Wipe", 5, 0, "The party wipes.", "Combat & duties"),
        new("big_pull", "Big pull", 1, 180, "5+ real enemies (targetable and hostile) in combat within 30 yalms for over a second.", "Combat & duties"),
        new("duty_complete", "Duty complete", 3, 0, "Duty cleared with at least one death.", "Combat & duties"),
        new("duty_complete_clean", "Clean clear", 3, 0, "Duty cleared with no deaths.", "Combat & duties"),
        new("limit_break_1", "Limit Break 1", 2, 0, "You start casting a level 1 Limit Break.", "Combat & duties"),
        new("limit_break_2", "Limit Break 2", 3, 0, "You start casting a level 2 Limit Break.", "Combat & duties"),
        new("limit_break_3", "Limit Break 3", 5, 0, "You start casting a level 3 Limit Break.", "Combat & duties"),

        // General events: social
        new("party_join", "Joining a party", 2, 60, "You join someone's party. Friends with packs say their own line when they join yours.", "Social"),

        // General events: places (you arrive somewhere of a different kind than before)
        new("zone_city", "City", 1, 600, "You arrive in a city. Also plays for the cities below that have no lines of their own.", "Places", Group: "Cities"),
        new("zone_city_limsa", "City: Limsa Lominsa", 1, 600, "You arrive in Limsa Lominsa.", "Places", Group: "Cities"),
        new("zone_city_gridania", "City: Gridania", 1, 600, "You arrive in Gridania.", "Places", Group: "Cities"),
        new("zone_city_uldah", "City: Ul'dah", 1, 600, "You arrive in Ul'dah.", "Places", Group: "Cities"),
        new("zone_city_ishgard", "City: Ishgard", 1, 600, "You arrive in Ishgard (Foundation, The Pillars).", "Places", Group: "Cities"),
        new("zone_city_idyllshire", "City: Idyllshire", 1, 600, "You arrive in Idyllshire.", "Places", Group: "Cities"),
        new("zone_city_rhalgr", "City: Rhalgr's Reach", 1, 600, "You arrive in Rhalgr's Reach.", "Places", Group: "Cities"),
        new("zone_city_kugane", "City: Kugane", 1, 600, "You arrive in Kugane.", "Places", Group: "Cities"),
        new("zone_city_doma", "City: Doman Enclave", 1, 600, "You arrive in the Doman Enclave.", "Places", Group: "Cities"),
        new("zone_city_crystarium", "City: The Crystarium", 1, 600, "You arrive in the Crystarium.", "Places", Group: "Cities"),
        new("zone_city_eulmore", "City: Eulmore", 1, 600, "You arrive in Eulmore.", "Places", Group: "Cities"),
        new("zone_city_sharlayan", "City: Old Sharlayan", 1, 600, "You arrive in Old Sharlayan.", "Places", Group: "Cities"),
        new("zone_city_radz", "City: Radz-at-Han", 1, 600, "You arrive in Radz-at-Han.", "Places", Group: "Cities"),
        new("zone_city_tuliyollal", "City: Tuliyollal", 1, 600, "You arrive in Tuliyollal.", "Places", Group: "Cities"),
        new("zone_city_solution9", "City: Solution Nine", 1, 600, "You arrive in Solution Nine.", "Places", Group: "Cities"),
        new("zone_field", "Field", 1, 600, "You arrive in an open field zone (from a different kind of place).", "Places"),
        new("zone_inn", "Inn", 1, 600, "You enter an inn room.", "Places"),
        new("zone_residential", "Residential area", 1, 600, "You arrive in a residential district.", "Places", Group: "Housing"),
        new("zone_home", "Home", 1, 600, "You enter your own house, FC house, chambers or apartment, or a place registered as home (Settings > Homes). Plays the House/Apartment line if this has none.", "Places", Group: "Housing"),
        new("zone_house", "House", 1, 600, "You enter someone else's house (or its workshop).", "Places", Group: "Housing"),
        new("zone_apartment", "Apartment", 1, 600, "You enter someone else's apartment.", "Places", Group: "Housing"),
        new("zone_saucer", "Gold Saucer", 1, 600, "You arrive at the Gold Saucer.", "Places"),
        new("zone_island", "Island Sanctuary", 1, 600, "You arrive at your Island Sanctuary.", "Places"),

        // General events: state (you start doing something)
        new("state_swim", "Swimming", 1, 600, "You start swimming.", "State"),
        new("state_dive", "Diving", 1, 600, "You dive underwater.", "State"),
        new("state_mount", "Mounted", 1, 600, "You get on a mount.", "State"),
        new("state_fly", "Flying", 1, 600, "Your mount takes off.", "State"),
        new("state_craft", "Crafting", 1, 600, "You start crafting.", "State"),
        new("state_gather", "Gathering", 1, 600, "You start gathering (mining, botany).", "State"),
        new("state_fish", "Fishing", 1, 600, "You cast a line.", "State"),
        new("state_perform", "Performing", 1, 600, "You start playing an instrument (Perform).", "State"),
        new("state_weapon", "Weapon drawn", 1, 600, "You draw your weapon outside combat.", "State"),

        // General events: time of day (Eorzea time)
        new("time_dawn", "Dawn", 1, 1800, "Eorzea time reaches dawn (5:00).", "Time"),
        new("time_morning", "Morning", 1, 1800, "Eorzea time reaches morning (7:00).", "Time"),
        new("time_day", "Day", 1, 1800, "Eorzea time reaches midday (12:00).", "Time"),
        new("time_dusk", "Dusk", 1, 1800, "Eorzea time reaches dusk (17:00).", "Time"),
        new("time_evening", "Evening", 1, 1800, "Eorzea time reaches evening (19:00).", "Time"),
        new("time_night", "Night", 1, 1800, "Eorzea time reaches night (22:00).", "Time"),

        // General events: treasure
        new("chest_open", "Treasure coffer", 2, 20, "You open a treasure coffer (dungeons, raids, and anywhere else). Coffers other people open don't count.", "Treasure"),
        new("chest_map", "Treasure map coffer", 3, 20, "You open a treasure map coffer.", "Treasure"),

        // General events: weather. Fires when the weather changes while you're out in the world, or when
        // you arrive somewhere with inclement weather you weren't already in (not in duties or combat).
        new("weather_clear", "Clear skies", 1, 900, "The weather turns to Clear Skies or Fair Skies.", "Weather"),
        new("weather_clouds", "Clouds", 1, 900, "The weather turns cloudy (Clouds).", "Weather"),
        new("weather_fog", "Fog", 1, 900, "Fog rolls in.", "Weather"),
        new("weather_wind", "Wind", 1, 900, "The wind picks up (Wind, Gales).", "Weather"),
        new("weather_rain", "Rain", 1, 900, "It starts to rain (Rain, Showers).", "Weather"),
        new("weather_thunder", "Thunder", 1, 900, "A storm rolls in (Thunder, Thunderstorms).", "Weather"),
        new("weather_snow", "Snow", 1, 900, "It starts to snow (Snow, Blizzards).", "Weather"),
        new("weather_heat", "Heat", 1, 900, "It gets hot (Hot Spells, Heat Waves).", "Weather"),
        new("weather_dust", "Dust", 1, 900, "Dust kicks up (Dust Storms, Sandstorms).", "Weather"),
        new("weather_gloom", "Gloom", 1, 900, "Gloom, umbral weather or anything stranger (Auroras, Moon Dust...).", "Weather"),

        // General events: idle
        new("idle", "Idle", 1, 300, "Every now and then, after you've stood still for a while (no moving, combat, duties, crafting or gathering). Set how often under Settings.", "Idle"),

        // Job-specific (which jobs have them is defined in JobTriggers)
        new("burst", "Burst window", 2, 100, "You pop your big damage or raid buff."),
        new("big_heal", "Big heal", 2, 60, "You use one of your big party heals or mitigations."),
        new("stance_on", "Tank stance", 1, 300, "You turn on your tank stance."),
        new("invuln", "Invulnerability", 4, 60, "You use your invulnerability (Hallowed Ground, Holmgang, Living Dead, Superbolide)."),
        new("kardia_swap", "Kardia", 1, 120, "You place Kardia on a party member, or move it to someone else."),
        new("card_play", "Card play", 1, 60, "You play a card on a party member."),
        new("dance_partner", "Dance partner", 1, 120, "You choose a Dance Partner, or switch to someone else."),
        new("song", "Song", 1, 100, "You start a new song."),
        new("enshroud", "Enshroud", 1, 90, "You enter Enshroud."),
    ];

    /// <summary>Subsections of GENERAL EVENTS, in display order.</summary>
    public static readonly string[] CommonCategories = ["Combat & duties", "Social", "Treasure", "Places", "State", "Time", "Weather", "Idle"];

    /// <summary>
    /// The weather event for a weather name from the game's Weather sheet. Anything not listed
    /// (Gloom, Umbral Wind, Auroras, Moon Dust...) counts as gloom.
    /// </summary>
    public static string? WeatherEvent(string weatherName)
    {
        if (string.IsNullOrWhiteSpace(weatherName)) return null;
        var n = weatherName.ToLowerInvariant();
        if (n.Contains("umbral")) return "weather_gloom";
        if (n.Contains("clear") || n.Contains("fair")) return "weather_clear";
        if (n.Contains("thunder") || n.Contains("storm cloud")) return "weather_thunder";
        if (n.Contains("dust") && !n.Contains("moon") || n.Contains("sandstorm")) return "weather_dust";
        if (n.Contains("cloud")) return "weather_clouds";
        if (n.Contains("fog") || n.Contains("mist")) return "weather_fog";
        if (n.Contains("wind") || n.Contains("gale")) return "weather_wind";
        if (n.Contains("rain") || n.Contains("shower")) return "weather_rain";
        if (n.Contains("snow") || n.Contains("blizzard")) return "weather_snow";
        if (n.Contains("heat") || n.Contains("hot spell")) return "weather_heat";
        return "weather_gloom";
    }

    public static Info? Get(string name) => All.FirstOrDefault(e => e.Name == name);

    /// <summary>Combat jobs a pack can have lines for, plus the shared group.</summary>
    public static readonly string[] Jobs =
    [
        VoicePack.SharedJob,
        "PLD", "WAR", "DRK", "GNB",
        "WHM", "SCH", "AST", "SGE",
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "BRD", "MCH", "DNC",
        "BLM", "SMN", "RDM", "PCT", "BLU",
    ];

    public static readonly string[] Moods = ["playful", "serious", "tense"];
}
