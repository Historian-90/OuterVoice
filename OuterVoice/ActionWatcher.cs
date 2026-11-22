using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace OuterVoice;

/// <summary>
/// Sees every action as it goes off, instant or cast, for any character on screen: the game calls
/// ActionEffectHandler.Receive when an action's effect lands. Only actions from characters we care
/// about (you and friends with packs) are kept, then read once per tick by the EventWatcher.
/// </summary>
public sealed unsafe class ActionWatcher : IDisposable
{
    private readonly Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private readonly ConcurrentQueue<(uint Caster, uint ActionId)> used = new();

    /// <summary>Entity ids worth recording (set by the EventWatcher each tick).</summary>
    public volatile HashSet<uint> Watching = new();

    public ActionWatcher()
    {
        try
        {
            hook = Plugin.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                (nint)ActionEffectHandler.Addresses.Receive.Value, Detour);
            hook.Enable();
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Couldn't watch actions; \"Uses an action\" conditions won't fire.");
        }
    }

    public bool Working => hook != null;

    private void Detour(uint casterEntityId, Character* casterPtr, System.Numerics.Vector3* targetPos,
                        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects,
                        GameObjectId* targetEntityIds)
    {
        try
        {
            // ActionType 1 = a regular action (skills, spells, abilities); not items, mounts etc.
            if (header != null && header->ActionType == 1 && Watching.Contains(casterEntityId))
                used.Enqueue((casterEntityId, header->ActionId));
        }
        catch
        {
            // never break the game's own handling
        }
        hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
    }

    /// <summary>Actions that went off since the last call.</summary>
    public IEnumerable<(uint Caster, uint ActionId)> Drain()
    {
        while (used.TryDequeue(out var a))
            yield return a;
    }

    public void Dispose() => hook?.Dispose();
}
