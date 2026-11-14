using System;
using Dalamud.Plugin.Services;

namespace OuterVoice;

/// <summary>
/// Moves a character's mouth while their line plays, using the game's own "talking" mouth
/// animations (the ones NPCs use in dialogue). Mouth only: the body keeps doing whatever it
/// was doing. Purely visual and local: each player's plugin animates the speaker on their own
/// screen, so friends see your character talk too (if they have your pack).
/// </summary>
public sealed class LipSync : IDisposable
{
    // ActionTimeline rows for the talking mouth: roughly 1s, 2s and 4s of speech.
    private const ushort SpeakShort = 629;
    private const ushort SpeakMiddle = 630;
    private const ushort SpeakLong = 631;
    private const ushort SpeakNone = 0;

    private uint entityId;
    private DateTime endsAt;
    private DateTime nextBlockAt;
    private bool active;

    public LipSync() => Plugin.Framework.Update += OnUpdate;

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Stop();
    }

    /// <summary>Start the speaker's mouth moving for about <paramref name="duration"/>.</summary>
    public void Start(uint speakerEntityId, TimeSpan duration)
    {
        Stop();
        if (speakerEntityId == 0 || duration < TimeSpan.FromMilliseconds(300))
            return;

        entityId = speakerEntityId;
        endsAt = DateTime.UtcNow + duration;
        nextBlockAt = DateTime.UtcNow;
        active = true;
    }

    /// <summary>Close the mouth now (a line was cut off or the plugin is unloading).</summary>
    public void Stop()
    {
        if (!active) return;
        active = false;
        Set(entityId, SpeakNone);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!active || DateTime.UtcNow < nextBlockAt)
            return;

        // Chain the longest talking animation that fits in the time left, then shorter ones.
        var left = endsAt - DateTime.UtcNow;
        var (timeline, length) = left.TotalSeconds switch
        {
            >= 3.5 => (SpeakLong, 4.0),
            >= 1.5 => (SpeakMiddle, 2.0),
            >= 0.4 => (SpeakShort, 1.0),
            _ => (SpeakNone, 0.0),
        };

        if (timeline == SpeakNone)
        {
            Stop();
            return;
        }

        if (!Set(entityId, timeline))
        {
            active = false; // the speaker left (zoned, out of range)
            return;
        }

        nextBlockAt = DateTime.UtcNow + TimeSpan.FromSeconds(length - 0.05);
    }

    private static unsafe bool Set(uint id, ushort timeline)
    {
        try
        {
            var obj = Plugin.ObjectTable.SearchByEntityId(id);
            if (obj == null || obj.Address == 0) return false;
            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
            chara->Timeline.SetLipsOverrideTimeline(timeline);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Lip sync failed");
            return false;
        }
    }
}
