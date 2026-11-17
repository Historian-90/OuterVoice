using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace OuterVoice.Windows;

/// <summary>Custom Events tab: player-made triggers built from casts, statuses, emotes and Moodles.</summary>
public sealed partial class ConfigWindow
{
    private static readonly string[] ConditionLabels =
        ["Casting", "Has status", "Doing emote", "Has Moodle", "Opens coffer", "Weather is", "Idle for", "Joins a party",
         "Time of day", "Place", "Doing"];

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
                MultiSelect(c, WorldState.PlaceLabels, "City, field, inn, housing, Gold Saucer, Island Sanctuary, or any duty.");
                break;
            case ConditionType.State:
                MultiSelect(c, WorldState.StateLabels,
                            "For friends' packs, only Mounted, Weapon drawn and In combat can be seen on your screen.");
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

    private void DrawCustomEvents()
    {
        var pack = plugin.Speaker.Pack;
        if (pack == null)
        {
            ImGui.TextColored(Muted, "Load or create a voice pack on the Voice Pack tab first.");
            return;
        }

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted,
            "Make your own triggers. A custom event plays a line the moment ALL of its conditions are true at the " +
            "same time. Custom events are saved in your voice pack, so friends who import it hear them too.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("+ New custom event"))
        {
            var created = new CustomEvent { Conditions = { new CustomCondition() } };
            pack.CustomEvents.Add(created);
            focusCustomEvent = created.Id;
            customSearch = string.Empty;
            Changed();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##customsearch", "Search name, category, conditions...", ref customSearch, 64);

        var q = customSearch.Trim();
        var shown = pack.CustomEvents
            .Where(e => e.Id == focusCustomEvent || q.Length == 0 ||
                        e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        e.CategoryOrDefault.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        e.Summary.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pack.CustomEvents.Count > 0 && shown.Count == 0)
            ImGui.TextColored(Muted, "Nothing matches your search.");

        ImGui.BeginChild("##customlist", new Vector2(0, 0));
        CustomEvent? toDelete = null;
        foreach (var group in shown.GroupBy(e => e.CategoryOrDefault).OrderBy(g => g.Key == "Other").ThenBy(g => g.Key))
        {
            if (!SubSection($"custom:cat:{group.Key}", $"{group.Key}  ({group.Count()})"))
                continue;

            foreach (var ev in group.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.PushID(ev.Id);
                if (DrawCustomEventRow(pack, ev))
                    toDelete = ev;
                ImGui.PopID();
            }

            ImGui.TreePop();
        }
        ImGui.EndChild();

        if (toDelete != null)
        {
            pack.CustomEvents.Remove(toDelete);
            Changed();
        }

        focusCustomEvent = null;
    }

    /// <summary>One custom event as a compact collapsible row. Returns true if it should be deleted.</summary>
    private bool DrawCustomEventRow(VoicePack pack, CustomEvent ev)
    {
        var lineCount = pack.Jobs.Values.SelectMany(l => l).Count(l => l.Event == ev.Id);
        var name = string.IsNullOrWhiteSpace(ev.Name) ? "(unnamed)" : ev.Name;
        var state = !ev.Enabled ? "  [off]" : !ev.IsUsable ? "  [incomplete]" : "";
        var header = $"{name}{state}  ·  {ev.Summary}  ·  {lineCount} line{(lineCount == 1 ? "" : "s")}###row";

        if (ev.Id == focusCustomEvent)
        {
            ImGui.SetNextItemOpen(true);
            ImGui.SetScrollHereY();
        }

        if (!ev.Enabled) ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        var open = ImGui.CollapsingHeader(header);
        if (!ev.Enabled) ImGui.PopStyleColor();
        if (!open)
            return false;

        ImGui.Indent();
        var delete = DrawCustomEvent(pack, ev);
        ImGui.Unindent();
        ImGui.Spacing();
        return delete;
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

        ImGui.SameLine();
        if (ImGui.SmallButton("Edit lines ->"))
        {
            // Custom lines usually live in ALL; fall back to whatever job is open.
            if (pack.Jobs.ContainsKey(VoicePack.SharedJob)) editJob = VoicePack.SharedJob;
            pendingTab = "Lines";
            focusEvent = ev.Id;
            lineSearch = string.Empty;
        }

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
                   "  friends' Moodles only show up if they're synced to you).\n\n" +
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

        // Status line: what's missing, and how many lines it has.
        var lines = pack.Jobs.Values.SelectMany(l => l).Count(l => l.Event == ev.Id);
        if (!ev.IsUsable)
            ImGui.TextColored(Warn, "Fill in every condition for this event to work.");
        else if (lines == 0)
            ImGui.TextColored(Warn, "No lines yet: add some on the Lines tab.");
        else
            ImGui.TextColored(Good, $"{lines} line{(lines == 1 ? "" : "s")} on the Lines tab.");

        var ctrl = ImGui.GetIO().KeyCtrl;
        if (!ctrl) ImGui.BeginDisabled();
        var delete = ImGui.SmallButton("Delete event");
        if (!ctrl) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl to delete this event. Its lines stay in the pack but won't play.");

        return delete;
    }
}
