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

            foreach (var (job, lines) in OrderedJobs(pack))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(JobLabel(job));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(lines.Count.ToString());
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

        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("##editjob", editJob.Length > 0 ? JobLabel(editJob) : "(no jobs)"))
        {
            foreach (var j in jobs)
                if (ImGui.Selectable(JobLabel(j), j == editJob))
                    editJob = j;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var available = KnownEvents.Jobs.Where(j => !pack.Jobs.ContainsKey(j)).ToArray();
        if (available.Length > 0)
        {
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

        if (editJob.Length > 0)
        {
            ImGui.SameLine();
            var canRemove = ImGui.GetIO().KeyCtrl;
            if (!canRemove) ImGui.BeginDisabled();
            if (ImGui.Button("Remove job"))
            {
                pack.Jobs.Remove(editJob);
                editJob = string.Empty;
                Changed();
                if (!canRemove) ImGui.EndDisabled();
                return;
            }
            if (!canRemove) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl to remove this job and all its lines. (Audio files are kept.)");
        }

        if (editJob.Length == 0 || !pack.Jobs.TryGetValue(editJob, out var jobLines))
        {
            ImGui.TextColored(Muted, "Add a job to start writing lines.");
            return;
        }

        ImGui.Spacing();
        ImGui.BeginChild("##lines", new Vector2(0, 0));

        foreach (var ev in KnownEvents.All)
        {
            if (!ev.AppliesTo(editJob))
                continue;

            var evLines = jobLines.Where(l => l.Event == ev.Name).ToList();
            var rec = evLines.Count(l => l.AudioPath != null);
            var header = $"{ev.Label}  ({rec}/{evLines.Count} recorded)###ev_{ev.Name}";

            if (!ImGui.CollapsingHeader(header))
            {
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(ev.Trigger);
                continue;
            }

            ImGui.Indent();
            ImGui.TextColored(Muted, ev.Trigger);
            var triggerInfo = JobTriggers.Describe(ev.Name, editJob);
            if (triggerInfo.Length > 0)
                InfoMarker(triggerInfo);

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
            {
                jobLines.Add(new Line
                {
                    Id = pack.NextLineId(editJob, ev.Name),
                    Event = ev.Name,
                    Mood = KnownEvents.Moods[0],
                    Weight = 1,
                    Job = editJob,
                });
                Changed();
            }

            ImGui.Unindent();
            ImGui.Spacing();
        }

        ImGui.EndChild();
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
                plugin.Player.Play(line.AudioPath, plugin.Configuration.Volume, 99);
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
