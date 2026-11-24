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

    /// <summary>Eorzea time is in one of these periods. Value: "Dawn,Dusk" etc.</summary>
    TimeOfDay,

    /// <summary>You're in one of these kinds of place. Value: "City,Inn" etc.</summary>
    Place,

    /// <summary>You're doing one of these. Value: "Swimming,Diving" etc.</summary>
    State,

    /// <summary>
    /// A built-in event happens (value: its name, e.g. "wipe"). An event with this condition is a
    /// variant: when the built-in event fires and the other conditions are true, the variant's line
    /// plays instead of the built-in one.
    /// </summary>
    BuiltIn,

    /// <summary>
    /// You use an action with this name: fires when it goes off, so instant skills and abilities
    /// work too (a cast spell fires when the cast finishes).
    /// </summary>
    UseAction,

    /// <summary>
    /// A specific player joins your party. Value: their name, "First Last" or "First Last@World".
    /// </summary>
    PlayerJoins,

    /// <summary>Someone dies (HP drops to 0). Usually used with Who: a party member or a player.</summary>
    Dies,

    /// <summary>Someone gets back up after dying (raised or revived).</summary>
    GetsRaised,

    /// <summary>Someone's HP is below this percentage (value: 1-99), but not dead.</summary>
    LowHp,

    /// <summary>Someone is in combat.</summary>
    InCombat,

    /// <summary>Someone has their weapon drawn.</summary>
    WeaponDrawn,
}

/// <summary>Who a condition looks at. The line is still said by the pack's own character.</summary>
public enum ConditionWho
{
    /// <summary>The pack's own character (you, or the friend whose pack it is).</summary>
    You,

    /// <summary>Whoever that character has targeted.</summary>
    Target,

    /// <summary>Anyone else in the party (true if any one of them matches).</summary>
    Party,

    /// <summary>A specific player, by name (see <see cref="CustomCondition.WhoName"/>).</summary>
    Player,
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

    /// <summary>Who the condition looks at (for the types where that makes sense).</summary>
    public ConditionWho Who { get; set; } = ConditionWho.You;

    /// <summary>For Who = Player: "First Last" or "First Last@World".</summary>
    public string WhoName { get; set; } = string.Empty;

    /// <summary>Condition types that can look at someone other than the pack's own character.</summary>
    public static bool SupportsWho(ConditionType type) => type is ConditionType.Status or ConditionType.Moodle
        or ConditionType.OpenCoffer or ConditionType.Dies or ConditionType.GetsRaised or ConditionType.LowHp;

    /// <summary>Condition types that need no value (just "it happens").</summary>
    public static bool NoValue(ConditionType type) => type is ConditionType.JoinParty or ConditionType.Dies
        or ConditionType.GetsRaised or ConditionType.InCombat or ConditionType.WeaponDrawn;

    [Newtonsoft.Json.JsonIgnore]
    public bool IsComplete => (NoValue(Type) || !string.IsNullOrWhiteSpace(Value)) &&
                              (Who != ConditionWho.Player || !string.IsNullOrWhiteSpace(WhoName));

