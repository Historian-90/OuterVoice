using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace OuterVoice.Windows;

/// <summary>Custom Events tab: player-made triggers built from casts, statuses, emotes and Moodles.</summary>
public sealed partial class ConfigWindow
{
    private static readonly string[] ConditionLabels = ["Casting", "Has status", "Doing emote", "Has Moodle"];

    private static readonly string[] ConditionHints =
    [
        "Action name, e.g. Pneuma",
        "Buff/debuff name, e.g. Swiftcast",
        "Emote, e.g. wave or /wave",
        "Moodle title, e.g. Caffeinated",
    ];

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
            "same time. Write its lines on the Lines tab (the ALL group works for every job). Custom events are " +
            "saved in your voice pack, so friends who import it hear them too.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button("+ New custom event"))
        {
            pack.CustomEvents.Add(new CustomEvent { Conditions = { new CustomCondition() } });
            Changed();
        }

        CustomEvent? toDelete = null;
        foreach (var ev in pack.CustomEvents)
        {
            ImGui.PushID(ev.Id);
            var header = $"{(string.IsNullOrWhiteSpace(ev.Name) ? "(unnamed)" : ev.Name)}###hdr";
            if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();
                if (DrawCustomEvent(pack, ev))
                    toDelete = ev;
                ImGui.Unindent();
                ImGui.Spacing();
            }
            ImGui.PopID();
        }

        if (toDelete != null)
        {
            pack.CustomEvents.Remove(toDelete);
            Changed();
        }
    }

    /// <summary>Draws one custom event's editor. Returns true if the user asked to delete it.</summary>
    private bool DrawCustomEvent(VoicePack pack, CustomEvent ev)
    {
        var name = ev.Name;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Name", ref name, 64))
        {
            ev.Name = name;
            Changed();
        }

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

            var type = (int)c.Type;
            ImGui.SetNextItemWidth(120);
            if (ImGui.Combo("##type", ref type, ConditionLabels, ConditionLabels.Length))
            {
                c.Type = (ConditionType)type;
                Changed();
            }

            ImGui.SameLine();
            var value = c.Value;
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputTextWithHint("##value", ConditionHints[(int)c.Type], ref value, 128))
            {
                c.Value = value;
                Changed();
            }

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
