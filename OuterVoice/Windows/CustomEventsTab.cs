using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace OuterVoice.Windows;

/// <summary>The trigger editor for player-made events (shown inline on the Lines tab).</summary>
public sealed partial class ConfigWindow
{
    private static readonly string[] ConditionLabels =
        ["Starts casting", "Has status", "Doing emote", "Has Moodle", "Treasure coffer", "Weather", "Idle", "Joining a party",
         "Time of day", "Place", "State", "Built-in event", "Uses an action", "Player joins the party",
         "Dies", "Gets raised", "HP below", "In combat", "Weapon drawn"];

    // Who a condition looks at. "Has status" also has "Given by you" (a status you put on someone).
    private static readonly string[] WhoLabels = ["You", "Your target", "Anyone in party", "A player..."];
    private static readonly string[] StatusWhoLabels = ["You", "Given by you", "Your target", "Anyone in party", "A player..."];


    private static readonly string[] CofferValues = [CustomCondition.CofferAny, CustomCondition.CofferRegular, CustomCondition.CofferMap];
    private static readonly string[] CofferLabels = ["Any coffer", "Regular coffer", "Treasure map coffer"];

    private static readonly string[] ConditionHints =
    [
        "Action name, e.g. Pneuma",
        "Buff/debuff name, e.g. Swiftcast",
        "Emote, e.g. wave or /wave",
        "Moodle title, e.g. Caffeinated",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "Action name, e.g. Swiftcast or Kerachole",
        "Player name, e.g. Aereon Tucker",
        "",
        "",
        "",
        "",
        "",
    ];

    private string customSearch = string.Empty;

