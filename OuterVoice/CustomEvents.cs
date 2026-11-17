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

    /// <summary>You just opened a treasure coffer. Value: "any", "regular" or "map".</summary>
    OpenCoffer,

    /// <summary>The current weather is of this kind. Value: a weather label ("Rain", "Snow"...).</summary>
    Weather,

    /// <summary>You've been standing still (out of combat and duties) for at least this many minutes.</summary>
    Idle,

    /// <summary>You just joined a party.</summary>
    JoinParty,
}

/// <summary>Who a "Has status" condition looks at.</summary>
public enum ConditionScope
{
    /// <summary>The status is on you (from anyone).</summary>
    You,

    /// <summary>
    /// You put the status on someone else: a party member (or Duty Support NPC), or your current
    /// target. For targeted buffs like Haima.
    /// </summary>
    Target,
}

public class CustomCondition
{
    public ConditionType Type { get; set; } = ConditionType.Emote;
    public string Value { get; set; } = string.Empty;

    /// <summary>For "Has status": on you, or on someone you gave it to.</summary>
    public ConditionScope Scope { get; set; } = ConditionScope.You;

    /// <summary>The value a newly picked condition type starts with (empty means "type something").</summary>
    public static string DefaultValue(ConditionType type) => type switch
    {
        ConditionType.OpenCoffer => CofferAny,
        ConditionType.Weather => "Rain",
        ConditionType.Idle => "5",
        ConditionType.JoinParty => "yes",
        _ => string.Empty,
    };

    public const string CofferAny = "any";
    public const string CofferRegular = "regular";
    public const string CofferMap = "map";

    /// <summary>Weather labels a condition can pick, matching the weather events.</summary>
    public static string[] WeatherLabels =>
        KnownEvents.All.Where(e => e.Category == "Weather").Select(e => e.Label).ToArray();

    public static int IdleMinutes(string value) => int.TryParse(value, out var m) ? Math.Clamp(m, 1, 60) : 5;
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

    /// <summary>Optional grouping shown on the Lines and Custom Events tabs ("Emotes", "RP"...).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Switched-off events are kept but never trigger.</summary>
    public bool Enabled { get; set; } = true;

    public string CategoryOrDefault => string.IsNullOrWhiteSpace(Category) ? "Other" : Category.Trim();

    public bool IsUsable => Conditions.Count > 0 && Conditions.All(c => !string.IsNullOrWhiteSpace(c.Value));

    public string Summary =>
        Conditions.Count == 0
            ? "No conditions yet: set them up on the Custom Events tab."
            : "When " + string.Join(" + ", Conditions.Select(Describe));

    public static string Describe(CustomCondition c) => c.Type switch
    {
        ConditionType.Cast => $"casting {Quote(c.Value)}",
        ConditionType.Status => c.Scope == ConditionScope.Target
            ? $"you give {Quote(c.Value)} to someone"
            : $"you have {Quote(c.Value)}",
        ConditionType.Emote => $"doing /{(c.Value.Length > 0 ? c.Value.TrimStart('/') : "?")}",
        ConditionType.Moodle => $"Moodle {Quote(c.Value)}",
        ConditionType.OpenCoffer => c.Value switch
        {
            CustomCondition.CofferMap => "you open a treasure map coffer",
            CustomCondition.CofferRegular => "you open a coffer (not a map coffer)",
            _ => "you open a coffer",
        },
        ConditionType.Weather => $"the weather is {(c.Value.Length > 0 ? c.Value.ToLowerInvariant() : "?")}",
        ConditionType.Idle => $"standing still {CustomCondition.IdleMinutes(c.Value)}+ min",
        ConditionType.JoinParty => "you join a party",
        _ => c.Value,
    };

    private static string Quote(string s) => s.Length > 0 ? $"\"{s}\"" : "?";

    public KnownEvents.Info ToInfo() =>
        new(Id, Enabled ? Name : Name + " (off)", Priority, CooldownSeconds, Summary, CategoryOrDefault, IsCustom: true);
}
