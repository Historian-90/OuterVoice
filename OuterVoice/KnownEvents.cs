using System.Collections.Generic;
using System.Linq;

namespace OuterVoice;

/// <summary>The events the plugin can detect, with default timing. Packs may override timing.</summary>
public static class KnownEvents
{
    public record Info(string Name, string Label, int Priority, int CooldownSeconds, string Trigger,
                       string? Category = null, bool IsCustom = false)
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
        // Every job: combat and duties
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

        // Every job: weather. Fires when the weather changes while you're out in the world, or when
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

        // Every job: idle
        new("idle", "Idle", 1, 300, "Every now and then, after a long stretch out of combat and out of duties. Set how often under Settings.", "Idle"),

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

    /// <summary>Subsections of EVERY JOB, in display order.</summary>
    public static readonly string[] CommonCategories = ["Combat & duties", "Weather", "Idle"];

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
