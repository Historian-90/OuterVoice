using System.Collections.Generic;
using System.Linq;

namespace OuterVoice;

/// <summary>The events the plugin can detect, with default timing. Packs may override timing.</summary>
public static class KnownEvents
{
    public record Info(string Name, string Label, int Priority, int CooldownSeconds, string Trigger, string? OnlyJob = null);

    public static readonly IReadOnlyList<Info> All =
    [
        new("duty_start", "Duty start", 2, 0, "A duty commences."),
        new("kardia_swap", "Kardia swap", 1, 120, "Your Kardion moves from one party member to another.", "SGE"),
        new("big_heal", "Big heal", 2, 60, "You use one of your job's big party heals or mitigations."),
        new("clutch_save", "Clutch save", 4, 45, "Someone drops under 15% HP and is back above 50% within 3 seconds."),
        new("tank_death", "Tank death", 4, 30, "A tank in your party dies."),
        new("raise", "Raise", 3, 30, "Someone gets a Raise from you."),
        new("self_death", "You die", 4, 0, "You die."),
        new("wipe", "Wipe", 5, 0, "The party wipes."),
        new("big_pull", "Big pull", 1, 180, "5+ enemies in combat within 30 yalms."),
        new("duty_complete", "Duty complete", 3, 0, "Duty cleared with at least one death."),
        new("duty_complete_clean", "Clean clear", 3, 0, "Duty cleared with no deaths."),
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
