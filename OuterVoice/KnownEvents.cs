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

        public bool AppliesTo(string job) =>
            Jobs == null || job == VoicePack.SharedJob || Jobs.Contains(job);

        /// <summary>Partner events can have separate lines for first choosing a partner.</summary>
        public bool SupportsInitial => JobTriggers.All.Any(t => t.Event == Name && t.Kind == TriggerKind.PartnerSwap);
    }

    public static readonly IReadOnlyList<Info> All =
    [
        // Every job
        new("duty_start", "Duty start", 2, 0, "A duty commences."),
        new("clutch_save", "Clutch save", 4, 45, "Someone drops under 15% HP and is back above 50% within 3 seconds."),
        new("tank_death", "Tank death", 4, 30, "A tank in your party dies."),
        new("raise", "Raise", 3, 30, "Someone gets a Raise from you."),
        new("self_death", "You die", 4, 0, "You die."),
        new("wipe", "Wipe", 5, 0, "The party wipes."),
        new("big_pull", "Big pull", 1, 180, "5+ enemies in combat within 30 yalms."),
        new("duty_complete", "Duty complete", 3, 0, "Duty cleared with at least one death."),
        new("duty_complete_clean", "Clean clear", 3, 0, "Duty cleared with no deaths."),
        new("limit_break_1", "Limit Break 1", 2, 0, "You start casting a level 1 Limit Break."),
        new("limit_break_2", "Limit Break 2", 3, 0, "You start casting a level 2 Limit Break."),
        new("limit_break_3", "Limit Break 3", 5, 0, "You start casting a level 3 Limit Break."),

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
