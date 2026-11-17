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
                       evLines.Any(l => l.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
            return !plugin.Configuration.LinesHideEmpty || evLines.Count > 0;
        }

        // Every job: combat & duties, weather, idle
        var common = events.Where(e => !e.IsCustom && e.Jobs == null && Visible(e)).ToList();
        if (common.Count > 0 && Section("lines:common", "EVERY JOB", true))
        {
            foreach (var group in common.GroupBy(e => e.Category ?? KnownEvents.CommonCategories[0])
                                        .OrderBy(g => Array.IndexOf(KnownEvents.CommonCategories, g.Key)))
            {
                if (!SubSection($"lines:common:{group.Key}", group.Key)) continue;
                if (group.Key == "Weather")
                    HelpNote("Plays when the weather changes while you're out in the world, or when you arrive somewhere " +
                             "with inclement weather (anything but clear skies and clouds) that you weren't already in. " +
                             "Not in duties or combat. One voice in the party comments.");
                else if (group.Key == "Idle")
                    HelpNote($"Plays now and then after {plugin.Configuration.IdleMinutes}+ minutes out of combat and duties " +
                             $"(how often: Settings, currently {plugin.Configuration.IdleFrequency}).");
                foreach (var ev in group) DrawEventLines(pack, jobLines, ev);
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
        if (custom.Count > 0 && Section("lines:custom", "CUSTOM EVENTS", true))
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
            ImGui.TextColored(Muted, q.Length > 0 ? "Nothing matches your search." : "No events with lines yet. Use \"+ Add lines to...\" above.");

        ImGui.EndChild();
        focusEvent = null;
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
    private string? focusCustomEvent;  // custom event to expand on the Custom Events tab
    private string? pendingTab;

    private ImGuiTabItemFlags TabFlags(string tab) =>
        pendingTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    /// <summary>Search, hide-empty toggle, and a picker to start lines for a hidden event.</summary>
    private void DrawLinesToolbar(VoicePack pack, List<Line> jobLines)
    {
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##linesearch", "Search events and lines...", ref lineSearch, 64);

        ImGui.SameLine();
        var hide = plugin.Configuration.LinesHideEmpty;
        if (ImGui.Checkbox("Hide empty", ref hide))
        {
            plugin.Configuration.LinesHideEmpty = hide;
            plugin.Configuration.Save();
        }

        if (!plugin.Configuration.LinesHideEmpty)
            return;

        var empty = pack.AllEvents()
                        .Where(e => e.AppliesTo(editJob) && jobLines.All(l => l.Event != e.Name))
                        .ToList();
        if (empty.Count == 0)
            return;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(170);
        if (ImGui.BeginCombo("##addto", "+ Add lines to..."))
        {
            foreach (var ev in empty)
            {
                if (ImGui.Selectable(ev.Label))
                {
                    AddLine(pack, jobLines, ev.Name);
                    focusEvent = ev.Name;
                }
            }
            ImGui.EndCombo();
        }
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

    private void DrawEventLines(VoicePack pack, List<Line> jobLines, KnownEvents.Info ev)
    {
        var evLines = jobLines.Where(l => l.Event == ev.Name).ToList();
        var rec = evLines.Count(l => l.AudioPath != null);
        var header = $"{ev.Label}  ({rec}/{evLines.Count} recorded)###ev_{ev.Name}";

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
        ImGui.TextColored(Muted, ev.Trigger);
        var triggerInfo = JobTriggers.Describe(ev.Name, editJob);
        if (triggerInfo.Length > 0)
            InfoMarker(triggerInfo);
        if (ev.IsCustom)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Edit trigger##trg_{ev.Name}"))
            {
                pendingTab = "Custom Events";
                focusCustomEvent = ev.Name;
            }
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
