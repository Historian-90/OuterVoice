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

    private string lineSearch = string.Empty;

    /// <summary>Lines whose event no longer exists: move them to a real event, or delete them.</summary>
    private void DrawUnusedLines(VoicePack pack, List<Line> jobLines)
    {
        var unused = jobLines.Where(pack.IsUnused).ToList();
        ImGui.SetWindowFontScale(1.3f);
        ImGui.TextUnformatted($"Unused lines ({unused.Count})");
        ImGui.SetWindowFontScale(1f);
        if (unused.Count == 0)
        {
            ImGui.TextColored(Muted, "Nothing here any more.");
            return;
        }

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
    private string? pendingTab;



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
                    SelectEvent(ev.Name);
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
        SelectEvent(created.Id);
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

    private static string JobLabel(string job) => job == VoicePack.SharedJob ? "Every job" : job;
}
