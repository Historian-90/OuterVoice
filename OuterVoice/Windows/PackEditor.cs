using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;

namespace OuterVoice.Windows;

/// <summary>Voice Pack and Lines tabs: edit the pack in-game; Save writes voicepack.json.</summary>
public sealed partial class ConfigWindow
{
    private static readonly Vector4 Warn = new(1f, 0.8f, 0.35f, 1f);

    private string editJob = string.Empty;
    private string addJobChoice = string.Empty;

    // ---- Save bar (shown under the header when there are unsaved edits) --------

    private void DrawSaveBar()
    {
        if (!plugin.PackDirty)
            return;

        ImGui.TextColored(Warn, "Unsaved changes");
        ImGui.SameLine();
        if (ImGui.SmallButton("Save"))
            plugin.SavePack();
        ImGui.SameLine();
        if (ImGui.SmallButton("Discard"))
        {
            plugin.Player.Stop();
            plugin.ReloadPack();
        }
    }

    private void Changed() => plugin.PackDirty = true;

    // ---- Voice Pack tab ----------------------------------------------------------

    private void DrawPack()
    {
        var cfg = plugin.Configuration;

        Section("Folder");
        ImGui.SetNextItemWidth(-150);
        ImGui.InputText("##folder", ref folderInput, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            plugin.FileDialogs.OpenFolderDialog("Choose a voice pack folder", (ok, path) =>
            {
                if (!ok) return;
                folderInput = path;
                LoadFolder(path);
            }, Directory.Exists(cfg.PackFolder) ? cfg.PackFolder : null, false);
        }
        ImGui.SameLine();
        if (ImGui.Button("Load"))
            LoadFolder(folderInput);
        HelpMarker("The folder holding the voice pack and its audio clips. Share the whole folder to share the pack.");

        if (plugin.PackMissing)
        {
            ImGui.Spacing();
            ImGui.TextColored(Warn, plugin.PackError ?? string.Empty);
            if (ImGui.Button("Create a new voice pack here"))
                plugin.CreatePack();
            return;
        }

        if (plugin.PackError != null)
        {
            ImGui.TextColored(Bad, plugin.PackError);
            return;
        }

        var pack = plugin.Speaker.Pack;
        if (pack == null)
            return;

        Section("Pack");
        var name = pack.Name;
        if (ImGui.InputText("Pack name", ref name, 64))
        {
            pack.Name = name;
            Changed();
        }

        var character = pack.Character;
        ImGui.SetNextItemWidth(ImGui.CalcItemWidth() - 150);
        if (ImGui.InputTextWithHint("##character", "First Last@World", ref character, 64))
        {
            pack.Character = character;
            Changed();
        }
        ImGui.SameLine();
        if (ImGui.Button("Use my character") && Plugin.CurrentCharacterId() is { } me)
        {
            pack.Character = me;
            Changed();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Character");
        HelpMarker("Which character this pack voices. Later, friends' plugins use this to know whose voice to play.");

        var gap = pack.GlobalCooldownSeconds;
        if (ImGui.SliderInt("Default gap", ref gap, 0, 120, "%d sec"))
        {
            pack.GlobalCooldownSeconds = gap;
            Changed();
        }
        HelpMarker("The pack's suggested time between lines. Your own setting on the Settings tab overrides it.");

        Section("Jobs");
        if (ImGui.BeginTable("jobs", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Job");
            ImGui.TableSetupColumn("Lines");
            ImGui.TableSetupColumn("Recorded");
            ImGui.TableHeadersRow();

            foreach (var (job, allLines) in OrderedJobs(pack))
            {
                var lines = allLines.Where(l => !pack.IsUnused(l)).ToList();
                var unused = allLines.Count - lines.Count;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(JobLabel(job));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(lines.Count.ToString());
                if (unused > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Warn, $"+{unused} unused");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Lines for events that no longer exist. Find them under UNUSED LINES on the Lines tab.");
                }
                ImGui.TableNextColumn();
                var rec = lines.Count(l => l.AudioPath != null);
                ImGui.TextColored(rec == lines.Count && lines.Count > 0 ? Good : Muted, $"{rec} / {lines.Count}");
            }

            ImGui.EndTable();
        }
        ImGui.TextColored(Muted, "Add or remove jobs on the Lines tab.");

        DrawExport(pack);
    }

    private void LoadFolder(string folder)
    {
        if (plugin.PackDirty)
            plugin.SavePack();
        plugin.Player.Stop();
        plugin.Configuration.PackFolder = folder.Trim().Trim('"');
        plugin.Configuration.Save();
        plugin.ReloadPack();
        editJob = string.Empty;
    }

    // ---- Lines tab -----------------------------------------------------------------

    private void DrawLines()
    {
        var pack = plugin.Speaker.Pack;
        if (pack == null)
        {
            ImGui.TextColored(Muted, "Load or create a voice pack on the Voice Pack tab first.");
            return;
        }

        // Job picker + add/remove.
        var jobs = OrderedJobs(pack).Select(j => j.Key).ToList();
        if (!jobs.Contains(editJob))
            editJob = EventWatcher.CurrentJob() is { } cur && jobs.Contains(cur) ? cur : jobs.FirstOrDefault() ?? string.Empty;

        // Left: the job you're editing (and removing it). Right, after the divider: adding a job.
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Editing");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        if (ImGui.BeginCombo("##editjob", editJob.Length > 0 ? JobLabel(editJob) : "(no jobs)"))
        {
            foreach (var j in jobs)
                if (ImGui.Selectable(JobLabel(j), j == editJob))
                    editJob = j;
            ImGui.EndCombo();
        }

        if (editJob.Length > 0)
        {
            ImGui.SameLine();
            var canRemove = ImGui.GetIO().KeyCtrl;
            if (!canRemove) ImGui.BeginDisabled();
            var removed = ImGui.Button("Remove job");
            if (!canRemove) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl to remove this job and all its lines. (Audio files are kept.)");
            if (removed)
            {
                pack.Jobs.Remove(editJob);
                editJob = string.Empty;
                Changed();
                return;
            }
        }

        var available = KnownEvents.Jobs.Where(j => !pack.Jobs.ContainsKey(j)).ToArray();
        if (available.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, "  |  ");
            ImGui.SameLine();
            ImGui.TextUnformatted("New");
            ImGui.SameLine();
            if (!available.Contains(addJobChoice))
                addJobChoice = EventWatcher.CurrentJob() is { } cur2 && available.Contains(cur2) ? cur2 : available[0];
            ImGui.SetNextItemWidth(80);
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
            }
        }

