using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;

namespace OuterVoice.Windows;

/// <summary>
/// The Lines page: a flat list of events on the left (headings, not nested dropdowns), and the
/// selected event on the right. Anything you create or pick opens on the right straight away.
/// </summary>
public sealed partial class ConfigWindow
{
    private const string UnusedKey = "__unused";
    private const float ListWidth = 270f;

    /// <summary>The event shown on the right (or <see cref="UnusedKey"/>).</summary>
    private string? selectedEvent;
    private bool scrollToSelected;

    /// <summary>Open an event on the right and scroll the list to it.</summary>
    private void SelectEvent(string name)
    {
        selectedEvent = name;
        scrollToSelected = true;
        pendingTab = "Lines";
    }

    private void DrawLines()
    {
        var pack = plugin.Speaker.Pack;
        if (pack == null)
        {
            ImGui.TextColored(Muted, "Load or create a voice pack on the Voice Pack page first.");
            if (ImGui.Button("Go to Voice Pack")) page = Page.Pack;
            return;
        }

        var jobs = OrderedJobs(pack).Select(j => j.Key).ToList();
        if (!jobs.Contains(editJob))
            editJob = EventWatcher.CurrentJob() is { } cur && jobs.Contains(cur) ? cur : jobs.FirstOrDefault() ?? string.Empty;

        ImGui.BeginChild("##eventlist", new Vector2(ListWidth, 0), true);
        DrawEventList(pack, jobs);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##eventdetail", Vector2.Zero, true);
        DrawEventDetail(pack);
        ImGui.EndChild();

        if (deleteCustom != null)
        {
            if (selectedEvent == deleteCustom.Id) selectedEvent = null;
            pack.CustomEvents.Remove(deleteCustom);
            editingCustom.Remove(deleteCustom.Id);
            deleteCustom = null;
            Changed();
        }
    }

    // ---- Left: job, search, the list, and the add buttons ------------------------------------

