using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Ipc.Exceptions;

namespace OuterVoice;

/// <summary>
/// Reads Moodles (custom statuses from the Moodles plugin) through Moodles' public IPC.
/// Works for you and, when Moodles data is synced (e.g. via Lightless), for friends too.
/// If Moodles isn't installed this quietly returns nothing.
/// </summary>
public static class MoodlesIpc
{
    // Matches Moodles' MoodlesStatusInfo tuple; its enum fields are plain ints here.
    private static bool disabled;
    private static bool loggedMissing;

    public static IReadOnlyList<string> TitlesOn(nint characterAddress)
    {
        if (disabled || characterAddress == 0)
            return [];

        try
        {
            var subscriber = Plugin.PluginInterface.GetIpcSubscriber<nint,
                List<(int Version, Guid GUID, int IconID, string Title, string Description, string CustomVFXPath,
                      long ExpireTicks, int Type, int Stacks, int StackSteps, uint Modifiers, Guid ChainedStatus,
                      int ChainTrigger, string Applier, string Dispeller, bool Permanent)>>(
                "Moodles.GetStatusManagerInfoByPtrV2");

            return subscriber.InvokeFunc(characterAddress)
                             .Select(m => Clean(m.Title))
                             .Where(t => t.Length > 0)
                             .ToList();
        }
        catch (IpcNotReadyError)
        {
            if (!loggedMissing)
            {
                Plugin.Log.Info("Moodles isn't installed or loaded; Moodle conditions won't match.");
                loggedMissing = true;
            }
            return [];
        }
        catch (Exception ex)
        {
            // A Moodles update changed its IPC; stop trying so we don't spam errors every frame.
            Plugin.Log.Warning(ex, "Couldn't read Moodles; Moodle conditions are turned off until the plugin reloads.");
            disabled = true;
            return [];
        }
    }

    /// <summary>Strips Moodles' [color=...]/[glow=...] formatting tags from a title.</summary>
    public static string Clean(string? title) =>
        Regex.Replace(title ?? string.Empty, @"\[/?[a-zA-Z]+(=[^\]]*)?\]", string.Empty).Trim();
}
