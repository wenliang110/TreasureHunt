using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;
using TreasureHunt.Services;
using TreasureHunt.Helpers;
using TreasureHunt.Models;

namespace TreasureHunt.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly List<string> _logLines = new();
    private const int MaxLogLines = 50;

    public MainWindow(Plugin plugin) : base("TreasureHunt 自动挖宝###TreasureHuntMain")
    {
        Size = new Vector2(500, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;

        _plugin = plugin;

        _plugin.Orchestrator.OnLog += OnOrchestratorLog;
        _plugin.MapPurchaseService.OnLog += OnOrchestratorLog;
        _plugin.MapDecipherService.OnLog += OnOrchestratorLog;
        _plugin.NavigationService.OnLog += OnOrchestratorLog;
        _plugin.TreasureCofferService.OnLog += OnOrchestratorLog;
        _plugin.PortalDungeonService.OnLog += OnOrchestratorLog;
        _plugin.MoneyBagService.OnLog += OnOrchestratorLog;
        _plugin.MoneyBagService.OnBagCountChanged += OnBagCountChanged;
    }

    private void OnOrchestratorLog(string message)
    {
        _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (_logLines.Count > MaxLogLines)
            _logLines.RemoveAt(0);
    }

    private void OnBagCountChanged(int count)
    {
        // 可以在这里添加额外的 UI 更新逻辑
    }

    public void Dispose()
    {
        _plugin.Orchestrator.OnLog -= OnOrchestratorLog;
        _plugin.MapPurchaseService.OnLog -= OnOrchestratorLog;
        _plugin.MapDecipherService.OnLog -= OnOrchestratorLog;
        _plugin.NavigationService.OnLog -= OnOrchestratorLog;
        _plugin.TreasureCofferService.OnLog -= OnOrchestratorLog;
        _plugin.PortalDungeonService.OnLog -= OnOrchestratorLog;
        _plugin.MoneyBagService.OnLog -= OnOrchestratorLog;
        _plugin.MoneyBagService.OnBagCountChanged -= OnBagCountChanged;
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();
        DrawFeatureToggles();
        ImGui.Separator();
        DrawActionButtons();
        ImGui.Separator();
        DrawStatusPanel();
        ImGui.Separator();
        DrawLogPanel();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(new Vector4(0.85f, 0.65f, 0.0f, 1.0f), "TreasureHunt - FF14 自动挖宝插件");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Gargantuaskin (G18) → Vault Oneiron | 国服 CN");
    }

    private void DrawFeatureToggles()
    {
        var config = _plugin.Configuration;

        // 可折叠区块标题 (匹配参考图风格)
        ImGui.TextColored(new Vector4(0.4f, 0.6f, 0.3f, 1.0f), "▼ 自动挖宝");
        ImGui.Separator();
        ImGui.TextWrapped("自动购买藏宝图，收纳并解读藏宝图，可在解读后标记位置并且传送到最近的水晶。");
        ImGui.Spacing();

        // 功能开关 (与参考图顺序一致)
        var avoidOthers = config.AvoidOthersTreasureMonsters;
        DrawCheckbox("不选中他人宝箱怪", ref avoidOthers);
        config.AvoidOthersTreasureMonsters = avoidOthers;
        var markLocation = config.EnableMarkLocation;
        DrawCheckbox("解读后标记位置", ref markLocation);
        config.EnableMarkLocation = markLocation;
        var oneClickBuyDecipher = config.EnableOneClickBuyDecipher;
        DrawCheckbox("一键买图解读", ref oneClickBuyDecipher);
        config.EnableOneClickBuyDecipher = oneClickBuyDecipher;
        var unlimitedDigging = config.EnableUnlimitedDigging;
        DrawCheckbox("无限挖掘", ref unlimitedDigging);
        config.EnableUnlimitedDigging = unlimitedDigging;
        var autoTeleport = config.EnableAutoTeleport;
        DrawCheckbox("自动传送", ref autoTeleport);
        config.EnableAutoTeleport = autoTeleport;
        var moneyBagCollection = config.EnableMoneyBagCollection;
        DrawCheckbox("TP 钱袋", ref moneyBagCollection);
        config.EnableMoneyBagCollection = moneyBagCollection;

        // TP 钱袋子状态
        if (config.EnableMoneyBagCollection)
        {
            ImGui.Indent();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f),
                $"已收集: {_plugin.MoneyBagService.BagsCollected}/100 | 剩余: {_plugin.MoneyBagService.RemainingTime}s");
            ImGui.Unindent();
        }

        ImGui.Spacing();

        // 输入框: 价格限制 + 藏宝图ID
        var priceVal = (int)config.MaxPurchasePrice;
        ImGui.Text("价格限制:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputInt(" Gil##price", ref priceVal, 1000, 10000))
        {
            config.MaxPurchasePrice = priceVal;
            config.Save();
        }

        ImGui.Spacing();

        var mapId = (int)config.TreasureMapItemId;
        ImGui.Text("藏宝图ID:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputInt("##mapid", ref mapId, 0, 0))
        {
            config.TreasureMapItemId = (uint)mapId;
            config.Save();
        }

        ImGui.Spacing();
        var fullAutoMode = config.EnableFullAutoMode;
        DrawCheckbox("全自动模式", ref fullAutoMode);
        config.EnableFullAutoMode = fullAutoMode;
    }

    private void DrawCheckbox(string label, ref bool value)
    {
        var changed = ImGui.Checkbox(label, ref value);
        if (changed)
        {
            _plugin.Configuration.Save();
        }
    }

    private void DrawActionButtons()
    {
        var isRunning = _plugin.Orchestrator.IsRunning;
        var buttonSize = new Vector2(-1, 32);

        // 开始/停止按钮 (并排，匹配参考图)
        if (!isRunning)
        {
            ImGui.Columns(2, string.Empty, false);
            if (ImGui.Button("开始", new Vector2(-1, 32)))
            {
                _logLines.Clear();
                if (_plugin.Configuration.EnableFullAutoMode)
                    _ = _plugin.Orchestrator.RunFullAutoAsync();
                else if (_plugin.Configuration.EnableOneClickBuyDecipher)
                    _ = _plugin.Orchestrator.OneClickBuyAndDecipherAsync();
            }
            ImGui.NextColumn();
            ImGui.BeginDisabled();
            ImGui.Button("停止", new Vector2(-1, 32));
            ImGui.EndDisabled();
            ImGui.Columns(1);

            ImGui.Spacing();

            // 单独功能按钮
            ImGui.Columns(3, string.Empty, false);
            if (ImGui.Button("买图", new Vector2(-1, 25)))
            {
                _ = _plugin.MapPurchaseService.PurchaseMapAsync();
            }
            ImGui.NextColumn();
            if (ImGui.Button("解读", new Vector2(-1, 25)))
            {
                _ = _plugin.MapDecipherService.DecipherMapAsync();
            }
            ImGui.NextColumn();
            if (ImGui.Button("钱袋子", new Vector2(-1, 25)))
            {
                _ = _plugin.MoneyBagService.StartCollectionAsync();
            }
            ImGui.NextColumn();
            if (ImGui.Button("设置", new Vector2(-1, 25)))
            {
                _plugin.ToggleConfigUi();
            }
            ImGui.Columns(1);

            // 命令参考
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "/thunt <宝图ID> <价格限制>");
        }
        else
        {
            ImGui.Columns(2, string.Empty, false);
            ImGui.BeginDisabled();
            ImGui.Button("开始", new Vector2(-1, 32));
            ImGui.EndDisabled();
            ImGui.NextColumn();
            if (ImGui.Button("停止", new Vector2(-1, 32)))
            {
                _plugin.Orchestrator.Cancel();
            }
            ImGui.Columns(1);
        }
    }

    private void DrawStatusPanel()
    {
        ImGui.Text("运行状态");
        ImGui.Spacing();

        var state = _plugin.Orchestrator.State;

        // 状态指示灯
        var color = _plugin.Orchestrator.IsRunning
            ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
        ImGui.TextColored(color, _plugin.Orchestrator.IsRunning ? "● 运行中" : "○ 空闲");
        ImGui.SameLine();
        ImGui.Text($"| 阶段: {state.Phase}");
        ImGui.SameLine();
        ImGui.Text($"| {state.StatusMessage ?? ""}");

        if (!string.IsNullOrEmpty(state.LastError))
        {
            ImGui.TextColored(new Vector4(0.9f, 0.2f, 0.2f, 1.0f), $"错误: {state.LastError}");
        }

        // 依赖插件状态
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "依赖插件:");
        ImGui.SameLine();

        // vnavmesh
        var vnavStatus = DependencyManager.GetStatus(DependencyType.Vnavmesh);
        ImGui.TextColored(vnavStatus.IsAvailable ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f) : new Vector4(0.9f, 0.2f, 0.2f, 1.0f),
            vnavStatus.IsAvailable ? "vnavmesh OK" : "vnavmesh 不可用");
        ImGui.SameLine();

        // 战斗插件
        var bossModStatus = DependencyManager.GetStatus(DependencyType.BossMod);
        var rsrStatus = DependencyManager.GetStatus(DependencyType.RotationSolver);
        var combatOk = bossModStatus.IsAvailable || rsrStatus.IsAvailable;
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "| 战斗: ");
        ImGui.SameLine();
        ImGui.TextColored(combatOk ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f) : new Vector4(0.9f, 0.6f, 0.2f, 1.0f),
            combatOk ? "OK" : "手动");
        ImGui.SameLine();

        // Roll 点插件
        var lazyLootStatus = DependencyManager.GetStatus(DependencyType.LazyLoot);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "| Roll点: ");
        ImGui.SameLine();
        ImGui.TextColored(lazyLootStatus.IsAvailable ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f) : new Vector4(0.9f, 0.6f, 0.2f, 1.0f),
            lazyLootStatus.IsAvailable ? "LazyLoot OK" : "LazyLoot 未安装");
    }

    private void DrawLogPanel()
    {
        ImGui.Text("日志输出");
        ImGui.Spacing();

        var logHeight = ImGui.GetContentRegionAvail().Y;
        if (ImGui.BeginChild("LogScroll", new Vector2(-1, logHeight), false, ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            foreach (var line in _logLines)
            {
                ImGui.TextWrapped(line);
            }

            // 自动滚动到底部
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            {
                ImGui.SetScrollHereY(1.0f);
            }
        }
        ImGui.EndChild();
    }
}
