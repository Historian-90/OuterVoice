using System;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OuterVoice.Windows;

namespace OuterVoice;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ITextureReadbackProvider TextureReadback { get; private set; } = null!;

    private const string Command = "/outervoice";
    private const string ShortCommand = "/ov";

    internal static Plugin Instance { get; private set; } = null!;

    public Configuration Configuration { get; }
    public VoicePlayer Player { get; }
    public Ducking Ducking { get; }
    public ActionWatcher Actions { get; }
    public LipSync Lips { get; }
    public Sync.Publisher Publisher { get; }
    public Sync.SubscriptionManager Subscriptions { get; private set; } = null!;
    public Speaker Speaker { get; }
    public Portraits Portraits { get; }
    public WhatsNewWindow WhatsNew { get; }
    public string? PackError { get; private set; }

    /// <summary>True when the chosen folder has no voice pack yet (offer to create one).</summary>
    public bool PackMissing { get; private set; }

    /// <summary>True when the pack has been edited in the plugin but not saved.</summary>
    public bool PackDirty { get; set; }

    public FileDialogManager FileDialogs { get; } = new();

    private readonly EventWatcher watcher;
    private readonly WindowSystem windowSystem = new("OuterVoice");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Instance = this;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Version < 2)
        {
            // 0.6.0: the Lines tab shows only events with lines by default (with a picker for the rest).
            Configuration.LinesHideEmpty = true;
            Configuration.Version = 2;
            Configuration.Save();
        }

        Player = new VoicePlayer();
        Lips = new LipSync();
        Publisher = new Sync.Publisher(this);
        Speaker = new Speaker(this);
        Portraits = new Portraits();
        Ducking = new Ducking(this);
        Actions = new ActionWatcher();
        ReloadPack();
        PackSharing.LoadFriends(Speaker);
        Subscriptions = new Sync.SubscriptionManager(this);

        watcher = new EventWatcher(Speaker);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);
        WhatsNew = new WhatsNewWindow(this);
        windowSystem.AddWindow(WhatsNew);
        WhatsNew.ShowIfUpdated();
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.Draw += FileDialogs.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Outer Voice. Also: on, off, test <event> [job], reload. Short form: /ov",
        });
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { ShowInHelp = false });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(ShortCommand);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= FileDialogs.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        windowSystem.RemoveAllWindows();

        // Unsaved edits survive plugin updates and reloads: keep them for next time.
        if (PackDirty) WriteRecovery();

        watcher.Dispose();
        Portraits.Dispose();
        Ducking.Dispose();
        Actions.Dispose();
        Subscriptions.Dispose();
        Player.Dispose();
        Lips.Dispose();
    }

    public void ReloadPack()
    {
        PackDirty = false;
        PackMissing = false;
        if (string.IsNullOrWhiteSpace(Configuration.PackFolder))
        {
            Speaker.Pack = null;
            PackError = "Choose a folder for your voice pack (Browse...). It can be an empty folder.";
            return;
        }

        try
        {
            Speaker.Pack = VoicePack.Load(Configuration.PackFolder);
            PackError = null;
            TidyLineIds(Speaker.Pack);
            var recovery = Path.Combine(Speaker.Pack.Folder, RecoveryName);
            PackRecovery = File.Exists(recovery) ? recovery : null;
        }
        catch (FileNotFoundException)
        {
            Speaker.Pack = null;
            PackMissing = true;
            PackError = "There's no voice pack in this folder yet.";
        }
        catch (Exception ex)
        {
            Speaker.Pack = null;
            PackError = $"Couldn't read the voice pack: {ex.Message}";
            Log.Error(ex, "Failed to load voice pack");
        }
    }

    /// <summary>
    /// Gives older lines ids that match their event ("start_01" becomes "duty_start_03"), saves the
    /// pack, then removes the recordings' old copies.
    /// </summary>
    private static void TidyLineIds(VoicePack pack)
    {
        try
        {
            var before = pack.Jobs.Values.SelectMany(l => l).Count(l => !System.Text.RegularExpressions.Regex.IsMatch(
                l.Id, $"^{System.Text.RegularExpressions.Regex.Escape(l.Event)}_\\d+$"));
            if (before == 0) return;

            // Keep a copy of the pack file as it was, just in case.
            var json = Path.Combine(pack.Folder, VoicePack.FileName);
            if (File.Exists(json)) File.Copy(json, json + ".before-id-tidy", overwrite: true);

            var oldFiles = pack.NormalizeLineIds();
            pack.Save();
            foreach (var f in oldFiles)
            {
                try { File.Delete(f); } catch { /* an old copy left behind is harmless */ }
            }
            Log.Information($"Renamed {before} line id(s) to match their events");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't tidy line ids");
        }
    }

    public void CreatePack()
    {
        try
        {
            Speaker.Pack = VoicePack.CreateNew(Configuration.PackFolder, CurrentCharacterId() ?? string.Empty);
            PackError = null;
            PackMissing = false;
            PackDirty = false;
        }
        catch (Exception ex)
        {
            PackError = $"Couldn't create a voice pack here: {ex.Message}";
            Log.Error(ex, "Failed to create voice pack");
        }
    }

    public bool SavePack()
    {
        if (Speaker.Pack == null) return false;
        try
        {
            Speaker.Pack.Save();
            PackDirty = false;
            DiscardRecovery();
            if (Configuration.PublishOnSave && !string.IsNullOrWhiteSpace(Configuration.PublishFolder))
                Publisher.Publish();
            return true;
        }
        catch (Exception ex)
        {
            PackError = $"Couldn't save: {ex.Message}";
            Log.Error(ex, "Failed to save voice pack");
            return false;
        }
    }

    // ---- Unsaved changes safety net ---------------------------------------------------------

    public const string RecoveryName = "voicepack.unsaved.json";

    /// <summary>Unsaved edits kept from an earlier session (plugin update, reload, crash), if any.</summary>
    public string? PackRecovery { get; private set; }

    private DateTime lastRecoveryWrite;

    /// <summary>Writes the pack's unsaved state next to it (voicepack.unsaved.json).</summary>
    public void WriteRecovery()
    {
        if (Speaker.Pack is not { } pack || pack.Folder.Length == 0) return;
        try
        {
            File.WriteAllText(Path.Combine(pack.Folder, RecoveryName),
                              Newtonsoft.Json.JsonConvert.SerializeObject(pack, VoicePack.JsonSettings));
            lastRecoveryWrite = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't keep a copy of unsaved changes");
        }
    }

    /// <summary>While there are unsaved changes, keep a copy every 20 seconds (in case the game crashes).</summary>
    public void KeepRecoveryFresh()
    {
        if (PackDirty && DateTime.UtcNow - lastRecoveryWrite > TimeSpan.FromSeconds(20))
            WriteRecovery();
    }

    /// <summary>Puts the kept unsaved changes back (the current pack file is backed up first).</summary>
    public void RestoreRecovery()
    {
        if (PackRecovery == null || Speaker.Pack is not { } pack) return;
        try
        {
            var json = Path.Combine(pack.Folder, VoicePack.FileName);
            if (File.Exists(json)) File.Copy(json, json + ".before-restore", overwrite: true);
            File.Copy(PackRecovery, json, overwrite: true);
            File.Delete(PackRecovery);
            ReloadPack();
        }
        catch (Exception ex)
        {
            PackError = $"Couldn't restore: {ex.Message}";
            Log.Error(ex, "Couldn't restore unsaved changes");
        }
    }

    public void DiscardRecovery()
    {
        if (Speaker.Pack is { } pack)
        {
            try { File.Delete(Path.Combine(pack.Folder, RecoveryName)); } catch { /* nothing to remove */ }
        }
        PackRecovery = null;
    }

    /// <summary>"First Last@World" for the logged-in character.</summary>
    public static string? CurrentCharacterId()
    {
        var me = ObjectTable.LocalPlayer;
        if (me == null) return null;
        var world = me.HomeWorld.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(world) ? me.Name.TextValue : $"{me.Name.TextValue}@{world}";
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty)
        {
            case "on":
                Configuration.Enabled = true;
                Configuration.Save();
                ChatGui.Print("Outer Voice: on.");
                break;
            case "off":
                Configuration.Enabled = false;
                Configuration.Save();
                Player.Stop();
                ChatGui.Print("Outer Voice: off.");
                break;
            case "reload":
                ReloadPack();
                ChatGui.Print(PackError ?? "Line pack reloaded.");
                break;
            case "test" when parts.Length > 1:
            {
                // /ov test <event> [job]
                var testArgs = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var job = testArgs.Length > 1 ? testArgs[1].ToUpperInvariant() : EventWatcher.CurrentJob() ?? VoicePack.SharedJob;
                if (Speaker.Test(testArgs[0], job) == null)
                    ChatGui.PrintError($"No {job} lines found for event '{testArgs[0]}'.");
                break;
            }
            default:
                configWindow.Toggle();
                break;
        }
    }
}
