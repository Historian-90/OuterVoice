using System;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace OuterVoice;

/// <summary>
/// Shows the game's own battle-talk pop-up (the "This is Thancred." box) on your screen.
/// Purely local UI: nothing is sent to the server.
/// </summary>
public static class BattleTalk
{
    public static void Show(string name, string text, float seconds, uint portraitIconId)
    {
        // Game UI must be touched from the game's main thread.
        Plugin.Framework.RunOnFrameworkThread(() => ShowNow(name, text, seconds, portraitIconId));
    }

    private static unsafe void ShowNow(string name, string text, float seconds, uint portraitIconId)
    {
        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
                return;

            if (portraitIconId != 0)
                ui->ShowBattleTalkImage(name, text, seconds, portraitIconId, 0, 0, 0);
            else
                ui->ShowBattleTalk(name, text, seconds, 0);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't show battle-talk pop-up");
        }
    }
}
