using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace OuterVoice.Windows;

/// <summary>The trigger editor for player-made events (shown inline on the Lines tab).</summary>
public sealed partial class ConfigWindow
{
    private static readonly string[] ConditionLabels =
        ["Starts casting", "Has status", "Doing emote", "Has Moodle", "Treasure coffer", "Weather", "Idle", "Joining a party",
         "Time of day", "Place", "State", "Built-in event", "Uses an action", "Player joins the party"];

    private static readonly string[] ScopeLabels = ["Player (You)", "Target"];

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
        InfoMarker("Casting: you're casting an action with a cast bar (instant actions can't be seen).\n" +
                   "Has status: a regular game buff or debuff on you, from anyone.\n" +
                   "Doing emote: the emote you're currently performing.\n" +
                   "Has Moodle: a custom status from the Moodles plugin (needs Moodles installed;\n" +
                   "  friends' Moodles only show up if they're synced to you).\n" +
                   "Treasure coffer / Joining a party: it just happened (within a few seconds).\n" +
                   "Weather / Time of day / Place / State: true for as long as it lasts.\n" +
                   "Idle: standing still that long, out of combat and duties.\n" +
                   "Built-in event: makes this a variant of that event (plays instead of it).\n\n" +
                   "Names must match what the game (or Moodles) shows; capitals don't matter.");

        CustomCondition? removeCondition = null;
        for (var i = 0; i < ev.Conditions.Count; i++)
        {
            var c = ev.Conditions[i];
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

            if (c.Type == ConditionType.Status)
            {
                var scope = (int)c.Scope;
                ImGui.SetNextItemWidth(110);
                if (ImGui.Combo("##scope", ref scope, ScopeLabels, ScopeLabels.Length))
                {
                    c.Scope = (ConditionScope)scope;
                    Changed();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Player (You): the status is on you, from anyone.\n" +
                                     "Target: you put it on someone else (a party member, or whoever you're targeting).\n" +
                                     "Use Target for buffs you give others, like Haima.");
                ImGui.SameLine();
            }

            var type = (int)c.Type;
            ImGui.SetNextItemWidth(120);
            if (ImGui.Combo("##type", ref type, ConditionLabels, ConditionLabels.Length) && (ConditionType)type != c.Type)
            {
                c.Type = (ConditionType)type;
                c.Value = CustomCondition.DefaultValue(c.Type);
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
            ev.Conditions.Remove(removeCondition);
            Changed();
        }

        if (ImGui.SmallButton("+ Add condition"))
        {
            ev.Conditions.Add(new CustomCondition());
            Changed();
        }

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
