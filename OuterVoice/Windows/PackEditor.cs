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
        HelpMarker("The pack's suggested time between lines. Your own setting on the Settings page overrides it.");

        DrawPortrait(pack);

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
        ImGui.TextColored(Muted, "Each line's job is set with the job box on the line (Lines page).");

        DrawExport(pack);
    }

    private bool previewPortraitWhenReady;

    /// <summary>Voice Pack > Portrait: the picture shown in battle-talk pop-ups for this character.</summary>
    private void DrawPortrait(VoicePack pack)
    {
        Section("Portrait");
        var path = pack.PortraitPath;
        const float preview = 96f;
        if (path != null)
        {
            var wrap = Plugin.TextureProvider.GetFromFileAbsolute(path).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(preview, preview * wrap.Height / Math.Max(1, wrap.Width)));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "Your character's picture in the game's battle-talk pop-up (Settings > Text pop-ups > " +
                                 "Battle-talk pop-up). Friends with your pack see it too. Needs Penumbra. Tip: a PNG with a " +
                                 "transparent background looks like the game's own NPC portraits.");
        ImGui.PopTextWrapPos();
        if (ImGui.Button(path == null ? "Choose a picture..." : "Change picture..."))
        {
            plugin.FileDialogs.OpenFileDialog("Choose a portrait", "Images{.png,.jpg,.jpeg}", (ok, file) =>
            {
                if (!ok || string.IsNullOrEmpty(file)) return;
                try
                {
                    pack.ImportPortrait(file);
                    Changed();
                }
                catch (Exception ex)
                {
                    SetStatus(ex.Message, true);
                }
            });
        }
        if (path != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove picture"))
            {
                pack.Portrait = string.Empty;
                Changed();
            }
            ImGui.SameLine();
            if (ImGui.Button("Preview pop-up"))
                plugin.Speaker.ShowText(plugin.Speaker.DisplayName(), "This is how your lines will look.", pack);
        }
        if (path != null)
        {
            // Framing: applied when you let go of a slider, then previewed once the new picture is ready.
            ImGui.SetNextItemWidth(200);
            var size = pack.PortraitSize;
            if (ImGui.SliderInt("Size##portrait", ref size, 10, 100, "%d%%")) pack.PortraitSize = size;
            var sizeDone = ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SetNextItemWidth(200);
            var px = pack.PortraitX;
            if (ImGui.SliderInt("Left / right##portrait", ref px, 0, 100, "%d")) pack.PortraitX = px;
            var xDone = ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SetNextItemWidth(200);
            var py = pack.PortraitY;
            if (ImGui.SliderInt("Top / bottom##portrait", ref py, 0, 100, "%d")) pack.PortraitY = py;
            var yDone = ImGui.IsItemDeactivatedAfterEdit();
            HelpMarker("Size: how big the picture is in the pop-up's portrait area.\n" +
                       "Left / right and Top / bottom: where it sits (Top / bottom 100 = resting on the text box).\n" +
                       "A preview pop-up shows each change.");
            if (sizeDone || xDone || yDone)
            {
                Changed();
                plugin.Portraits.IconFor(pack); // start rebuilding now
                previewPortraitWhenReady = true;
            }
            if (previewPortraitWhenReady && plugin.Portraits.IconFor(pack) != null)
            {
                previewPortraitWhenReady = false;
                plugin.Speaker.ShowText(plugin.Speaker.DisplayName(), "This is how your lines will look.", pack);
            }
        }
        if (plugin.Portraits.Status.Length > 0 && path != null)
            ImGui.TextColored(plugin.Portraits.StatusIsError ? Bad : Good, plugin.Portraits.Status);
        if (path != null && plugin.Configuration.TextOutput != TextOutput.BattleTalk)
            ImGui.TextColored(Warn, "Pop-ups are set to something other than Battle-talk, so the portrait won't show.");
        ImGui.EndGroup();
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

        var targets = pack.AllEvents().Where(EventShown).ToList();
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
            if (pack.Jobs.TryGetValue(remove.Job, out var list)) list.Remove(remove);
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
                         .Where(EventShown)
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
                    if (count == 0) AddLine(pack, ev.Name);
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

    /// <summary>Adds an empty line: to the job being shown, or Every job (job abilities: their job).</summary>
    private void AddLine(VoicePack pack, string eventName)
    {
        var job = DefaultJobFor(pack.AllEvents().FirstOrDefault(e => e.Name == eventName));
        if (!pack.Jobs.TryGetValue(job, out var lines)) pack.Jobs[job] = lines = new List<Line>();
        lines.Add(new Line
        {
            Id = pack.NextLineId(job, eventName),
            Event = eventName,
            Weight = 1,
            Job = job,
        });
        Changed();
    }

    private string DefaultJobFor(KnownEvents.Info? ev)
    {
        var specific = editJob is not (AllLines or VoicePack.SharedJob) && editJob.Length > 0;
        if (ev?.Jobs is { Length: > 0 } jobs)
            return specific && jobs.Contains(editJob) ? editJob
                : EventWatcher.CurrentJob() is { } cur && jobs.Contains(cur) ? cur
                : jobs[0];
        return specific ? editJob : VoicePack.SharedJob;
    }

    /// <summary>
    /// The jobs box on a line: tick boxes for Every job, roles and single jobs. Job abilities only
    /// offer the jobs that have them.
    /// </summary>
    private void DrawLineJobs(VoicePack pack, Line line, KnownEvents.Info ev)
    {
        ImGui.SetNextItemWidth(130);
        if (!ImGui.BeginCombo("##jobs", line.JobsLabel, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Plays on: {line.JobsLabel}.\nPick Every job, a role (all healers...), or any mix of jobs.");
            return;
        }

        var allowed = ev.Jobs is { Length: > 0 } evJobs ? evJobs.ToList() : KnownEvents.CombatJobs.ToList();
        var set = !string.Equals(line.Job, VoicePack.SharedJob, StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { line.Job }
            : line.OnlyJobs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        if (ev.Jobs == null)
        {
            var every = set.Count == 0;
            if (ImGui.Checkbox("Every job", ref every) && every)
            {
                set.Clear();
                changed = true;
            }
            ImGui.Separator();
        }

        foreach (var (role, jobs) in KnownEvents.Roles)
        {
            var roleJobs = jobs.Where(allowed.Contains).ToList();
            if (roleJobs.Count < 2) continue;
            var all = roleJobs.All(set.Contains);
            if (ImGui.Checkbox($"{role}##role", ref all))
            {
                if (all) set.UnionWith(roleJobs);
                else set.ExceptWith(roleJobs);
                changed = true;
            }
        }
        ImGui.Separator();

        foreach (var job in allowed)
        {
            var on = set.Contains(job);
            if (ImGui.Checkbox($"{job}##job", ref on))
            {
                if (on) set.Add(job);
                else set.Remove(job);
                changed = true;
            }
        }
        ImGui.EndCombo();

        // A job ability always needs at least one of its jobs.
        if (changed && (ev.Jobs == null || set.Count > 0))
        {
            plugin.Player.Stop();
            pack.SetLineJobs(line, set);
            Changed();
        }
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

        // Which jobs this line plays on
        DrawLineJobs(pack, line, ev);

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
