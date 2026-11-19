using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace OuterVoice.Windows;

/// <summary>
/// "What's new": opens by itself the first time the plugin runs after an update, showing the
/// changelog entries since the version you last saw. Settings > About opens it any time.
/// </summary>
public sealed class WhatsNewWindow : Window
{
    private readonly Plugin plugin;
    private List<Section> sections = new();

    private sealed record Section(Version Version, string Title, List<string> Bullets);

    public WhatsNewWindow(Plugin plugin) : base("Outer Voice: What's new###OuterVoiceWhatsNew")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    /// <summary>On load: after an update (not a first install), show what changed since last time.</summary>
    public void ShowIfUpdated()
    {
        var cfg = plugin.Configuration;
        var current = Current;
        Version.TryParse(cfg.LastSeenVersion, out var lastSeen);
        cfg.LastSeenVersion = current.ToString();
        cfg.Save();

        if (lastSeen == null || lastSeen >= current) return; // first install, or nothing new
        Open(since: lastSeen);
    }

    /// <summary>Shows entries newer than <paramref name="since"/> (null = the last few versions).</summary>
    public void Open(Version? since = null)
    {
        var all = Parse(LoadChangelog());
        sections = since == null
            ? all.Take(5).ToList()
            : all.Where(s => s.Version > since && s.Version <= Current).ToList();
        if (sections.Count == 0) sections = all.Take(1).ToList();
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(0.55f, 0.42f, 0.85f, 1f), $"You're on Outer Voice {Current}.");
        ImGui.Separator();

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("##changes", new Vector2(0, -footer));
        ImGui.PushTextWrapPos();
        foreach (var section in sections)
        {
            ImGui.Spacing();
            ImGui.SetWindowFontScale(1.15f);
            ImGui.TextUnformatted(section.Title);
            ImGui.SetWindowFontScale(1f);
            foreach (var bullet in section.Bullets)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted(bullet);
            }
        }
        ImGui.PopTextWrapPos();
        ImGui.EndChild();

        if (ImGui.Button("Got it")) IsOpen = false;
    }

    private static string LoadChangelog()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (stream == null) return string.Empty;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Reads "## 1.2.3" headings and "- " bullets (continuation lines are joined).</summary>
    private static List<Section> Parse(string markdown)
    {
        var result = new List<Section>();
        Section? current = null;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## "))
            {
                var title = line[3..].Trim();
                var versionText = title.Split(' ', '-')[0].Trim();
                current = Version.TryParse(versionText, out var version)
                    ? new Section(version, title, new List<string>())
                    : null;
                if (current != null) result.Add(current);
            }
            else if (current != null && line.TrimStart().StartsWith("- "))
            {
                current.Bullets.Add(line.TrimStart()[2..].Trim());
            }
            else if (current != null && current.Bullets.Count > 0 && line.StartsWith("  ") && line.Trim().Length > 0)
            {
                current.Bullets[^1] += " " + line.Trim();
            }
        }
        return result.OrderByDescending(s => s.Version).ToList();
    }
}
