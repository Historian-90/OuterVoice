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
    public int Version { get; set; } = 2;

    public bool Enabled { get; set; } = true;

    /// <summary>Folder holding voicepack.json and the audio clips.</summary>
    public string PackFolder { get; set; } = string.Empty;

    /// <summary>0-100.</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Scale (and mute) voices with the game's Master and Voice volume settings.</summary>
    public bool FollowGameVolume { get; set; } = true;

    /// <summary>Run friends' clips through a limiter so nothing is ear-blastingly loud.</summary>
    public bool LimitFriendLoudness { get; set; } = true;

    /// <summary>Move the speaking character's mouth while their line plays.</summary>
    public bool LipSync { get; set; } = true;

    /// <summary>Overrides the pack's global cooldown when >= 0.</summary>
    public int GlobalCooldownOverride { get; set; } = -1;

    public TextOutput TextOutput { get; set; } = TextOutput.BattleTalk;

    /// <summary>Show the text even when a line has no audio file yet.</summary>
    public bool TextWithoutAudio { get; set; } = true;

    /// <summary>Name on the battle-talk pop-up. Empty = use the pack's character first name.</summary>
    public string SpeakerName { get; set; } = string.Empty;

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

    /// <summary>Folder inside Dropbox or OneDrive where your pack zip is published.</summary>
    public string PublishFolder { get; set; } = string.Empty;

    /// <summary>Re-publish automatically every time you click Save.</summary>
    public bool PublishOnSave { get; set; } = true;

    /// <summary>Friends' packs you follow through their Dropbox or OneDrive links.</summary>
    public List<Sync.Subscription> Subscriptions { get; set; } = new();

    /// <summary>Houses and apartments you've registered (Settings > Homes).</summary>
    public List<RegisteredPlace> RegisteredPlaces { get; set; } = new();

    /// <summary>Battle-talk pop-up style number passed to the game (0 = the usual box).</summary>
    public int BattleTalkStyle { get; set; }

    /// <summary>How chatty each kind of event is (missing = Normal). Idle uses IdleFrequency instead.</summary>
    public Dictionary<string, Chattiness> Chatter { get; set; } = new();

    public Chattiness ChatterFor(string category) =>
        Chatter.TryGetValue(category, out var c) ? c : Chattiness.Normal;

    /// <summary>How often idle lines play once you've been standing still for a while.</summary>
    public IdleFrequency IdleFrequency { get; set; } = IdleFrequency.Normal;

    /// <summary>Minutes standing still (out of combat and duties) before idle lines can start.</summary>
    public int IdleMinutes { get; set; } = 5;

    /// <summary>Lines tab: only show events that already have lines.</summary>
    public bool LinesHideEmpty { get; set; } = true;

    /// <summary>Sections the user collapsed (or opened) on the Lines tab.</summary>
    public HashSet<string> CollapsedSections { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public class RegisteredPlace
{
    /// <summary>The game's house id (HouseId.Id).</summary>
    public ulong HouseId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Arriving here plays your Home line.</summary>
    public bool Home { get; set; } = true;

    /// <summary>Friends who arrive here play their Home line too (they live here with you).</summary>
    public bool FriendsHome { get; set; }
}

/// <summary>Off, or a cooldown multiplier: Less = 3x as long between lines, More = half as long.</summary>
public enum Chattiness
{
    Off,
    Less,
    Normal,
    More,
}

public enum IdleFrequency
{
    Off,
    Rare,
    Normal,
    Frequent,
}

[Serializable]
public class FriendPrefs
{
    public bool Muted { get; set; }

    /// <summary>0-100, applied on top of your main volume.</summary>
    public int Volume { get; set; } = 100;
}