    /// <summary>The value a newly picked condition type starts with (empty means "type something").</summary>
    public static string DefaultValue(ConditionType type) => type switch
    {
        ConditionType.OpenCoffer => CofferAny,
        ConditionType.Weather => "Rain",
        ConditionType.Idle => "5",
        ConditionType.JoinParty or ConditionType.Dies or ConditionType.GetsRaised
            or ConditionType.InCombat or ConditionType.WeaponDrawn => "yes",
        ConditionType.LowHp => "25",
        ConditionType.TimeOfDay => "Night",
        ConditionType.Place => "City",
        ConditionType.State => "Swimming",
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

    /// <summary>
    /// "Or when..." blocks. The event plays when all conditions of any one block are true
    /// (<see cref="Conditions"/> is the first block).
    /// </summary>
    public List<List<CustomCondition>> OrBlocks { get; set; } = new();

    /// <summary>Every block: the first one, then the "or" ones.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public IEnumerable<List<CustomCondition>> Blocks => new[] { Conditions }.Concat(OrBlocks);

    [Newtonsoft.Json.JsonIgnore]
    public IEnumerable<CustomCondition> AllConditions => Blocks.SelectMany(b => b);
    public int CooldownSeconds { get; set; } = 30;
    public int Priority { get; set; } = 2;

    /// <summary>Optional grouping shown on the Lines tab ("Emotes", "RP"...).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Switched-off events are kept but never trigger.</summary>
    public bool Enabled { get; set; } = true;

    public string CategoryOrDefault => string.IsNullOrWhiteSpace(Category) ? "Other" : Category.Trim();

    public bool IsUsable => Blocks.Any(b => b.Count > 0 && b.All(c => c.IsComplete));

    /// <summary>The built-in event this one is a variant of, if it has a "Built-in event" condition.</summary>
    public string? VariantOf => Conditions.FirstOrDefault(c => c.Type == ConditionType.BuiltIn)?.Value is { Length: > 0 } v ? v : null;

    public string Summary =>
        Conditions.Count == 0
            ? "No conditions yet: click Edit trigger."
            : "When " + string.Join(", or when ",
                Blocks.Where(b => b.Count > 0).Select(b => string.Join(" + ", b.Select(Describe))));

    /// <summary>"you", "your target", "someone in the party" or the player's name.</summary>
    public static string WhoText(CustomCondition c) => c.Who switch
    {
        ConditionWho.Target => "your target",
        ConditionWho.Party => "someone in the party",
        ConditionWho.Player => c.WhoName.Length > 0 ? c.WhoName : "?",
        _ => "you",
    };

    public static string Describe(CustomCondition c)
    {
        if (c.Who != ConditionWho.You && CustomCondition.SupportsWho(c.Type))
        {
            var who = WhoText(c);
            return c.Type switch
            {
                ConditionType.Status => $"{who} has {Quote(c.Value)}",
                ConditionType.Moodle => $"{who} has Moodle {Quote(c.Value)}",
                ConditionType.OpenCoffer => $"{who} opens a coffer",
                _ => DescribeBody(c, who),
            };
        }
        return DescribeBody(c, "you");
    }

    private static string DescribeBody(CustomCondition c, string who) => c.Type switch
    {
        ConditionType.Dies => $"{who} {(who == "you" ? "die" : "dies")}",
        ConditionType.GetsRaised => $"{who} {(who == "you" ? "get" : "gets")} raised",
        ConditionType.LowHp => $"{who} {(who == "you" ? "drop" : "drops")} below {c.Value}% HP",
        ConditionType.InCombat => $"{who} {(who == "you" ? "are" : "is")} in combat",
        ConditionType.WeaponDrawn => $"{who} {(who == "you" ? "have" : "has")} a weapon drawn",
        ConditionType.Cast => $"starting to cast {Quote(c.Value)}",
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
        ConditionType.UseAction => $"using {Quote(c.Value)}",
        ConditionType.PlayerJoins => $"{(c.Value.Length > 0 ? c.Value : "?")} joins your party",
        ConditionType.BuiltIn => $"{(KnownEvents.Get(c.Value)?.Label ?? "?").ToLowerInvariant()} happens",
        ConditionType.TimeOfDay => $"it's {OrList(c.Value)}",
        ConditionType.Place => $"in {OrList(c.Value)}",
        ConditionType.State => $"{OrList(c.Value)}",
        _ => c.Value,
    };

    private static string OrList(string value)
    {
        var picks = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return picks.Length == 0 ? "?" : string.Join(" or ", picks).ToLowerInvariant();
    }

    private static string Quote(string s) => s.Length > 0 ? $"\"{s}\"" : "?";

    public KnownEvents.Info ToInfo() =>
        new(Id, Enabled ? Name : Name + " (off)", Priority, CooldownSeconds, Summary, CategoryOrDefault, IsCustom: true);
}