        if (editJob.Length == 0 || !pack.Jobs.TryGetValue(editJob, out var jobLines))
        {
            ImGui.TextColored(Muted, "Add a job to start writing lines.");
            return;
        }

        DrawLinesToolbar(pack, jobLines);

        ImGui.Spacing();
        ImGui.BeginChild("##lines", new Vector2(0, 0));

        // Job abilities aren't offered on ALL any more, but ALL lines written for them earlier still show.
        var events = pack.AllEvents()
                         .Where(e => e.AppliesTo(editJob) || jobLines.Any(l => l.Event == e.Name))
                         .ToList();
        var q = lineSearch.Trim();

        bool Visible(KnownEvents.Info ev)
        {
            var evLines = jobLines.Where(l => l.Event == ev.Name).ToList();
            if (ev.Name == focusEvent) return true;
            if (q.Length > 0)
                return ev.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       (ev.IsCustom && ev.Trigger.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                       evLines.Any(l => l.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
            // Your own events always show: they're what you're working on, even before they have lines.
            return ev.IsCustom || !plugin.Configuration.LinesHideEmpty || evLines.Count > 0;
        }

        // General events: combat & duties, social, treasure, weather, idle
        var common = events.Where(e => !e.IsCustom && e.Jobs == null && Visible(e)).ToList();
        if (common.Count > 0 && Section("lines:common", "GENERAL EVENTS", true))
        {
            foreach (var group in common.GroupBy(e => e.Category ?? KnownEvents.CommonCategories[0])
                                        .OrderBy(g => Array.IndexOf(KnownEvents.CommonCategories, g.Key)))
            {
                var groupLines = group.Sum(e => jobLines.Count(l => l.Event == e.Name));
                if (!SubSection($"lines:common:{group.Key}",
                                $"{group.Key}  ({groupLines} line{(groupLines == 1 ? "" : "s")})")) continue;
                if (group.Key == "Weather")
                    HelpNote("Plays when the weather changes while you're out in the world, or when you arrive somewhere " +
                             "with inclement weather (anything but clear skies and clouds) that you weren't already in. " +
                             "Not in duties or combat. One voice in the party comments.");
                else if (group.Key == "Places")
                    HelpNote("Plays when you (or a friend, arriving where you are) arrive in a different kind of place. " +
                             "A city without its own lines plays the City line; Home plays House/Apartment if it has none. " +
                             "Homes: Settings > Homes.");
                else if (group.Key == "State")
                    HelpNote("Plays when you (or a friend near you) start doing something, outside duties and combat. " +
                             "Friends' Diving, Flying and Fishing can't be seen, so those only play for you.");
                else if (group.Key == "Time")
                    HelpNote("Eorzea time: Dawn 5:00, Morning 7:00, Day 12:00, Dusk 17:00, Evening 19:00, Night 22:00. " +
                             "A new period comes every 9-15 real minutes, so these have a 30-minute cooldown. " +
                             "One voice in the party comments; not in duties or combat.");
                else if (group.Key == "Idle")
                    HelpNote($"Plays now and then after {plugin.Configuration.IdleMinutes}+ minutes standing still, out of combat and duties " +
                             $"(how often: Settings, currently {plugin.Configuration.IdleFrequency}).");
                foreach (var nested in group.Where(e => e.Group != null).GroupBy(e => e.Group!))
                {
                    var n = nested.Sum(e => jobLines.Count(l => l.Event == e.Name));
                    if (!NestedGroup($"lines:common:{group.Key}:{nested.Key}", $"{nested.Key}  ({n} line{(n == 1 ? "" : "s")})"))
                        continue;
                    foreach (var ev in nested)
                        DrawEventLines(pack, jobLines, ev, ShortLabel(ev, nested.Key));
                    ImGui.TreePop();
                }
                foreach (var ev in group.Where(e => e.Group == null)) DrawEventLines(pack, jobLines, ev);
                ImGui.TreePop();
            }
        }

        // Job abilities: on a job's page. On ALL only if old ALL lines exist for them.
        var jobEvents = events.Where(e => !e.IsCustom && e.Jobs != null && Visible(e)).ToList();
        if (jobEvents.Count > 0 && Section($"lines:job:{(editJob == VoicePack.SharedJob ? "all" : "job")}",
                                           "JOB ABILITIES", true))
        {
            if (editJob == VoicePack.SharedJob)
                HelpNote("These ALL lines are from before job abilities moved to each job's page. They still play " +
                         "on every job that has the ability. Add them to a job instead to tidy this section away.");
            foreach (var ev in jobEvents) DrawEventLines(pack, jobLines, ev);
        }

        // Custom events, grouped by category
        var custom = events.Where(e => e.IsCustom && Visible(e)).ToList();
        if (custom.Count > 0 && Section("lines:custom", "YOUR EVENTS", true))
        {
            foreach (var group in custom.GroupBy(e => e.Category ?? "Other").OrderBy(g => g.Key == "Other").ThenBy(g => g.Key))
            {
                if (!SubSection($"lines:cat:{group.Key}", group.Key)) continue;
                foreach (var ev in group) DrawEventLines(pack, jobLines, ev);
                ImGui.TreePop();
            }
        }

        DrawUnusedLines(pack, jobLines);

        if (common.Count + jobEvents.Count + custom.Count == 0)
            ImGui.TextColored(Muted, q.Length > 0
                ? "Nothing matches your search."
                : "No lines yet. Click \"+ Add lines for...\" to pick an event, or \"+ New event\" to make your own.");

        ImGui.EndChild();
        focusEvent = null;

        if (deleteCustom != null)
        {
            pack.CustomEvents.Remove(deleteCustom);
            editingCustom.Remove(deleteCustom.Id);
            deleteCustom = null;
            Changed();
        }
    }

    private string lineSearch = string.Empty;

    /// <summary>Lines whose event no longer exists: move them to a real event, or delete them.</summary>
    private void DrawUnusedLines(VoicePack pack, List<Line> jobLines)
    {
        var unused = jobLines.Where(pack.IsUnused).ToList();
        if (unused.Count == 0 || !Section("lines:unused", $"UNUSED LINES ({unused.Count})", true))
            return;

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "These lines belong to events that no longer exist (retired, or a deleted custom event), " +
                                 "so they never play. Move them to another event to reuse them, or delete them.");
        ImGui.PopTextWrapPos();

        var targets = pack.AllEvents().Where(e => e.AppliesTo(editJob)).ToList();
        Line? remove = null;
        foreach (var line in unused)
        {
            ImGui.PushID("unused" + line.Id);
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(line.Text) ? "(no text)" : line.Text);
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"[{line.Event}]");

