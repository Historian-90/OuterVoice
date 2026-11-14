using System;
using System.Collections.Generic;
using System.Linq;

namespace OuterVoice;

public enum ConditionType
{
    /// <summary>You're casting an action with this name (anything with a cast bar).</summary>
    Cast,

    /// <summary>You have a game status (buff/debuff) with this name, from anyone.</summary>
    Status,

    /// <summary>You're doing this emote.</summary>
    Emote,

    /// <summary>You have a Moodle (from the Moodles plugin) with this title.</summary>
    Moodle,
}

public class CustomCondition
{
    public ConditionType Type { get; set; } = ConditionType.Emote;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// A player-made event: plays a line the moment ALL of its conditions are true at once.
/// Saved in the voice pack, so friends who import the pack get it too.
/// </summary>
public class CustomEvent
{
    public string Id { get; set; } = "custom_" + Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New event";
    public List<CustomCondition> Conditions { get; set; } = new();
    public int CooldownSeconds { get; set; } = 30;
    public int Priority { get; set; } = 2;

    public bool IsUsable => Conditions.Count > 0 && Conditions.All(c => !string.IsNullOrWhiteSpace(c.Value));

    public string Summary =>
        Conditions.Count == 0
            ? "No conditions yet: set them up on the Custom Events tab."
            : "When " + string.Join(" + ", Conditions.Select(Describe));

    public static string Describe(CustomCondition c) => c.Type switch
    {
        ConditionType.Cast => $"casting {Quote(c.Value)}",
        ConditionType.Status => $"you have {Quote(c.Value)}",
        ConditionType.Emote => $"doing /{(c.Value.Length > 0 ? c.Value.TrimStart('/') : "?")}",
        ConditionType.Moodle => $"Moodle {Quote(c.Value)}",
        _ => c.Value,
    };

    private static string Quote(string s) => s.Length > 0 ? $"\"{s}\"" : "?";

    public KnownEvents.Info ToInfo() => new(Id, Name, Priority, CooldownSeconds, Summary);
}
