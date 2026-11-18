using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
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
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 420) };
        Size = new Vector2(920, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private enum Page { Lines, Pack, Friends, Settings }

    private Page page = Page.Lines;
    private const float RailWidth = 46f;
    private static readonly Vector4 Accent = new(0.55f, 0.42f, 0.85f, 1f);

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        // Jumps requested from elsewhere ("open this event on the Lines page").
        if (pendingTab == "Lines") page = Page.Lines;
        pendingTab = null;

        ImGui.BeginChild("##rail", new Vector2(RailWidth, 0));
        RailButton(Page.Lines, FontAwesomeIcon.Comments, "Lines\nWhat your character says, and when.");
        RailButton(Page.Pack, FontAwesomeIcon.FolderOpen, "Voice Pack\nYour pack's folder and character, exporting and publishing.");
        RailButton(Page.Friends, FontAwesomeIcon.UserFriends, "Friends\nFriends' voice packs: links, updates, volume.");
        RailButton(Page.Settings, FontAwesomeIcon.Cog, "Settings\nVolume, pop-ups, chatter, homes, testing.");
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##page", Vector2.Zero);
        switch (page)
        {
            case Page.Lines: DrawLines(); break;
            case Page.Pack:
                ImGui.BeginChild("##packpage", Vector2.Zero);
                DrawPack();
                ImGui.EndChild();
                break;
            case Page.Friends: DrawFriends(); break;
            case Page.Settings: DrawSettings(); break;
        }
        ImGui.EndChild();
    }

    private void RailButton(Page target, FontAwesomeIcon icon, string tooltip)
    {
        var active = page == target;
        var size = new Vector2(RailWidth - 8, RailWidth - 8);
        var clicked = active
            ? ImGuiComponents.IconButton((int)target, icon, Accent, Accent, Accent, size)
            : ImGuiComponents.IconButton((int)target, icon, new Vector4(0, 0, 0, 0), null, null, size);
        if (clicked) page = target;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.Spacing();
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
        ImGui.BeginChild("##settings", Vector2.Zero);
        try
        {
            DrawSettingsBody(cfg);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>A collapsible group on the Settings page (open by default, remembered while the window is open).</summary>
    private static bool Group(string title, bool openByDefault = true)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.35f));
        var open = ImGui.CollapsingHeader(title, openByDefault ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        ImGui.PopStyleColor();
        return open;
    }

    private void DrawSettingsBody(Configuration cfg)
    {
        if (Group("Playback"))
        {
            var volume = cfg.Volume;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("Volume", ref volume, 0, 100, "%d%%"))
            {
                cfg.Volume = volume;
                cfg.Save();
            }

            var follow = cfg.FollowGameVolume;
            if (ImGui.Checkbox("Follow the game's Master and Voice volume", ref follow))
            {
                cfg.FollowGameVolume = follow;
                cfg.Save();
            }
            HelpMarker("Voices get quieter with the game's Master and Voice sliders, and go silent when either is muted.");

            var packCd = plugin.Speaker.Pack?.GlobalCooldownSeconds ?? 20;
            var cd = cfg.GlobalCooldownOverride >= 0 ? cfg.GlobalCooldownOverride : packCd;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("Gap between lines", ref cd, 0, 120, "%d sec"))
            {
                cfg.GlobalCooldownOverride = cd;
                cfg.Save();
            }
            HelpMarker("Minimum time between any two lines. Urgent events (deaths, wipes) can still cut in.");

            var lips = cfg.LipSync;
            if (ImGui.Checkbox("Move the speaker's mouth while they talk", ref lips))
            {
                cfg.LipSync = lips;
                if (!lips) plugin.Lips.Stop();
                cfg.Save();
            }
            HelpMarker("Uses the game's own talking-mouth animation (like NPCs in dialogue). Only on your screen; friends with your pack see it on theirs.");
        }

        if (Group("Chatter"))
            DrawChatter(cfg);

        if (Group("Text pop-ups"))
        {
            var textMode = (int)cfg.TextOutput;
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Show text as", ref textMode, "Nothing\0Chat (only you see it)\0Toast\0Battle-talk pop-up\0"))
            {
                cfg.TextOutput = (TextOutput)textMode;
                cfg.Save();
            }

            if (cfg.TextOutput == TextOutput.BattleTalk)
            {
                var secs = cfg.BattleTalkSeconds;
                ImGui.SetNextItemWidth(220);
                if (ImGui.SliderFloat("Pop-up duration", ref secs, 1f, 10f, "%.1f sec"))
                {
                    cfg.BattleTalkSeconds = secs;
                    cfg.Save();
                }

                var icon = (int)cfg.PortraitIconId;
                ImGui.SetNextItemWidth(220);
                if (ImGui.InputInt("Fallback portrait icon", ref icon))
                {
                    cfg.PortraitIconId = (uint)Math.Max(0, icon);
                    cfg.Save();
                }
                HelpMarker("Voice packs with a picture (Voice Pack page > Portrait) show it in their pop-ups (needs Penumbra).\n" +
                           "This game icon number is used for speakers without one. 0 = no portrait.");
            }

            if (cfg.TextOutput is TextOutput.Chat or TextOutput.BattleTalk)
            {
                var speakerName = cfg.SpeakerName;
                ImGui.SetNextItemWidth(220);
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

            if (ImGui.Button("Preview text"))
                plugin.Speaker.ShowText(plugin.Speaker.DisplayName(), "This is how your lines will look.");
        }

        if (Group("Homes"))
            DrawHomes(cfg);

        if (Group("Jobs"))
        {
            var shared = cfg.SharedLinesOnOtherJobs;
            if (ImGui.Checkbox("Use Every job lines on jobs without their own", ref shared))
            {
                cfg.SharedLinesOnOtherJobs = shared;
                cfg.Save();
            }
            HelpMarker("Lines written under \"Every job\" work on any job. With this off, only jobs that have their own lines speak.");
        }

        if (Group("Testing", openByDefault: false))
            DrawTest();
    }

    /// <summary>Settings > Chatter: each kind of event on/off, and how often.</summary>
    private void DrawChatter(Configuration cfg)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "How talkative each kind of event is, for every voice you hear (yours and friends'). " +
                                 "Less = three times as long between lines, More = half as long.");
        ImGui.PopTextWrapPos();

        if (!ImGui.BeginTable("##chatter", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("How often", ImGuiTableColumnFlags.WidthStretch);

        var rows = KnownEvents.CommonCategories.Concat([KnownEvents.JobAbilities, KnownEvents.YourEvents]);
        foreach (var category in rows)
        {
            ImGui.PushID(category);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(category);
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(220);

            if (category == "Idle")
            {
                var freq = (int)cfg.IdleFrequency;
                if (ImGui.Combo("##idle", ref freq, "Off\0Rare (about every 30 min)\0Normal (about every 15 min)\0Frequent (about every 7 min)\0"))
                {
                    cfg.IdleFrequency = (IdleFrequency)freq;
                    cfg.Save();
                }
                if (cfg.IdleFrequency != IdleFrequency.Off)
                {
                    ImGui.SameLine();
                    var mins = cfg.IdleMinutes;
                    ImGui.SetNextItemWidth(140);
                    if (ImGui.SliderInt("##idlemins", ref mins, 2, 30, "after %d min still"))
                    {
                        cfg.IdleMinutes = mins;
                        cfg.Save();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("How long you must stand still before idle lines can play.\nMoving, fighting, a duty, crafting or gathering starts the wait over.");
                }
            }
            else
            {
                var level = (int)cfg.ChatterFor(category);
                if (ImGui.Combo("##level", ref level, "Off\0Less\0Normal\0More\0"))
                {
                    if ((Chattiness)level == Chattiness.Normal) cfg.Chatter.Remove(category);
                    else cfg.Chatter[category] = (Chattiness)level;
                    cfg.Save();
                }
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /// <summary>Settings > Testing: play any event that has lines, for any job.</summary>
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

    /// <summary>Settings > Homes: register houses/apartments for Home lines and Place conditions.</summary>
    private void DrawHomes(Configuration cfg)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "Your own house, FC house, private chambers and apartment count as home automatically. " +
                                 "Register any other place here (say, your partner's house) to make it home too, or to " +
                                 "use it in a custom event's Place condition.");
        ImGui.PopTextWrapPos();

        var here = WorldState.CurrentHouse();
        if (here is { } h)
        {
            var registered = WorldState.RegisteredHere(h);
            var owned = WorldState.IsOwnedByMe(h);
            ImGui.TextUnformatted($"You're in: {WorldState.HouseName(h)}{(owned ? " (yours: already home)" : "")}");
            if (registered == null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Register this place"))
                {
                    cfg.RegisteredPlaces.Add(new RegisteredPlace { HouseId = h.Id, Name = WorldState.HouseName(h), Home = !owned });
                    cfg.Save();
                }
            }
        }
        else
        {
            ImGui.TextColored(Muted, "Go inside a house or apartment to register it.");
        }

        if (cfg.RegisteredPlaces.Count == 0) return;
        if (!ImGui.BeginTable("##homes", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Home");
        ImGui.TableSetupColumn("Friends live here");
        ImGui.TableSetupColumn("");
        ImGui.TableHeadersRow();

        RegisteredPlace? remove = null;
        foreach (var r in cfg.RegisteredPlaces)
        {
            ImGui.PushID(r.HouseId.ToString());
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var name = r.Name;
            if (ImGui.InputText("##name", ref name, 80)) r.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit()) cfg.Save();

            ImGui.TableNextColumn();
            var home = r.Home;
            if (ImGui.Checkbox("##home", ref home)) { r.Home = home; cfg.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Arriving here plays your Home line.");

            ImGui.TableNextColumn();
            var friends = r.FriendsHome;
            if (ImGui.Checkbox("##friends", ref friends)) { r.FriendsHome = friends; cfg.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Friends arriving here play their Home line too (they live here with you).\n" +
                                 "Off: they play their House/Apartment line, like any visitor.");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Remove")) remove = r;
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (remove != null)
        {
            cfg.RegisteredPlaces.Remove(remove);
            cfg.Save();
        }
    }

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
