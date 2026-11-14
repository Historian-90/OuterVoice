using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace OuterVoice;

public enum TextOutput
{
    None,
    Chat,
    Toast,
    BattleTalk,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>On jobs with no lines of their own, still speak the pack's shared (ALL) lines.</summary>
    public bool SharedLinesOnOtherJobs { get; set; } = false;

    /// <summary>Folder holding voicepack.json and the audio clips.</summary>
    public string PackFolder { get; set; } = string.Empty;

    /// <summary>0-100.</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Scale (and mute) voices with the game's Master and Voice volume settings.</summary>
    public bool FollowGameVolume { get; set; } = true;

    /// <summary>Overrides the pack's global cooldown when >= 0.</summary>
    public int GlobalCooldownOverride { get; set; } = -1;

    public TextOutput TextOutput { get; set; } = TextOutput.BattleTalk;

    /// <summary>Show the text even when a line has no audio file yet.</summary>
    public bool TextWithoutAudio { get; set; } = true;

    /// <summary>Name on the battle-talk pop-up. Empty = use the pack's character first name.</summary>
    public string SpeakerName { get; set; } = string.Empty;

    /// <summary>Icon ID used as the battle-talk portrait (0 = no portrait).</summary>
    public uint PortraitIconId { get; set; } = 0;

    /// <summary>How long the battle-talk pop-up stays up, in seconds.</summary>
    public float BattleTalkSeconds { get; set; } = 4f;

    /// <summary>Play imported friends' voices when they're in your party.</summary>
    public bool HearFriends { get; set; } = true;

    /// <summary>Per-friend mute/volume, keyed by "First Last@World".</summary>
    public Dictionary<string, FriendPrefs> Friends { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Compress clips when exporting a pack for sharing.</summary>
    public bool CompressOnExport { get; set; } = true;

    public FriendPrefs PrefsFor(string characterId)
    {
        if (!Friends.TryGetValue(characterId, out var prefs))
            Friends[characterId] = prefs = new FriendPrefs();
        return prefs;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public class FriendPrefs
{
    public bool Muted { get; set; }

    /// <summary>0-100, applied on top of your main volume.</summary>
    public int Volume { get; set; } = 100;
}