            ImGui.Indent();
            ImGui.SetNextItemWidth(170);
            if (ImGui.BeginCombo("##moveto", "Move to event..."))
            {
                foreach (var ev in targets)
                {
                    if (ImGui.Selectable(ev.Label))
                    {
                        line.Event = ev.Name;
                        Changed();
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            var ctrl = ImGui.GetIO().KeyCtrl;
            if (!ctrl) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Delete"))
                remove = line;
            if (!ctrl) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl to delete this line. (Its audio file is kept.)");
            ImGui.Unindent();
            ImGui.PopID();
        }

        if (remove != null)
        {
            jobLines.Remove(remove);
            Changed();
        }
    }
    private string? focusEvent;        // event to scroll to / expand on the Lines tab
    private string? focusCustomEvent;  // (unused since 0.6.0)
    private string? pendingTab;

    private ImGuiTabItemFlags TabFlags(string tab) =>
        pendingTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    /// <summary>Search, "show all" toggle, the event picker, and New event.</summary>
    private void DrawLinesToolbar(VoicePack pack, List<Line> jobLines)
    {
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##linesearch", "Search events and lines...", ref lineSearch, 64);

        ImGui.SameLine();
        if (ImGui.Button("+ Add lines for..."))
        {
            pickerSearch = string.Empty;
            ImGui.OpenPopup("##eventpicker");
        }
        DrawEventPicker(pack, jobLines);

        ImGui.SameLine();
        if (ImGui.Button("+ New event"))
            CreateCustomEvent(pack, null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Make your own event: pick conditions (an emote, a buff, a Moodle, a place...) and write lines for it.");

        ImGui.SameLine();
        var showAll = !plugin.Configuration.LinesHideEmpty;
        if (ImGui.Checkbox("Show all events", ref showAll))
        {
            plugin.Configuration.LinesHideEmpty = !showAll;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: only events you've written lines for (and your own events).\nOn: every event, empty or not.");
    }

    private string pickerSearch = string.Empty;

    /// <summary>A searchable list of every event for this job, by category, with what triggers it.</summary>
    private void DrawEventPicker(VoicePack pack, List<Line> jobLines)
    {
        if (!ImGui.BeginPopup("##eventpicker")) return;

        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##picksearch", "Search events...", ref pickerSearch, 64);

        var q = pickerSearch.Trim();
        string Heading(KnownEvents.Info e) =>
            e.IsCustom ? $"Your events: {e.Category}"
            : e.Jobs != null ? "Job abilities"
            : e.Group != null ? $"{e.Category} > {e.Group}"
            : e.Category ?? KnownEvents.CommonCategories[0];

        var events = pack.AllEvents()
                         .Where(e => e.AppliesTo(editJob))
                         .Where(e => q.Length == 0 ||
                                     e.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                     e.Trigger.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                     Heading(e).Contains(q, StringComparison.OrdinalIgnoreCase))
                         .ToList();

        ImGui.BeginChild("##picklist", new Vector2(460, 360));
        if (events.Count == 0)
            ImGui.TextColored(Muted, "Nothing matches.");
        foreach (var group in events.GroupBy(Heading))
        {
            ImGui.Spacing();
            ImGui.TextColored(Good, group.Key);
            ImGui.Separator();
            foreach (var ev in group)
            {
                var count = jobLines.Count(l => l.Event == ev.Name);
                var label = count > 0 ? $"{ev.Label}  ({count})" : ev.Label;
                if (ImGui.Selectable($"{label}##pick_{ev.Name}"))
                {
                    if (count == 0) AddLine(pack, jobLines, ev.Name);
                    focusEvent = ev.Name;
                    lineSearch = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(ev.Trigger);
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("+ New event"))
        {
            CreateCustomEvent(pack, null);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        ImGui.TextColored(Muted, "Picking an event adds a first line for it (or jumps to it).");
        ImGui.EndPopup();
    }

    /// <summary>Custom events whose trigger editor is open on the Lines tab.</summary>
    private readonly HashSet<string> editingCustom = new();
    private CustomEvent? deleteCustom;

    /// <summary>
    /// Makes a new custom event, or a variant of a built-in one, and opens it (trigger editor and all)
    /// on the Lines tab.
    /// </summary>
    private void CreateCustomEvent(VoicePack pack, KnownEvents.Info? variantOf)
    {
        var created = variantOf == null
            ? new CustomEvent { Conditions = { new CustomCondition() } }
            : new CustomEvent
            {
                Name = $"{variantOf.Label} (variant)",
                Category = "Variants",
                Priority = Math.Max(2, variantOf.Priority),
                CooldownSeconds = variantOf.CooldownSeconds,
                Conditions =
                {
                    new CustomCondition { Type = ConditionType.BuiltIn, Value = variantOf.Name },
                    new CustomCondition(),
                },
            };
        pack.CustomEvents.Add(created);
        editingCustom.Add(created.Id);
        focusEvent = created.Id;
        lineSearch = string.Empty;
        Changed();
    }

    private void AddLine(VoicePack pack, List<Line> jobLines, string eventName)
    {
        jobLines.Add(new Line
        {
            Id = pack.NextLineId(editJob, eventName),
            Event = eventName,
            Mood = KnownEvents.Moods[0],
            Weight = 1,
            Job = editJob,
        });
        Changed();
    }

    private void DrawEventLines(VoicePack pack, List<Line> jobLines, KnownEvents.Info ev, string? label = null)
    {
        var evLines = jobLines.Where(l => l.Event == ev.Name).ToList();
        var rec = evLines.Count(l => l.AudioPath != null);
        var header = $"{label ?? ev.Label}  ({rec}/{evLines.Count} recorded)###ev_{ev.Name}";

        if (ev.Name == focusEvent || lineSearch.Trim().Length > 0)
        {
            ImGui.SetNextItemOpen(true);
            if (ev.Name == focusEvent) ImGui.SetScrollHereY();
        }

        if (!ImGui.CollapsingHeader(header))
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(ev.Trigger);
            return;
        }

        ImGui.Indent();
        if (ev.IsCustom && pack.CustomEvents.FirstOrDefault(c => c.Id == ev.Name) is { } custom)
        {
            DrawCustomTrigger(pack, custom);
        }
        else
        {
            ImGui.TextColored(Muted, ev.Trigger);
            var triggerInfo = JobTriggers.Describe(ev.Name, editJob);
            if (triggerInfo.Length > 0)
                InfoMarker(triggerInfo);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Make a variant##var_{ev.Name}"))
                CreateCustomEvent(pack, ev);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A copy of this event with extra conditions (a Moodle, a place, the time...).\n" +
                                 "When they're true, the variant's lines play instead of these.");
            var variants = pack.CustomEvents.Where(c => c.VariantOf == ev.Name).Select(c => c.Name).ToList();
            if (variants.Count > 0)
                ImGui.TextColored(Muted, $"Variants: {string.Join(", ", variants)}");
        }

        Line? toDelete = null;
        foreach (var line in evLines)
        {
            if (DrawLine(pack, line, ev))
                toDelete = line;
        }

        if (toDelete != null)
        {
            jobLines.Remove(toDelete);
            Changed();
        }

        if (ImGui.Button($"+ Add line##add_{ev.Name}"))
            AddLine(pack, jobLines, ev.Name);

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private static void HelpNote(string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>A top-level collapsible section whose open/closed state is remembered.</summary>
    private bool Section(string id, string title, bool openByDefault)
    {
        var collapsed = plugin.Configuration.CollapsedSections;
        var key = id + (openByDefault ? "" : ":closed-default");
        var open = openByDefault ? !collapsed.Contains(key) : collapsed.Contains(key);
        ImGui.SetNextItemOpen(open);

        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.25f, 0.25f, 0.3f, 0.9f));
        var nowOpen = ImGui.CollapsingHeader($"{title}###{id}");
        ImGui.PopStyleColor();

        if (nowOpen != open)
        {
            // Store only deviations from the default.
            if (nowOpen == openByDefault) collapsed.Remove(key); else collapsed.Add(key);
            plugin.Configuration.Save();
        }

        if (nowOpen) ImGui.Spacing();
        return nowOpen;
    }

    /// <summary>A category inside a section (tree node). Caller must TreePop() when it returns true.</summary>
    private bool SubSection(string id, string title)
    {
        var collapsed = plugin.Configuration.CollapsedSections;
        var open = !collapsed.Contains(id);
        ImGui.SetNextItemOpen(open);
        var nowOpen = ImGui.TreeNodeEx($"{title}###{id}", ImGuiTreeNodeFlags.SpanAvailWidth);
        if (nowOpen != open)
        {
            if (nowOpen) collapsed.Remove(id); else collapsed.Add(id);
            plugin.Configuration.Save();
        }
        return nowOpen;
    }

    /// <summary>A custom event's trigger at the top of its lines: a summary, or the full editor.</summary>
    private void DrawCustomTrigger(VoicePack pack, CustomEvent custom)
    {
        var editing = editingCustom.Contains(custom.Id);
        ImGui.TextColored(custom.IsUsable ? Muted : Warn, $"When: {custom.Summary}");
        ImGui.SameLine();
        if (ImGui.SmallButton(editing ? $"Done##trg_{custom.Id}" : $"Edit trigger##trg_{custom.Id}"))
        {
            if (editing) editingCustom.Remove(custom.Id);
            else editingCustom.Add(custom.Id);
        }
        if (!editing) return;

        ImGui.PushID(custom.Id);
        ImGui.Indent();
        if (DrawCustomEvent(pack, custom))
            deleteCustom = custom;
        ImGui.Unindent();
        ImGui.PopID();
        ImGui.Separator();
    }

    /// <summary>"City: Kugane" reads as "Kugane" inside the Cities group; plain "City" is the fallback.</summary>
    private static string ShortLabel(KnownEvents.Info ev, string group) =>
        group == "Cities"
            ? ev.Label == "City" ? "Any city (plays where a city has no lines)" : ev.Label.Replace("City: ", "")
            : ev.Label;

    /// <summary>A group inside a category (Cities, Housing), closed by default. Caller must TreePop() when true.</summary>
    private bool NestedGroup(string id, string title)
    {
        var sections = plugin.Configuration.CollapsedSections; // for these, holds the *opened* ones
        var key = id + ":open";
        var searching = lineSearch.Trim().Length > 0;
        var open = sections.Contains(key) || searching;
        ImGui.SetNextItemOpen(open);
        var nowOpen = ImGui.TreeNodeEx($"{title}###{id}", ImGuiTreeNodeFlags.SpanAvailWidth);
        if (nowOpen != open && !searching)
        {
            if (nowOpen) sections.Add(key); else sections.Remove(key);
            plugin.Configuration.Save();
        }
        return nowOpen;
    }

    /// <summary>Draws one line's editor. Returns true if the user asked to delete it.</summary>
    private bool DrawLine(VoicePack pack, Line line, KnownEvents.Info ev)
    {
        var delete = false;
        ImGui.PushID(line.Job + line.Id);
        ImGui.Separator();

        var text = line.Text;
        if (ImGui.InputTextMultiline("##text", ref text, 500, new Vector2(-1, ImGui.GetTextLineHeight() * 2.6f)))
        {
            line.Text = text;
            Changed();
        }
        if (string.IsNullOrWhiteSpace(line.Text) && line.AudioPath == null)
            ImGui.TextColored(Warn, "Empty line: it won't play until you write something or add audio.");

        // Mood
        ImGui.SetNextItemWidth(95);
        if (ImGui.BeginCombo("##mood", string.IsNullOrEmpty(line.Mood) ? "mood" : line.Mood))
        {
            foreach (var m in KnownEvents.Moods)
            {
                if (ImGui.Selectable(m, m == line.Mood))
                {
                    line.Mood = m;
                    Changed();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mood: a note for whoever records the line.");

        // Weight
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        var weight = line.Weight;
        if (ImGui.SliderInt("##weight", ref weight, 1, 5, "weight %d"))
        {
            line.Weight = weight;
            Changed();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Higher weight = picked more often.");

        // Partner events: is this line for first choosing a partner, or for switching?
        if (ev.SupportsInitial)
        {
            ImGui.SameLine();
            var initial = line.Initial;
            if (ImGui.Checkbox("Initial##initial", ref initial))
            {
                line.Initial = initial;
                Changed();
            }
            InfoMarker("Checked: plays when you first choose a partner (nobody had it before).\n" +
                       "Unchecked: plays when you switch your partner to someone else.\n\n" +
                       "With no Initial lines, choosing a first partner stays quiet.");
        }

        // Audio
        ImGui.SameLine();
        if (line.AudioPath != null)
        {
            if (ImGui.SmallButton("Play"))
            {
                var length = plugin.Player.Play(line.AudioPath, plugin.Configuration.Volume, 99);
                if (plugin.Configuration.LipSync)
                    plugin.Lips.Start(Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0, length);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(line.AudioPath);
        }
        else
        {
            ImGui.TextColored(Muted, "no audio");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(line.AudioPath != null ? "Replace..." : "Add audio..."))
        {
            var target = line;
            plugin.FileDialogs.OpenFileDialog("Choose a recording for this line", "Audio{.mp3,.wav}", (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path)) return;
                try
                {
                    plugin.Player.Stop(); // release the old file if it's playing
                    pack.ImportAudio(target, path);
                    Changed();
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Couldn't import audio");
                    Plugin.ChatGui.PrintError($"Couldn't add that audio file: {ex.Message}");
                }
            });
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The file is copied into the voice pack folder.");

        ImGui.SameLine();
        ImGui.TextColored(Muted, line.Id);

        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 60);
        var ctrl = ImGui.GetIO().KeyCtrl;
        if (!ctrl) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Delete"))
            delete = true;
        if (!ctrl) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl to delete this line. (Its audio file is kept.)");

        ImGui.PopID();
        return delete;
    }

    // ---- Helpers ---------------------------------------------------------------------

    /// <summary>A small (i) icon after the previous item that shows text on hover.</summary>
    private static void InfoMarker(string text)
    {
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(text, FontAwesomeIcon.InfoCircle, null);
    }

    private static IEnumerable<KeyValuePair<string, List<Line>>> OrderedJobs(VoicePack pack) =>
        pack.Jobs.OrderBy(j => j.Key == VoicePack.SharedJob).ThenBy(j => j.Key);

    private static string JobLabel(string job) => job == VoicePack.SharedJob ? "ALL (any job)" : job;
}
