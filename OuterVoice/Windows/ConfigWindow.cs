using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace OuterVoice.Windows;

public sealed partial class ConfigWindow : Window
{
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Good = new(0.55f, 0.9f, 0.55f, 1f);

    private readonly Plugin plugin;
    private string folderInput;
    private string testJob = string.Empty;

    public ConfigWindow(Plugin plugin) : base("Outer Voice###OuterVoiceConfig")
    {
        this.plugin = plugin;
        folderInput = plugin.Configuration.PackFolder;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 380) };
        Size = new Vector2(500, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##tabs"))
            return;

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Voice Pack"))
        {
            DrawPack();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Lines", TabFlags("Lines")))
        {
            DrawLines();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Custom Events", TabFlags("Custom Events")))
        {
            DrawCustomEvents();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Friends"))
        {
            DrawFriends();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Test"))
        {
            DrawTest();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        pendingTab = null;
    }

    // ---- Header: on/off + status at a glance ---------------------------------

    private void DrawHeader()
    {
        var cfg = plugin.Configuration;
        var enabled = cfg.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            cfg.Enabled = enabled;
            if (!enabled) plugin.Player.Stop();
            cfg.Save();
        }

        ImGui.SameLine();
        var pack = plugin.Speaker.Pack;
        var job = EventWatcher.CurrentJob();
        if (pack == null)
            ImGui.TextColored(Bad, "  No voice pack loaded");
        else if (job == null)
            ImGui.TextColored(Muted, $"  {pack.Name}");
        else if (plugin.Speaker.SpeaksOn(job))
            ImGui.TextColored(Good, $"  {pack.Name}: speaking on {job}");
        else
            ImGui.TextColored(Muted, $"  {pack.Name}: no lines for {job}");

        DrawSaveBar();
    }

    // ---- Settings tab --------------------------------------------------------

    private void DrawSettings()
    {
        var cfg = plugin.Configuration;

        Section("Audio");
        var volume = cfg.Volume;
        if (ImGui.SliderInt("Volume", ref volume, 0, 100, "%d%%"))
        {
            cfg.Volume = volume;
            cfg.Save();
        }

        var packCd = plugin.Speaker.Pack?.GlobalCooldownSeconds ?? 20;
        var cd = cfg.GlobalCooldownOverride >= 0 ? cfg.GlobalCooldownOverride : packCd;
        if (ImGui.SliderInt("Gap between lines", ref cd, 0, 120, "%d sec"))
        {
            cfg.GlobalCooldownOverride = cd;
            cfg.Save();
        }
        HelpMarker("Minimum time between any two lines. Urgent events (deaths, wipes) can still cut in.");

        var follow = cfg.FollowGameVolume;
        if (ImGui.Checkbox("Follow the game's Master and Voice volume", ref follow))
        {
            cfg.FollowGameVolume = follow;
            cfg.Save();
        }
        HelpMarker("Voices get quieter with the game's Master and Voice sliders, and go silent when either is muted.");

        Section("Text");
        var textMode = (int)cfg.TextOutput;
        if (ImGui.Combo("Show text as", ref textMode, "Nothing\0Chat (only you see it)\0Toast\0Battle-talk pop-up\0"))
        {
            cfg.TextOutput = (TextOutput)textMode;
            cfg.Save();
        }

        if (cfg.TextOutput == TextOutput.BattleTalk)
        {
            var secs = cfg.BattleTalkSeconds;
            if (ImGui.SliderFloat("Pop-up duration", ref secs, 1f, 10f, "%.1f sec"))
            {
                cfg.BattleTalkSeconds = secs;
                cfg.Save();
            }

            var icon = (int)cfg.PortraitIconId;
            if (ImGui.InputInt("Portrait icon ID", ref icon))
            {
                cfg.PortraitIconId = (uint)Math.Max(0, icon);
                cfg.Save();
            }
            HelpMarker("0 = no portrait. A custom portrait needs a Penumbra mod that replaces an icon's image.");
        }

        if (cfg.TextOutput is TextOutput.Chat or TextOutput.BattleTalk)
        {
            var speakerName = cfg.SpeakerName;
            if (ImGui.InputTextWithHint("Speaker name", plugin.Speaker.DisplayName(), ref speakerName, 64))
            {
                cfg.SpeakerName = speakerName;
                cfg.Save();
            }
            HelpMarker("Leave blank to use the voice pack character's first name.");
        }

        var textOnly = cfg.TextWithoutAudio;
        if (ImGui.Checkbox("Show text for lines that aren't recorded yet", ref textOnly))
        {
            cfg.TextWithoutAudio = textOnly;
            cfg.Save();
        }

        Section("Jobs");
        var shared = cfg.SharedLinesOnOtherJobs;
        if (ImGui.Checkbox("Use shared lines on jobs without their own", ref shared))
        {
            cfg.SharedLinesOnOtherJobs = shared;
            cfg.Save();
        }
        HelpMarker("Lines in the pack's ALL group work on any job. With this off, only jobs that have their own lines speak.");

        ImGui.Spacing();
        if (ImGui.Button("Preview text"))
            plugin.Speaker.ShowText(plugin.Speaker.DisplayName(), "This is how your lines will look.");
    }

    // ---- Test tab ------------------------------------------------------------

    private void DrawTest()
    {
        var pack = plugin.Speaker.Pack;
        if (pack == null)
        {
            ImGui.TextColored(Bad, plugin.PackError ?? "No voice pack loaded.");
            return;
        }

        var jobs = pack.Jobs.Keys.OrderBy(j => j == VoicePack.SharedJob).ThenBy(j => j).ToArray();
        if (jobs.Length == 0)
        {
            ImGui.TextUnformatted("This pack has no lines yet.");
            return;
        }

        if (!jobs.Contains(testJob))
            testJob = EventWatcher.CurrentJob() is { } current && jobs.Contains(current) ? current : jobs[0];

        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("Job", testJob))
        {
            foreach (var j in jobs)
                if (ImGui.Selectable(j, j == testJob))
                    testJob = j;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextColored(Muted, "Plays even when disabled or out of combat.");

        ImGui.Spacing();
        if (ImGui.BeginTable("events", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Event");
            ImGui.TableSetupColumn("Recorded");
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            foreach (var ev in pack.AllEvents())
            {
                var name = ev.Name;
                var lines = pack.LinesFor(testJob, name);
                if (lines.Count == 0) continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ev.Label);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(pack.GetEvent(name).Trigger);
                ImGui.TableNextColumn();
                var rec = lines.Count(l => l.AudioPath != null);
                ImGui.TextColored(rec > 0 ? Good : Muted, $"{rec} / {lines.Count}");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Play##{name}"))
                    plugin.Speaker.Test(name, testJob);
            }

            ImGui.EndTable();
        }
    }

    // ---- Helpers ---------------------------------------------------------------

    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(Muted, title.ToUpperInvariant());
        ImGui.Separator();
    }

    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextColored(Muted, "(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
