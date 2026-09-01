using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;
using TreasureHunt.Services;
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
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Gargantuaskin (G18) → Vault Oneiron");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "| 国服 CN");
    }

    private void DrawFeatureToggles()
    {
        ImGui.Text("功能开关");
        ImGui.Spacing();

        var config = _plugin.Configuration;

        DrawCheckbox("1. 不选中他人宝箱怪", ref config.AvoidOthersTreasureMonsters);
        DrawCheckbox("2. 解读后标记位置", ref config.EnableMarkLocation);
        DrawCheckbox("3. 一键买图解读", ref config.EnableOneClickBuyDecipher);
        DrawCheckbox("4. 自动传送", ref config.EnableAutoTeleport);
        DrawCheckbox("5. TP 钱袋子自动收集", ref config.EnableMoneyBagCollection);
        ImGui.Indent();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), $"已收集: {_plugin.MoneyBagService.BagsCollected}/100 | 剩余: {_plugin.MoneyBagService.RemainingTime}s");
        ImGui.Unindent();

        ImGui.Spacing();
        DrawCheckbox("全自动模式 (买图→解读→传送→导航→挖掘→战斗→开箱→进洞→钱袋)", ref config.EnableFullAutoMode);
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
        var buttonSize = new Vector2(-1, 30);

        if (!isRunning)
        {
            if (ImGui.Button("启动全自动挖宝", buttonSize))
            {
                _logLines.Clear();
                _ = _plugin.Orchestrator.RunFullAutoAsync();
            }

            ImGui.Spacing();

            if (_plugin.Configuration.EnableOneClickBuyDecipher)
            {
                if (ImGui.Button("一键买图+解读+传送", buttonSize))
                {
                    _logLines.Clear();
                    _ = _plugin.Orchestrator.OneClickBuyAndDecipherAsync();
                }
                ImGui.Spacing();
            }

            // 单独功能按钮
            ImGui.Columns(2, null, false);
            if (ImGui.Button("单独: 买图", new Vector2(-1, 25)))
            {
                _ = _plugin.MapPurchaseService.PurchaseMapAsync();
            }
            ImGui.NextColumn();
            if (ImGui.Button("单独: 解读", new Vector2(-1, 25)))
            {
                _ = _plugin.MapDecipherService.DecipherMapAsync();
            }
            ImGui.NextColumn();
            if (ImGui.Button("单独: 钱袋子", new Vector2(-1, 25)))
            {
                _ = _plugin.MoneyBagService.StartCollectionAsync();
            }
            ImGui.NextColumn();
            if (ImGui.Button("打开设置", new Vector2(-1, 25)))
            {
                _plugin.ToggleConfigUi();
            }
            ImGui.Columns(1);
        }
        else
        {
            if (ImGui.Button("取消运行", buttonSize))
            {
                _plugin.Orchestrator.Cancel();
            }
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
        var vnav = TreasureHunt.Helpers.VnavmeshHelper.IsAvailable();
        ImGui.TextColored(vnav ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f) : new Vector4(0.9f, 0.2f, 0.2f, 1.0f),
            vnav ? "vnavmesh OK" : "vnavmesh 不可用");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "| 战斗插件: RSR/BossMod (手动)");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "| roll点: Kapture (手动)");
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
