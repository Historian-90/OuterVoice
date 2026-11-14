using System.Collections.Generic;
using System.Linq;

namespace OuterVoice;

public enum TriggerKind
{
    /// <summary>A status the character puts on themselves appears (e.g. a tank stance, a burst buff).</summary>
    SelfStatus,

    /// <summary>A status the character gives to one party member moves to a different member (Kardia, Dance Partner).</summary>
    PartnerSwap,

    /// <summary>A status from the character appears on any party member (Astrologian cards).</summary>
    GiveStatus,
}

/// <summary>One job-specific trigger: which statuses (or casts) mean this event happened for this job.</summary>
public sealed record JobTrigger(string Job, string Event, TriggerKind Kind, string[] Statuses, string[]? Casts = null);

/// <summary>
/// Job-specific triggers. Status and action names are the English in-game names; they're looked
/// up in the game's data when the plugin loads, so IDs never need updating after a patch.
/// A name that doesn't match anything is logged and simply never fires.
/// </summary>
public static class JobTriggers
{
    public static readonly IReadOnlyList<JobTrigger> All =
    [
        // ---- Tanks ----
        new("PLD", "stance_on", TriggerKind.SelfStatus, ["Iron Will"]),
        new("PLD", "invuln", TriggerKind.SelfStatus, ["Hallowed Ground"]),
        new("PLD", "burst", TriggerKind.SelfStatus, ["Fight or Flight"]),

        new("WAR", "stance_on", TriggerKind.SelfStatus, ["Defiance"]),
        new("WAR", "invuln", TriggerKind.SelfStatus, ["Holmgang"]),
        new("WAR", "burst", TriggerKind.SelfStatus, ["Inner Release"]),

        new("DRK", "stance_on", TriggerKind.SelfStatus, ["Grit"]),
        new("DRK", "invuln", TriggerKind.SelfStatus, ["Living Dead", "Walking Dead"]),
        new("DRK", "burst", TriggerKind.SelfStatus, ["Delirium"]),

        new("GNB", "stance_on", TriggerKind.SelfStatus, ["Royal Guard"]),
        new("GNB", "invuln", TriggerKind.SelfStatus, ["Superbolide"]),
        new("GNB", "burst", TriggerKind.SelfStatus, ["No Mercy"]),

        // ---- Healers ----
        new("WHM", "big_heal", TriggerKind.SelfStatus, ["Temperance", "Liturgy of the Bell"]),
        new("WHM", "burst", TriggerKind.SelfStatus, ["Presence of Mind"]),

        new("SCH", "big_heal", TriggerKind.SelfStatus, ["Seraphism"]),
        new("SCH", "burst", TriggerKind.SelfStatus, ["Dissipation"]),

        new("AST", "big_heal", TriggerKind.SelfStatus, ["Neutral Sect", "Macrocosmos"]),
        new("AST", "burst", TriggerKind.SelfStatus, ["Divination"]),
        new("AST", "card_play", TriggerKind.GiveStatus,
            ["The Balance", "The Spear", "The Arrow", "The Bole", "The Ewer", "The Spire"]),

        new("SGE", "big_heal", TriggerKind.SelfStatus, ["Kerachole", "Holos", "Panhaima"], ["Pneuma"]),
        new("SGE", "kardia_swap", TriggerKind.PartnerSwap, ["Kardion"]),

        // ---- Melee ----
        new("MNK", "burst", TriggerKind.SelfStatus, ["Brotherhood", "Riddle of Fire"]),
        new("DRG", "burst", TriggerKind.SelfStatus, ["Battle Litany", "Lance Charge"]),
        new("NIN", "burst", TriggerKind.SelfStatus, ["Ten Chi Jin", "Kassatsu"]),
        new("SAM", "burst", TriggerKind.SelfStatus, ["Meikyo Shisui"]),
        new("RPR", "burst", TriggerKind.SelfStatus, ["Arcane Circle"]),
        new("RPR", "enshroud", TriggerKind.SelfStatus, ["Enshrouded"]),
        new("VPR", "burst", TriggerKind.SelfStatus, ["Reawakened"]),

        // ---- Ranged ----
        new("BRD", "burst", TriggerKind.SelfStatus, ["Battle Voice", "Radiant Finale"]),
        new("BRD", "song", TriggerKind.SelfStatus, ["the Wanderer's Minuet", "Mage's Ballad", "Army's Paeon"]),
        new("MCH", "burst", TriggerKind.SelfStatus, ["Wildfire", "Hypercharged"]),
        new("DNC", "burst", TriggerKind.SelfStatus, ["Technical Finish", "Devilment"]),
        new("DNC", "dance_partner", TriggerKind.PartnerSwap, ["Dance Partner"]),

        // ---- Casters ----
        new("BLM", "burst", TriggerKind.SelfStatus, ["Ley Lines"]),
        new("SMN", "burst", TriggerKind.SelfStatus, ["Searing Light"]),
        new("RDM", "burst", TriggerKind.SelfStatus, ["Embolden", "Manafication"]),
        new("PCT", "burst", TriggerKind.SelfStatus, ["Starry Muse"]),
    ];

    /// <summary>Hover text listing what triggers an event, for one job or (for ALL) every job.</summary>
    public static string Describe(string eventName, string job)
    {
        var defs = All.Where(t => t.Event == eventName && (job == VoicePack.SharedJob || t.Job == job)).ToList();
        if (defs.Count == 0)
            return string.Empty;

        if (job == VoicePack.SharedJob)
        {
            return "Watches for these, depending on the job you're playing:\n\n" +
                   string.Join("\n", defs.GroupBy(d => d.Job).Select(g => $"{g.Key}: {string.Join(", ", g.SelectMany(Names))}"));
        }

        var lines = new List<string>();
        foreach (var d in defs)
        {
            switch (d.Kind)
            {
                case TriggerKind.SelfStatus:
                    lines.Add("Plays when one of these goes up on you:");
                    lines.AddRange(d.Statuses.Select(n => "  \u2022 " + n));
                    if (d.Casts is { Length: > 0 })
                    {
                        lines.Add("...or when you start casting:");
                        lines.AddRange(d.Casts.Select(n => "  \u2022 " + n));
                    }
                    break;
                case TriggerKind.PartnerSwap:
                    lines.Add($"Watches your {string.Join(" / ", d.Statuses)} on party members.");
                    lines.Add("Lines marked Initial play when you first choose a partner;");
                    lines.Add("the others play when you switch to someone else.");
                    break;
                case TriggerKind.GiveStatus:
                    lines.Add("Plays when you give a party member one of these:");
                    lines.AddRange(d.Statuses.Select(n => "  \u2022 " + n));
                    break;
            }
        }

        lines.Add("");
        lines.Add("A buff that's already up when you zone in doesn't count.");
        return string.Join("\n", lines);
    }

    private static IEnumerable<string> Names(JobTrigger t) =>
        t.Statuses.Concat((t.Casts ?? []).Select(c => c + " (cast)"));

    /// <summary>Jobs that have a trigger for this event (null = an event every job has).</summary>
    public static string[]? JobsFor(string eventName)
    {
        var jobs = All.Where(t => t.Event == eventName).Select(t => t.Job).Distinct().ToArray();
        return jobs.Length == 0 ? null : jobs;
    }
}