    private void DrawEventList(VoicePack pack, List<string> jobs)
    {
        DrawJobPicker(pack, jobs);
        if (editJob.Length == 0 || !pack.Jobs.TryGetValue(editJob, out var jobLines))
        {
            ImGui.TextColored(Muted, "Add a job to start writing lines.");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##linesearch", "Search events and lines...", ref lineSearch, 64);

        // Leave room at the bottom for the buttons.
        var footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("##rows", new Vector2(0, -footer));
        DrawEventRows(pack, jobLines);
        ImGui.EndChild();

        ImGui.Separator();
        var half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        if (ImGui.Button("+ Add event", new Vector2(half, 0)))
        {
            pickerSearch = string.Empty;
            ImGui.OpenPopup("##eventpicker");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pick any event (wipe, Kugane, rain...) and start writing lines for it.");
        DrawEventPicker(pack, jobLines);

        ImGui.SameLine();
        if (ImGui.Button("+ New", new Vector2(half, 0)))
            CreateCustomEvent(pack, null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Make your own event from conditions: an emote, a buff, a Moodle, a place, the time...");

        var showAll = !plugin.Configuration.LinesHideEmpty;
        if (ImGui.Checkbox("Show all events", ref showAll))
        {
            plugin.Configuration.LinesHideEmpty = !showAll;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Off: only events you've written lines for, and your own events.");
    }

    /// <summary>Job dropdown, plus a small menu to add or remove jobs.</summary>
    private void DrawJobPicker(VoicePack pack, List<string> jobs)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Job");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.BeginCombo("##editjob", editJob.Length > 0 ? JobLabel(editJob) : "(no jobs)"))
        {
            foreach (var j in jobs)
                if (ImGui.Selectable(JobLabel(j), j == editJob))
                    editJob = j;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##jobmenu", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("##jobs");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add or remove jobs");

        if (!ImGui.BeginPopup("##jobs")) return;

        var available = KnownEvents.Jobs.Where(j => !pack.Jobs.ContainsKey(j)).ToArray();
        if (available.Length > 0)
        {
            if (!available.Contains(addJobChoice))
                addJobChoice = EventWatcher.CurrentJob() is { } cur && available.Contains(cur) ? cur : available[0];
            ImGui.SetNextItemWidth(90);
            if (ImGui.BeginCombo("##addjob", addJobChoice))
            {
                foreach (var j in available)
                    if (ImGui.Selectable(j, j == addJobChoice))
                        addJobChoice = j;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("Add job"))
            {
                pack.Jobs[addJobChoice] = new List<Line>();
                editJob = addJobChoice;
                Changed();
                ImGui.CloseCurrentPopup();
            }
        }

        if (editJob.Length > 0)
        {
            var ctrl = ImGui.GetIO().KeyCtrl;
            if (!ctrl) ImGui.BeginDisabled();
            if (ImGui.Button($"Remove {JobLabel(editJob)}"))
            {
                pack.Jobs.Remove(editJob);
                editJob = string.Empty;
                selectedEvent = null;
                Changed();
                ImGui.CloseCurrentPopup();
            }
            if (!ctrl) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl to remove this job and all its lines. (Audio files are kept.)");
        }
        ImGui.EndPopup();
    }

    /// <summary>A row in the list: a heading, or an event.</summary>
    private sealed record ListRow(string? Heading, KnownEvents.Info? Event, string Label);

    /// <summary>Everything the list shows for this job, in order, with headings.</summary>
    private List<ListRow> BuildRows(VoicePack pack, List<Line> jobLines)
    {
        var q = lineSearch.Trim();
        var showAll = !plugin.Configuration.LinesHideEmpty;
        var events = pack.AllEvents()
                         .Where(e => e.AppliesTo(editJob) || jobLines.Any(l => l.Event == e.Name))
                         .ToList();

        bool Shown(KnownEvents.Info ev)
        {
            if (ev.Name == selectedEvent) return true;
            if (q.Length > 0)
                return ev.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       ev.Trigger.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       jobLines.Any(l => l.Event == ev.Name && l.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
            return ev.IsCustom || showAll || jobLines.Any(l => l.Event == ev.Name);
        }

        var rows = new List<ListRow>();
        void AddGroup(string heading, IEnumerable<KnownEvents.Info> group)
        {
            var shown = group.Where(Shown).ToList();
            if (shown.Count == 0) return;
            rows.Add(new ListRow(heading, null, heading));
            foreach (var ev in shown) rows.Add(new ListRow(null, ev, RowLabel(ev)));
        }

        var common = events.Where(e => !e.IsCustom && e.Jobs == null).ToList();
        foreach (var category in KnownEvents.CommonCategories)
            AddGroup(category.ToUpperInvariant(), common.Where(e => (e.Category ?? KnownEvents.CommonCategories[0]) == category));

        AddGroup("JOB ABILITIES", events.Where(e => !e.IsCustom && e.Jobs != null));

        foreach (var group in events.Where(e => e.IsCustom)
                                    .GroupBy(e => e.Category ?? "Other")
                                    .OrderBy(g => g.Key == "Other").ThenBy(g => g.Key))
            AddGroup($"YOUR EVENTS: {group.Key.ToUpperInvariant()}", group.OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase));

        var unused = jobLines.Count(pack.IsUnused);
        if (unused > 0)
        {
            rows.Add(new ListRow("NEEDS ATTENTION", null, "NEEDS ATTENTION"));
            rows.Add(new ListRow(null, new KnownEvents.Info(UnusedKey, "Unused lines", 0, 0, ""), $"Unused lines"));
        }
        return rows;
    }

    /// <summary>Shorter names in the list: "City: Kugane" is just "Kugane" under PLACES.</summary>
    private static string RowLabel(KnownEvents.Info ev) =>
        ev.Name == "zone_city" ? "Any city"
        : ev.Label.StartsWith("City: ", StringComparison.Ordinal) ? ev.Label[6..]
        : ev.Label;

    private void DrawEventRows(VoicePack pack, List<Line> jobLines)
    {
        var rows = BuildRows(pack, jobLines);
        if (rows.Count == 0)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Muted, lineSearch.Trim().Length > 0
                ? "Nothing matches your search."
                : "No lines yet. Click \"+ Add event\" below to pick an event, or \"+ New\" to make your own.");
            ImGui.PopTextWrapPos();
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        foreach (var row in rows)
        {
            if (row.Heading != null)
            {
                ImGui.Spacing();
                ImGui.TextColored(Accent, row.Heading);
                continue;
            }

            var ev = row.Event!;
            var isUnused = ev.Name == UnusedKey;
            var evLines = isUnused ? jobLines.Where(pack.IsUnused).ToList() : jobLines.Where(l => l.Event == ev.Name).ToList();
            var recorded = evLines.Count(l => l.AudioPath != null);

            var label = row.Label;
            if (ev.IsCustom && pack.CustomEvents.FirstOrDefault(c => c.Id == ev.Name) is { } ce)
            {
                if (ce.VariantOf != null) label += " (variant)";
                if (!ce.Enabled) label += " (off)";
            }

            var selected = selectedEvent == ev.Name;
            if (ImGui.Selectable($"  {label}##row_{ev.Name}", selected))
                selectedEvent = ev.Name;
            if (selected && scrollToSelected)
            {
                ImGui.SetScrollHereY(0.4f);
                scrollToSelected = false;
            }
            if (ImGui.IsItemHovered() && ev.Trigger.Length > 0) ImGui.SetTooltip(ev.Trigger);

            // Right side of the row: line count and a dot (filled = all recorded, hollow = some aren't).
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var midY = (min.Y + max.Y) / 2;
            if (evLines.Count > 0)
            {
                var count = evLines.Count.ToString();
                var w = ImGui.CalcTextSize(count).X;
                drawList.AddText(new Vector2(max.X - w - 22, min.Y), ImGui.GetColorU32(Muted), count);
                var dotColor = ImGui.GetColorU32(recorded == evLines.Count ? Good : Warn);
                var center = new Vector2(max.X - 9, midY);
                if (recorded == evLines.Count) drawList.AddCircleFilled(center, 4f, dotColor);
                else drawList.AddCircle(center, 4f, dotColor, 0, 1.5f);
            }
        }
    }

    // ---- Right: the selected event -----------------------------------------------------------

    private void DrawEventDetail(VoicePack pack)
    {
        if (editJob.Length == 0 || !pack.Jobs.TryGetValue(editJob, out var jobLines))
            return;

        if (selectedEvent == UnusedKey)
        {
            DrawUnusedLines(pack, jobLines);
            return;
        }

        var ev = selectedEvent == null ? null : pack.AllEvents().FirstOrDefault(e => e.Name == selectedEvent);
        if (ev == null)
        {
            selectedEvent = null;
            DrawLinesWelcome();
            return;
        }

        // Title and play button.
        ImGui.SetWindowFontScale(1.3f);
        ImGui.TextUnformatted(ev.IsCustom ? ev.Label : ev.Label);
        ImGui.SetWindowFontScale(1f);
        var evLines = jobLines.Where(l => l.Event == ev.Name).ToList();
        if (evLines.Count > 0)
        {
            ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 70);
            if (ImGui.Button("Play##testev", new Vector2(70, 0)))
                plugin.Speaker.Test(ev.Name, editJob);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play one of these lines now (even out of combat).");
        }
        ImGui.TextColored(Muted, EventPath(ev));
        ImGui.Spacing();

        if (ev.IsCustom && pack.CustomEvents.FirstOrDefault(c => c.Id == ev.Name) is { } custom)
        {
            if (custom.VariantOf is { } baseName && KnownEvents.Get(baseName) is { } baseEvent)
            {
                ImGui.TextColored(Muted, "Variant of:");
                ImGui.SameLine();
                if (ImGui.SmallButton($"{baseEvent.Label}##base"))
                    SelectEvent(baseEvent.Name);
                ImGui.SameLine();
                ImGui.TextColored(Muted, "Plays instead of it when the conditions below are true.");
            }
            DrawCustomTrigger(pack, custom);
        }
        else
        {
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(ev.Trigger);
            if (CategoryNote(ev.Category) is { } note)
                ImGui.TextColored(Muted, note);
            ImGui.PopTextWrapPos();
            var triggerInfo = JobTriggers.Describe(ev.Name, editJob);
            if (triggerInfo.Length > 0)
            {
                ImGui.SameLine();
                InfoMarker(triggerInfo);
            }

            if (ImGui.SmallButton("Make a variant"))
                CreateCustomEvent(pack, ev);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Your own version of this event with extra conditions (a Moodle, a place, the time...).\n" +
                                 "When they're true, the variant's lines play instead of these.");
            var variants = pack.CustomEvents.Where(c => c.VariantOf == ev.Name).ToList();
            if (variants.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Muted, "Variants:");
                foreach (var v in variants)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"{v.Name}##v_{v.Id}"))
                        SelectEvent(v.Id);
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (evLines.Count == 0)
            ImGui.TextColored(Muted, $"No lines for {JobLabel(editJob)} yet.");

        Line? toDelete = null;
        foreach (var line in evLines)
            if (DrawLine(pack, line, ev))
                toDelete = line;
        if (toDelete != null)
        {
            jobLines.Remove(toDelete);
            Changed();
        }

        ImGui.Spacing();
        if (ImGui.Button("+ Add line"))
            AddLine(pack, jobLines, ev.Name);
    }

