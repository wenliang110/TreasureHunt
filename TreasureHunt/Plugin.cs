using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons;
using TreasureHunt.Services;
using TreasureHunt.Windows;

namespace TreasureHunt;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "TreasureHunt";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/thunt";

    public Configuration Configuration { get; init; }

    public MapPurchaseService MapPurchaseService { get; init; }
    public MapDecipherService MapDecipherService { get; init; }
    public NavigationService NavigationService { get; init; }
    public TreasureCofferService TreasureCofferService { get; init; }
    public PortalDungeonService PortalDungeonService { get; init; }
    public MoneyBagService MoneyBagService { get; init; }
    public TreasureHuntOrchestrator Orchestrator { get; init; }

    public readonly WindowSystem WindowSystem = new("TreasureHunt");

    private MainWindow MainWindow { get; init; }
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.ValidateAndFix();
        Configuration.Save();

        MapPurchaseService = new MapPurchaseService(this);
        MapDecipherService = new MapDecipherService(this);
        NavigationService = new NavigationService(this);
        TreasureCofferService = new TreasureCofferService(this);
        PortalDungeonService = new PortalDungeonService(this);
        MoneyBagService = new MoneyBagService(this);
        Orchestrator = new TreasureHuntOrchestrator(this);

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 TreasureHunt 自动挖宝面板"
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("TreasureHunt 插件已加载");
    }

    public void Dispose()
    {
        Orchestrator?.Dispose();
        MoneyBagService?.Dispose();
        PortalDungeonService?.Dispose();
        TreasureCofferService?.Dispose();
        NavigationService?.Dispose();
        MapDecipherService?.Dispose();
        MapPurchaseService?.Dispose();

        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow?.Dispose();
        ConfigWindow?.Dispose();

        CommandManager.RemoveHandler(CommandName);

        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    private void DrawUi()
    {
        WindowSystem.Draw();
    }
}