    /// <summary>A dropdown of checkboxes; the condition is true when any picked option is.</summary>
    private void MultiSelect(CustomCondition c, string[] options, string tooltip)
    {
        var picked = WorldState.Picks(c.Value);
        var preview = picked.Count switch
        {
            0 => "Pick...",
            1 => options.FirstOrDefault(picked.Contains) ?? c.Value,
            _ => $"{picked.Count} selected",
        };

        var changed = false;
        if (ImGui.BeginCombo("##value", preview))
        {
            foreach (var option in options)
            {
                var on = picked.Contains(option);
                if (ImGui.Checkbox(option, ref on))
                {
                    if (on) picked.Add(option);
                    else picked.Remove(option);
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(picked.Count > 1 ? $"{string.Join(", ", options.Where(picked.Contains))}\n{tooltip}" : tooltip);

        if (changed)
        {
            c.Value = string.Join(",", options.Where(picked.Contains));
            Changed();
        }
    }

    /// <summary>The value editor for a condition: free text, or a picker for the newer kinds.</summary>
    private void DrawConditionValue(CustomCondition c)
    {
        ImGui.SetNextItemWidth(220);
        switch (c.Type)
        {
            case ConditionType.OpenCoffer:
            {
                var i = Math.Max(0, Array.IndexOf(CofferValues, c.Value));
                if (ImGui.Combo("##value", ref i, CofferLabels, CofferLabels.Length))
                {
                    c.Value = CofferValues[i];
                    Changed();
                }
                break;
            }
            case ConditionType.Weather:
            {
                var labels = CustomCondition.WeatherLabels;
                var i = Math.Max(0, Array.FindIndex(labels, l => string.Equals(l, c.Value, StringComparison.OrdinalIgnoreCase)));
                if (ImGui.Combo("##value", ref i, labels, labels.Length) || c.Value.Length == 0)
                {
                    c.Value = labels[i];
                    Changed();
                }
                break;
            }
            case ConditionType.Idle:
            {
                var m = CustomCondition.IdleMinutes(c.Value);
                if (ImGui.SliderInt("##value", ref m, 1, 60, "%d+ min standing still"))
                {
                    c.Value = m.ToString();
                    Changed();
                }
                break;
            }
            case ConditionType.TimeOfDay:
                MultiSelect(c, WorldState.PeriodLabels, "Eorzea time. Dawn 5-7, Morning 7-12, Day 12-17, Dusk 17-19, Evening 19-22, Night 22-5.");
                break;
            case ConditionType.Place:
                MultiSelect(c, WorldState.PlaceLabels(plugin.Configuration.RegisteredPlaces.Select(r => r.Name)),
                            "Home = your own house/apartment (or one registered as home under Settings).\n" +
                            "Registered places are added under Settings > Homes.");
                break;
            case ConditionType.State:
                MultiSelect(c, WorldState.StateLabels,
                            "For friends' packs, Diving, Flying and Fishing can't be seen on your screen.");
                break;
            case ConditionType.BuiltIn:
            {
                var current = KnownEvents.Get(c.Value);
                if (ImGui.BeginCombo("##value", current == null ? "Pick an event..." : current.Label))
                {
                    foreach (var group in KnownEvents.All.GroupBy(e => e.Jobs != null ? "Job abilities" : e.Category ?? "Combat & duties"))
                    {
                        ImGui.TextColored(Muted, group.Key);
                        foreach (var e in group)
                        {
                            if (ImGui.Selectable($"  {e.Label}##{e.Name}", e.Name == c.Value))
                            {
                                c.Value = e.Name;
                                Changed();
                            }
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip(e.Trigger);
                        }
                    }
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Makes this a variant: when this built-in event fires and the other conditions are true,\n" +
                                     "this event's line plays instead of the built-in one.");
                break;
            }
            case ConditionType.UseAction:
            {
                var value = c.Value;
                if (ImGui.InputTextWithHint("##value", ConditionHints[(int)c.Type], ref value, 128))
                {
                    c.Value = value;
                    Changed();
                }
                InfoMarker("Fires the moment the action goes off: instant skills, oGCD abilities and finished casts.\n" +
                           "For a spell's cast bar starting, use \"Starts casting\" instead.\n\n" +
                           "Works for you and for friends whose packs use it.");
                break;
            }
            case ConditionType.PlayerJoins:
            {
                var value = c.Value;
                ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 90));
                if (ImGui.InputTextWithHint("##value", ConditionHints[(int)c.Type], ref value, 128))
                {
                    c.Value = value;
                    Changed();
                }
                ImGui.SameLine();
                if (ImGui.Button("Pick...##pickplayer"))
                    ImGui.OpenPopup("##pickplayer");
                if (ImGui.BeginPopup("##pickplayer"))
                {
                    var names = plugin.Configuration.Subscriptions.Select(s => s.Character)
                        .Concat(Plugin.PartyList.Select(m => $"{m.Name.TextValue}@{m.World.ValueNullable?.Name.ExtractText()}"))
                        .Where(n => !string.IsNullOrWhiteSpace(n) && !n.EndsWith('@'))
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
                    if (names.Count == 0) ImGui.TextColored(Muted, "No friends or party members to pick from.");
                    foreach (var n in names)
                        if (ImGui.Selectable(n))
                        {
                            c.Value = n;
                            Changed();
                        }
                    ImGui.EndPopup();
                }
                InfoMarker("Plays when this player joins the party you're in (or you invite them).\n" +
                           "The world part (@Siren) is optional.");
                break;
            }
            case ConditionType.LowHp:
            {
                var pct = int.TryParse(c.Value, out var p) ? Math.Clamp(p, 1, 99) : 25;
                ImGui.SetNextItemWidth(180);
                if (ImGui.SliderInt("##value", ref pct, 1, 99, "below %d%% HP"))
                {
                    c.Value = pct.ToString();
                    Changed();
                }
                break;
            }
            case ConditionType.Dies:
            case ConditionType.GetsRaised:
            case ConditionType.InCombat:
            case ConditionType.WeaponDrawn:
                if (c.Value.Length == 0)
                {
                    c.Value = "yes";
                    Changed();
                }
                ImGui.TextColored(Muted, c.Type switch
                {
                    ConditionType.Dies => "(the moment they fall)",
                    ConditionType.GetsRaised => "(the moment they're back up)",
                    _ => "(for as long as it lasts)",
                });
                break;
            case ConditionType.JoinParty:
                if (c.Value.Length == 0)
                {
                    c.Value = "yes";
                    Changed();
                }
                ImGui.TextColored(Muted, "(you, or the friend whose pack it is)");
                break;
            default:
            {
                var value = c.Value;
                if (ImGui.InputTextWithHint("##value", ConditionHints[(int)c.Type], ref value, 128))
                {
                    c.Value = value;
                    Changed();
                }
                break;
            }
        }
    }
    private readonly System.Collections.Generic.Dictionary<string, string> categoryDrafts = new();

    /// <summary>One set of conditions ("all of these are true"), with Remove and + Add condition.</summary>
    private void DrawConditionBlock(List<CustomCondition> block, string id)
    {
        ImGui.PushID(id);
        CustomCondition? removeCondition = null;
        for (var i = 0; i < block.Count; i++)
        {
            var c = block[i];
            ImGui.PushID(i);

            if (i > 0)
            {
                ImGui.TextColored(Muted, "and");
                ImGui.SameLine();
            }
            else
            {
                ImGui.Dummy(new Vector2(ImGui.CalcTextSize("and").X, 0));
                ImGui.SameLine();
            }

            if (CustomCondition.SupportsWho(c.Type))
            {
                DrawWho(c);
                ImGui.SameLine();
            }

            var type = (int)c.Type;
            ImGui.SetNextItemWidth(130);
            if (ImGui.Combo("##type", ref type, ConditionLabels, ConditionLabels.Length) && (ConditionType)type != c.Type)
            {
                c.Type = (ConditionType)type;
                c.Value = CustomCondition.DefaultValue(c.Type);
                if (!CustomCondition.SupportsWho(c.Type)) c.Who = ConditionWho.You;
                if (c.Type != ConditionType.Status) c.Scope = ConditionScope.You;
                Changed();
            }

            ImGui.SameLine();
            DrawConditionValue(c);

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
                removeCondition = c;

            ImGui.PopID();
        }

        if (removeCondition != null)
        {
            block.Remove(removeCondition);
            Changed();
        }

        if (ImGui.SmallButton("+ Add condition"))
        {
            block.Add(new CustomCondition());
            Changed();
        }
        ImGui.PopID();
    }

    /// <summary>The Who box (and the player name for "A player...").</summary>
    private void DrawWho(CustomCondition c)
    {
        var isStatus = c.Type == ConditionType.Status;
        var labels = isStatus ? StatusWhoLabels : WhoLabels;
        // Map (Who, Scope) to the list index: status lists "Given by you" second.
        var index = c.Who switch
        {
            ConditionWho.You => isStatus && c.Scope == ConditionScope.Target ? 1 : 0,
            ConditionWho.Target => isStatus ? 2 : 1,
            ConditionWho.Party => isStatus ? 3 : 2,
            _ => isStatus ? 4 : 3,
        };
        ImGui.SetNextItemWidth(125);
        if (ImGui.Combo("##who", ref index, labels, labels.Length))
        {
            var i = isStatus && index >= 1 ? index - 1 : index; // skip "Given by you"
            c.Scope = isStatus && index == 1 ? ConditionScope.Target : ConditionScope.You;
            c.Who = isStatus && index == 1 ? ConditionWho.You : i switch
            {
                1 => ConditionWho.Target,
                2 => ConditionWho.Party,
                3 => ConditionWho.Player,
                _ => ConditionWho.You,
            };
            Changed();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Who this condition looks at. The line is always said by this pack's character.\n" +
                             "Anyone in party: true if any one party member matches.");

        if (c.Who != ConditionWho.Player) return;
        ImGui.SameLine();
        var name = c.WhoName;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputTextWithHint("##whoname", "First Last", ref name, 64))
        {
            c.WhoName = name;
            Changed();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Pick##whopick"))
            ImGui.OpenPopup("##whopicker");
        if (ImGui.BeginPopup("##whopicker"))
        {
            var names = plugin.Configuration.Subscriptions.Select(s => s.Character)
                .Concat(Plugin.PartyList.Select(m => $"{m.Name.TextValue}@{m.World.ValueNullable?.Name.ExtractText()}"))
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.EndsWith('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            if (names.Count == 0) ImGui.TextColored(Muted, "No friends or party members to pick from.");
            foreach (var n in names)
                if (ImGui.Selectable(n))
                {
                    c.WhoName = n;
                    Changed();
                }
            ImGui.EndPopup();
        }
    }

    /// <summary>Draws one custom event's editor. Returns true if the user asked to delete it.</summary>
    private bool DrawCustomEvent(VoicePack pack, CustomEvent ev)
    {
        var enabled = ev.Enabled;
        if (ImGui.Checkbox("On", ref enabled))
        {
            ev.Enabled = enabled;
            Changed();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Switch off to park this event without deleting it.");


        var name = ev.Name;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Name", ref name, 64))
        {
            ev.Name = name;
            Changed();
        }

        // Typing edits a draft; the category is applied when you leave the box (or press Enter).
        // Applying it per keystroke would move the event between groups mid-typing and drop focus.
        var category = categoryDrafts.TryGetValue(ev.Id, out var draft) ? draft : ev.Category;
        ImGui.SetNextItemWidth(190);
        if (ImGui.InputTextWithHint("##category", "Other", ref category, 32))
            categoryDrafts[ev.Id] = category;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ev.Category = category.Trim();
            categoryDrafts.Remove(ev.Id);
            Changed();
        }
        ImGui.SameLine(0, 2);
        var existing = pack.CustomEvents.Select(c => c.Category.Trim()).Where(c => c.Length > 0)
                           .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
        ImGui.SetNextItemWidth(26);
        if (ImGui.BeginCombo("##catpick", string.Empty, ImGuiComboFlags.NoPreview))
        {
            if (existing.Count == 0)
                ImGui.TextColored(Muted, "No categories yet: type one.");
            foreach (var c in existing)
            {
                if (ImGui.Selectable(c, string.Equals(c, ev.Category, StringComparison.OrdinalIgnoreCase)))
                {
                    ev.Category = c;
                    Changed();
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Category");
        HelpMarker("Groups events on this tab and the Lines tab, e.g. Emotes, RP, Moodles. Blank = Other.\nApplied when you click away or press Enter.");

        ImGui.TextUnformatted("When all of these are true:");
        InfoMarker("Each condition can look at you, your target, anyone in the party, or a specific player\n" +
                   "(Who box). The line is always said by this pack's character.\n\n" +
                   "Starts casting: the cast bar appears. Uses an action: the moment it goes off (instant ones too).\n" +
                   "Has status: a game buff or debuff (Given by you = one you put on someone, like Haima).\n" +
                   "Dies / Gets raised / Treasure coffer / Joining a party: it just happened (within a few seconds).\n" +
                   "HP below, In combat, Weapon drawn, Weather, Time of day, Place, State: true while it lasts.\n" +
                   "Has Moodle: a custom status from the Moodles plugin.\n" +
                   "Built-in event: makes this a variant of that event (plays instead of it).\n\n" +
                   "+ Or when... adds another set: the event plays when ANY one set is completely true.\n" +
                   "Names must match what the game shows; capitals don't matter.");

        DrawConditionBlock(ev.Conditions, "b0");

        List<CustomCondition>? removeBlock = null;
        for (var b = 0; b < ev.OrBlocks.Count; b++)
        {
            ImGui.Spacing();
            ImGui.TextColored(Accent, "OR");
            ImGui.SameLine();
            ImGui.TextUnformatted("when all of these are true:");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove this set##rb{b}"))
                removeBlock = ev.OrBlocks[b];
            DrawConditionBlock(ev.OrBlocks[b], $"b{b + 1}");
        }
        if (removeBlock != null)
        {
            ev.OrBlocks.Remove(removeBlock);
            Changed();
        }

        if (ImGui.SmallButton("+ Or when..."))
        {
            ev.OrBlocks.Add(new List<CustomCondition> { new() });
            Changed();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds another set of conditions. The event plays when any one set is completely true.");
        ImGui.Spacing();

        var cd = ev.CooldownSeconds;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Cooldown", ref cd, 0, 600, "%d sec"))
        {
            ev.CooldownSeconds = cd;
            Changed();
        }

        var urgent = ev.Priority >= 4;
        if (ImGui.Checkbox("Urgent (can cut off other lines)", ref urgent))
        {
            ev.Priority = urgent ? 4 : 2;
            Changed();
        }

        if (!ev.IsUsable)
            ImGui.TextColored(Warn, "Fill in every condition for this event to work.");
        if (ev.VariantOf != null)
            ImGui.TextColored(Muted, "Variant: plays instead of the built-in line when everything else here is true.");

        var ctrl = ImGui.GetIO().KeyCtrl;
        if (!ctrl) ImGui.BeginDisabled();
        var delete = ImGui.SmallButton("Delete event");
        if (!ctrl) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl to delete this event. Its lines stay in the pack but won't play.");

        return delete;
    }
}