    private void DrawLinesWelcome()
    {
        ImGui.PushTextWrapPos();
        ImGui.TextUnformatted("Pick an event on the left to see and edit its lines.");
        ImGui.Spacing();
        ImGui.TextColored(Muted, "+ Add event: start lines for any built-in event (a wipe, arriving in Kugane, rain, a coffer...).");
        ImGui.TextColored(Muted, "+ New: make your own event from conditions (an emote, a buff, a Moodle, a place, the time...).");
        ImGui.TextColored(Muted, "Make a variant (on any built-in event): your own version of it that plays when extra conditions are true.");
        ImGui.Spacing();
        ImGui.TextColored(Muted, "Lines on the ALL job play on every job that has no lines of its own for that event.");
        ImGui.PopTextWrapPos();
    }

    /// <summary>"General events > Places > Cities", "Job abilities", "Your events > Emotes".</summary>
    private static string EventPath(KnownEvents.Info ev)
    {
        if (ev.IsCustom) return $"Your events > {ev.Category ?? "Other"}";
        if (ev.Jobs != null) return "Job abilities";
        var path = $"General events > {ev.Category ?? KnownEvents.CommonCategories[0]}";
        return ev.Group != null ? $"{path} > {ev.Group}" : path;
    }

    /// <summary>The extra explanation a category of built-in events needs.</summary>
    private string? CategoryNote(string? category) => category switch
    {
        "Weather" => "Plays when the weather changes while you're out in the world, or when you arrive somewhere with " +
                     "inclement weather you weren't already in. Not in duties or combat. One voice in the party comments.",
        "Idle" => $"Plays now and then after {plugin.Configuration.IdleMinutes}+ minutes standing still, out of combat and " +
                  $"duties (how often: Settings, currently {plugin.Configuration.IdleFrequency}).",
        "Places" => "Plays when you (or a friend, arriving where you are) arrive in a different kind of place. A city " +
                    "without its own lines plays Any city; Home plays House/Apartment if it has none. Homes: Settings.",
        "State" => "Plays when you (or a friend near you) start doing something, outside duties and combat. Friends' " +
                   "diving, flying and fishing can't be seen, so those only play for you.",
        "Time" => "Eorzea time: Dawn 5:00, Morning 7:00, Day 12:00, Dusk 17:00, Evening 19:00, Night 22:00. One voice " +
                  "in the party comments; not in duties or combat. 30-minute cooldown.",
        _ => null,
    };
}
