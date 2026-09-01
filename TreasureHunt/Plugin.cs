using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons;
using TreasureHunt.Helpers;
using TreasureHunt.Services;
using TreasureHunt.Windows;

namespace TreasureHunt;

public sealed unsafe class Plugin : IDalamudPlugin
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
        var subArgs = args.Trim().ToLower();
        if (subArgs == "debug")
        {
            DebugNearbyObjects();
            return;
        }
        if (subArgs == "dep" || subArgs == "deps" || subArgs == "dependency")
        {
            DebugDependencies();
            return;
        }
        if (subArgs.StartsWith("ui"))
        {
            DebugUi(subArgs);
            return;
        }
        ToggleMainUi();
    }

    private void DebugNearbyObjects()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            ChatGui.Print("[TreasureHunt] 无法获取玩家位置");
            return;
        }

        ChatGui.Print($"[TreasureHunt] === 附近对象列表 (坐标: {player.Position.X:F1}, {player.Position.Y:F1}, {player.Position.Z:F1}) ===");

        var count = 0;
        foreach (var obj in ObjectTable)
        {
            if (obj == null) continue;
            var dist = System.Numerics.Vector3.Distance(player.Position, obj.Position);
            if (dist > 100f) continue;

            var name = obj.Name.ToString();
            if (string.IsNullOrEmpty(name)) name = "(无名)";

            var dataId = 0u;
            try
            {
                unsafe
                {
                    var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
                    dataId = go->BaseId;
                }
            }
            catch { }

            ChatGui.Print($"  [{obj.ObjectKind}] Name=\"{name}\" DataId={dataId} Dist={dist:F1}m");
            count++;
        }
        ChatGui.Print($"[TreasureHunt] 共 {count} 个对象 (100m内)");
    }

    private void DebugDependencies()
    {
        DependencyManager.ForceRefresh();
        var statuses = DependencyManager.GetAllStatuses();
        ChatGui.Print("[TreasureHunt] === 依赖插件状态 ===");
        foreach (var kv in statuses)
        {
            var s = kv.Value;
            ChatGui.Print($"  {s.DisplayName}: {(s.IsAvailable ? "OK" : "未检测到")}{(s.IsRequired ? " (必需)" : "")}");
        }
    }

    private void DebugUi(string subArgs)
    {
        // /thunt ui - 列出当前可见的 PDR 相关 addon + 底层 API 状态
        ChatGui.Print("[TreasureHunt] === PDR 市场检测 ===");

        var addonName = PdrMarketHelper.GetMarketAddonName();
        if (!string.IsNullOrEmpty(addonName))
        {
            ChatGui.Print($"  原生市场窗口: {addonName}");
        }
        else
        {
            ChatGui.Print("  原生市场窗口: 未检测到(PDR是ImGui窗口，正常)");
        }

        var visible = PdrMarketHelper.ListVisibleAddons();
        if (visible.Count > 0)
        {
            ChatGui.Print($"  可见原生窗口: {string.Join(", ", visible)}");
        }

        ChatGui.Print("  ---");
        ChatGui.Print("  [底层 API 状态]");
        var debugInfo = PdrMarketHelper.GetDebugInfo();
        foreach (var line in debugInfo.Split('\n'))
        {
            ChatGui.Print($"  {line}");
        }

        ChatGui.Print("  ---");
        ChatGui.Print("  用法: 先用 /pdr market <物品ID> 打开市场");
        ChatGui.Print("  再用 /thunt ui 查看底层数据是否加载");
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    private void DrawUi()
    {
        WindowSystem.Draw();
    }
}
