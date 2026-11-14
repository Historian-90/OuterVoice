using System;
using System.IO;
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
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    private const string Command = "/outervoice";
    private const string ShortCommand = "/ov";

    internal static Plugin Instance { get; private set; } = null!;

    public Configuration Configuration { get; }
    public VoicePlayer Player { get; }
    public LipSync Lips { get; }
    public Sync.Publisher Publisher { get; }
    public Sync.SubscriptionManager Subscriptions { get; private set; } = null!;
    public Speaker Speaker { get; }
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

        Player = new VoicePlayer();
        Lips = new LipSync();
        Publisher = new Sync.Publisher(this);
        Speaker = new Speaker(this);
        ReloadPack();
        PackSharing.LoadFriends(Speaker);
        Subscriptions = new Sync.SubscriptionManager(this);

        watcher = new EventWatcher(Speaker);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);
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

        watcher.Dispose();
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
                var job = testArgs.Length > 1 ? testArgs[1].ToUpperInvariant() : EventWatcher.CurrentJob() ?? "SGE";
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
