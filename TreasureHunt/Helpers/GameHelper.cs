using System;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TreasureHunt.Helpers;

/// <summary>
/// 游戏状态辅助方法（参考 vsatisfy 的 Game.cs）
/// 提供精确的游戏状态检测和 UI 交互
/// </summary>
public static unsafe class GameHelper
{
    /// <summary>
    /// 检查区域是否已加载完成
    /// 参考 vsatisfy: GameMain.Instance()->TerritoryLoadState == 2
    /// 比 BetweenAreas 条件标志更精确
    /// </summary>
    public static bool IsTerritoryLoaded()
    {
        return GameMain.Instance()->TerritoryLoadState == 2;
    }

    /// <summary>
    /// 检查玩家是否正在施放传送
    /// 参考 vsatisfy: 检查 ActionId == 5 (Teleport)
    /// </summary>
    public static bool IsCastingTeleport()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;

        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
        var info = chara->GetCastInfo();
        return info is not null && info->IsCasting &&
               (ActionType)info->ActionType == ActionType.Action &&
               info->ActionId == 5;
    }

    /// <summary>
    /// 检查玩家是否可交互（非过场动画/加载中）
    /// 参考 vsatisfy: LocalPlayer?.IsTargetable
    /// </summary>
    public static bool IsInteractable()
    {
        return Plugin.ObjectTable.LocalPlayer?.IsTargetable ?? false;
    }

    /// <summary>
    /// 推进 NPC 对话框
    /// 参考 vsatisfy: 通过模拟鼠标点击事件推进 Talk addon
    /// 用于交互 NPC/对象后需要推进对话的场景
    /// </summary>
    public static void ProgressTalk()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("Talk");
        if (addon != null && addon->IsVisible && addon->IsReady)
        {
            var evt = new AtkEvent()
            {
                Listener = &addon->AtkEventListener,
                Target = &AtkStage.Instance()->AtkEventTarget
            };
            var data = new AtkEventData();
            addon->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
        }
    }

    /// <summary>
    /// 检查 Talk 对话框是否打开
    /// </summary>
    public static bool IsTalkOpen()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("Talk");
        return addon != null && addon->IsVisible && addon->IsReady;
    }

    /// <summary>
    /// 持续推进对话直到关闭
    /// </summary>
    public static void AdvanceTalkUntilClosed(int maxAttempts = 20)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            if (!IsTalkOpen()) return;
            ProgressTalk();
            System.Threading.Thread.Sleep(100);
        }
    }

    /// <summary>
    /// 检查 SelectString 对话框是否打开
    /// 参考 NightmareXIV SelectString 插件和 vsatisfy Game.cs
    /// 用于交互 NPC/对象后出现的选择菜单
    /// </summary>
    public static bool IsSelectStringOpen()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("SelectString");
        return addon != null && addon->IsVisible && addon->IsReady;
    }

    /// <summary>
    /// 选择 SelectString 对话框中的指定选项
    /// 参考 vsatisfy: FireCallback 方式选择选项
    /// </summary>
    public static void SelectStringOption(int index)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("SelectString");
        if (addon != null && addon->IsReady)
        {
            AtkValue val = default;
            val.SetInt(index);
            addon->FireCallback(1, &val, true);
        }
    }

    /// <summary>
    /// 等待 SelectString 出现并选择指定选项
    /// 参考 AutoDuty: 等待对话框出现后自动选择
    /// </summary>
    public static bool WaitForSelectStringAndSelect(int index, int timeoutMs = 5000)
    {
        var startTime = DateTime.Now;
        while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
        {
            if (IsSelectStringOpen())
            {
                System.Threading.Thread.Sleep(200);
                SelectStringOption(index);
                return true;
            }
            System.Threading.Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// 检查是否在过场动画中
    /// 参考 TextAdvance: 检测过场动画状态
    /// </summary>
    public static bool IsCutsceneActive()
    {
        var cond = Plugin.Condition;
        return cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent] ||
               cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene];
    }

    /// <summary>
    /// 获取聚焦的 UI 插件 ID
    /// 参考 vsatisfy: GetFocusedAddonByID
    /// </summary>
    public static AtkUnitBase* GetFocusedAddonByID(uint id)
    {
        var unitManager = &AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager.FocusedUnitsList;
        for (int j = 0; j < Math.Min(unitManager->Count, unitManager->Entries.Length); j++)
        {
            var unitBase = unitManager->Entries[j].Value;
            if (unitBase != null && unitBase->Id == id)
            {
                return unitBase;
            }
        }
        return null;
    }
}

