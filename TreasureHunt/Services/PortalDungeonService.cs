using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TreasureHunt.Helpers;
using TreasureHunt.Models;

namespace TreasureHunt.Services;

public class PortalDungeonResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool ReachedBonusRoom { get; set; }
    public int FloorsCleared { get; set; }
    public bool WasKickedOut { get; set; }
}

public class PortalDungeonService : IDisposable
{
    private readonly Plugin _plugin;
    private CancellationTokenSource? _cts;
    private readonly PortalDungeonState _state;

    public event Action<PortalDungeonPhase>? StateChanged;
    public event Action<string>? OnLog;

    private const int MaxFloors = 6;
    private const int RollWaitTimeoutSec = 60;

    // 进入洞前的领土 ID（用于检测是否被踢出）
    private uint _preDungeonTerritoryId = 0;

    public PortalDungeonService(Plugin plugin)
    {
        _plugin = plugin;
        _state = new PortalDungeonState();
    }

    /// <summary>
    /// 完整的洞内流程
    /// 流程: 进入传送门 → 等待加载 → 每层循环(找机关→交互→等战斗→开箱→roll点→检查奖励房→下一层) → 结束
    /// </summary>
    public async Task<PortalDungeonResult> ExecutePortalDungeonFlow()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var floorsCleared = 0;
            var reachedBonusRoom = false;
            var wasKickedOut = false;

            // 记录进入洞前的领土 ID
            _preDungeonTerritoryId = Plugin.ClientState.TerritoryType;

            // 步骤1: 进入传送门
            _state.SetPhase(PortalDungeonPhase.EnteringPortal);
            if (!await EnterPortal(token))
                return new PortalDungeonResult { Success = false, ErrorMessage = "进入传送门失败" };

            // 步骤2: 等待洞内区域加载完成
            _state.SetPhase(PortalDungeonPhase.InDungeon);
            OnLog?.Invoke("等待洞内区域加载...");
            var areaLoaded = await AsyncHelper.WaitForAreaChangeAsync(_preDungeonTerritoryId, token, 30000);
            if (!areaLoaded)
            {
                // 可能已经在洞内了（传送门直接传送）
                OnLog?.Invoke("区域加载等待超时，检查是否已在洞内...");
                if (!GameHelper.IsTerritoryLoaded() || !GameHelper.IsInteractable())
                {
                    return new PortalDungeonResult { Success = false, ErrorMessage = "洞内区域加载失败" };
                }
            }
            await Task.Delay(2000, token); // 额外等待 2 秒让场景稳定

            OnLog?.Invoke($"已进入洞内 (领土: {Plugin.ClientState.TerritoryType})");

            // 步骤3: 洞内循环
            for (int floor = 1; floor <= MaxFloors; floor++)
            {
                _state.CurrentFloor = floor;
                token.ThrowIfCancellationRequested();

                // 检查是否被踢出（领土变回洞外）
                if (IsKickedOut())
                {
                    _state.SetPhase(PortalDungeonPhase.ExitingDungeon);
                    wasKickedOut = true;
                    OnLog?.Invoke($"第 {floor} 层: 检测到被踢出");
                    break;
                }

                // 3a: 查找并交互洞内机关
                _state.SetPhase(PortalDungeonPhase.InteractingWithObject);
                OnLog?.Invoke($"第 {floor} 层: 查找机关...");
                if (!await InteractWithDungeonObject(token))
                {
                    _state.SetPhase(PortalDungeonPhase.ExitingDungeon);
                    wasKickedOut = true;
                    OnLog?.Invoke($"第 {floor} 层: 未找到机关，可能被踢出");
                    break;
                }

                // 3b: 等待战斗
                _state.SetPhase(PortalDungeonPhase.WaitingForCombat);
                OnLog?.Invoke($"第 {floor} 层: 等待战斗");
                await WaitForDungeonCombat(token);

                // 3c: 开箱
                _state.SetPhase(PortalDungeonPhase.OpeningChest);
                OnLog?.Invoke($"第 {floor} 层: 开箱");
                await OpenDungeonChest(token);

                // 3d: 等待 roll 点
                _state.SetPhase(PortalDungeonPhase.WaitingForRoll);
                OnLog?.Invoke($"第 {floor} 层: 等待 roll 点");
                await WaitForRollComplete(token);

                floorsCleared++;

                // 3e: 检查是否进入奖励房（特殊梦境 - 宝箱图案3连）
                if (await IsBonusRoomTriggered(token))
                {
                    OnLog?.Invoke("检测到奖励房触发！");
                    reachedBonusRoom = true;
                    _state.SetPhase(PortalDungeonPhase.InBonusRoom);
                    break;
                }

                // 3f: 检查是否被踢出
                if (IsKickedOut())
                {
                    _state.SetPhase(PortalDungeonPhase.ExitingDungeon);
                    wasKickedOut = true;
                    OnLog?.Invoke($"第 {floor} 层: 开箱后被踢出");
                    break;
                }

                // 3g: 交互进入下一层
                _state.SetPhase(PortalDungeonPhase.MovingToNextFloor);
                OnLog?.Invoke($"第 {floor} 层: 进入下一层");
                if (!await MoveToNextFloor(token))
                {
                    _state.SetPhase(PortalDungeonPhase.ExitingDungeon);
                    wasKickedOut = true;
                    OnLog?.Invoke($"第 {floor} 层: 无法进入下一层，可能被踢出");
                    break;
                }
            }

            _state.SetPhase(PortalDungeonPhase.ExitingDungeon);
            OnLog?.Invoke($"洞内流程结束，清理 {floorsCleared} 层");

            return new PortalDungeonResult
            {
                Success = true,
                FloorsCleared = floorsCleared,
                ReachedBonusRoom = reachedBonusRoom,
                WasKickedOut = wasKickedOut
            };
        }
        catch (OperationCanceledException)
        {
            return new PortalDungeonResult { Success = false, ErrorMessage = "已取消" };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"洞内流程异常: {ex.Message}");
            return new PortalDungeonResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _state.SetPhase(PortalDungeonPhase.Idle);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 进入传送门
    /// 流程: 移动到传送门 → 交互 → 等待确认对话框 → 确认进入
    /// </summary>
    private async Task<bool> EnterPortal(CancellationToken token)
    {
        var portal = GameObjectHelper.GetPortalTransferCircle();
        if (portal == null)
        {
            OnLog?.Invoke("未找到传送门");
            return false;
        }

        OnLog?.Invoke($"找到传送门: {portal.Name} at ({portal.Position.X:F1}, {portal.Position.Z:F1})");

        // 移动到传送门附近
        if (!GameObjectHelper.IsInInteractRange(portal, 3.0f))
        {
            OnLog?.Invoke("移动到传送门...");
            if (VnavmeshHelper.IsAvailable())
            {
                var reached = await VnavmeshHelper.MoveToAsync(portal.Position, tolerance: 2.5f, fly: false, timeoutMs: 15000, token: token);
                if (!reached)
                {
                    OnLog?.Invoke("无法到达传送门位置");
                    return false;
                }
            }
            else
            {
                OnLog?.Invoke("vnavmesh 不可用，无法移动到传送门");
                return false;
            }
        }

        // 交互传送门并处理后续对话框（可能有 SelectYesno 确认进入）
        OnLog?.Invoke("交互传送门...");
        var interacted = await GameObjectHelper.InteractWithObjectAsync(portal, token,
            selectStringIndex: 0, selectIconStringIndex: 0, autoConfirmYesno: true,
            totalTimeoutMs: 15000);

        if (!interacted)
        {
            OnLog?.Invoke("传送门交互失败");
            return false;
        }

        // 等待传送开始（BetweenAreas 或领土变化）
        OnLog?.Invoke("等待进入洞...");
        var enterStarted = await AsyncHelper.WaitUntilAsync(
            () => Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
                  Plugin.ClientState.TerritoryType != _preDungeonTerritoryId,
            "等待洞加载",
            token,
            15000,
            200);

        if (!enterStarted)
        {
            OnLog?.Invoke("未检测到进入洞的加载过程，可能交互失败");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 交互洞内机关
    /// 使用名称搜索（不依赖硬编码 DataId）
    /// </summary>
    private async Task<bool> InteractWithDungeonObject(CancellationToken token)
    {
        var obj = GameObjectHelper.GetDungeonObject();
        if (obj == null)
        {
            // 等待一下再试（机关可能还在加载）
            await Task.Delay(2000, token);
            obj = GameObjectHelper.GetDungeonObject();
            if (obj == null)
            {
                OnLog?.Invoke("未找到洞内机关");
                return false;
            }
        }

        OnLog?.Invoke($"找到机关: {obj.Name} at ({obj.Position.X:F1}, {obj.Position.Z:F1})");

        // 移动到机关附近
        if (!GameObjectHelper.IsInInteractRange(obj, 3.0f))
        {
            if (VnavmeshHelper.IsAvailable())
            {
                await VnavmeshHelper.MoveToAsync(obj.Position, tolerance: 2.5f, fly: false, timeoutMs: 15000, token: token);
            }
        }

        // 交互并处理对话框（机关交互后可能弹出 SelectString/SelectYesno）
        OnLog?.Invoke("交互机关...");
        var interacted = await GameObjectHelper.InteractWithObjectAsync(obj, token,
            selectStringIndex: 0, selectIconStringIndex: 0, autoConfirmYesno: true,
            totalTimeoutMs: 15000);

        if (interacted)
        {
            await Task.Delay(_plugin.Configuration.InteractionDelay, token);
        }

        return interacted;
    }

    private async Task WaitForDungeonCombat(CancellationToken token)
    {
        OnLog?.Invoke("等待洞内战斗...");

        // 等待进入战斗
        var combatStarted = await AsyncHelper.WaitUntilAsync(
            () => Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat],
            "洞内战斗开始",
            token,
            30000,
            500);

        if (combatStarted)
        {
            OnLog?.Invoke("战斗开始");
        }
        else
        {
            OnLog?.Invoke("未检测到战斗，可能已秒杀");
            return;
        }

        // 等待战斗结束
        var combatEnded = await AsyncHelper.WaitUntilAsync(
            () => !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat],
            "洞内战斗结束",
            token,
            300000,
            500);

        if (combatEnded)
        {
            OnLog?.Invoke("战斗结束");
            await Task.Delay(_plugin.Configuration.CombatWaitDelay, token);
        }
        else
        {
            OnLog?.Invoke("等待战斗超时");
        }
    }

    private async Task OpenDungeonChest(CancellationToken token)
    {
        // 查找洞内宝箱
        var chest = GameObjectHelper.GetDungeonChest();
        if (chest == null)
        {
            OnLog?.Invoke("未找到洞内宝箱，等待2秒后重试...");
            await Task.Delay(2000, token);
            chest = GameObjectHelper.GetDungeonChest();
            if (chest == null)
            {
                OnLog?.Invoke("仍未找到洞内宝箱");
                return;
            }
        }

        OnLog?.Invoke($"找到宝箱: {chest.Name} at ({chest.Position.X:F1}, {chest.Position.Z:F1})");

        // 移动到宝箱附近
        if (!GameObjectHelper.IsInInteractRange(chest, 3.0f))
        {
            if (VnavmeshHelper.IsAvailable())
            {
                await VnavmeshHelper.MoveToAsync(chest.Position, tolerance: 2.5f, fly: false, timeoutMs: 15000, token: token);
            }
        }

        // 交互开箱并处理对话框
        OnLog?.Invoke("开箱...");
        await GameObjectHelper.InteractWithObjectAsync(chest, token,
            selectStringIndex: 0, selectIconStringIndex: 0, autoConfirmYesno: true,
            totalTimeoutMs: 10000);

        await Task.Delay(_plugin.Configuration.InteractionDelay, token);
    }

    private async Task WaitForRollComplete(CancellationToken token)
    {
        OnLog?.Invoke("等待 roll 点完成...");

        // 检查 LazyLoot 是否可用
        if (LazyLootHelper.IsAvailable())
        {
            OnLog?.Invoke("检测到 LazyLoot，自动执行 Need Roll");
            LazyLootHelper.RollNeed();
        }
        else
        {
            OnLog?.Invoke("未检测到 LazyLoot，请手动 Roll 或安装 LazyLoot 插件");
        }

        // 等待 Roll 完成
        var completed = await LazyLootHelper.WaitForRollComplete(RollWaitTimeoutSec * 1000, token);
        if (completed)
        {
            OnLog?.Invoke("roll 点完成");
        }
        else
        {
            OnLog?.Invoke("等待 roll 点超时");
        }
    }

    private async Task<bool> IsBonusRoomTriggered(CancellationToken token)
    {
        await Task.Delay(1000, token);

        // 检查是否有闪亮袋子（奖励房标志）
        var bags = GameObjectHelper.GetShiningBags();
        if (bags.Count > 0)
        {
            return true;
        }

        // 检查是否有倒计时（奖励房 90 秒倒计时）
        // 通过检查 LimitTimeController 或类似机制
        // 简单方式：检查当前是否在不同领土或特定条件
        return false;
    }

    private async Task<bool> MoveToNextFloor(CancellationToken token)
    {
        // 查找下一层入口
        var nextFloor = GameObjectHelper.GetNextFloorEntrance();
        if (nextFloor == null)
        {
            await Task.Delay(2000, token);
            nextFloor = GameObjectHelper.GetNextFloorEntrance();
            if (nextFloor == null)
            {
                OnLog?.Invoke("未找到下一层入口");
                return false;
            }
        }

        OnLog?.Invoke($"找到下一层入口: {nextFloor.Name} at ({nextFloor.Position.X:F1}, {nextFloor.Position.Z:F1})");

        // 移动到入口附近
        if (!GameObjectHelper.IsInInteractRange(nextFloor, 3.0f))
        {
            if (VnavmeshHelper.IsAvailable())
            {
                await VnavmeshHelper.MoveToAsync(nextFloor.Position, tolerance: 2.5f, fly: false, timeoutMs: 15000, token: token);
            }
        }

        // 交互并处理对话框（进入下一层可能有确认对话框）
        OnLog?.Invoke("进入下一层...");
        var interacted = await GameObjectHelper.InteractWithObjectAsync(nextFloor, token,
            selectStringIndex: 0, selectIconStringIndex: 0, autoConfirmYesno: true,
            totalTimeoutMs: 15000);

        if (!interacted)
            return false;

        // 等待楼层加载
        var preFloorTerritory = Plugin.ClientState.TerritoryType;
        var loaded = await AsyncHelper.WaitForAreaChangeAsync(preFloorTerritory, token, 20000);
        if (loaded)
        {
            await Task.Delay(1000, token);
        }

        return true;
    }

    /// <summary>
    /// 检测是否被踢出（领土回到进入洞前的区域）
    /// </summary>
    public bool IsKickedOut()
    {
        // 如果领土回到了进入洞前的区域，说明被踢出了
        if (_preDungeonTerritoryId != 0 && Plugin.ClientState.TerritoryType == _preDungeonTerritoryId)
        {
            // 排除刚开始还没进入洞的情况
            if (_state.Phase == PortalDungeonPhase.EnteringPortal || _state.Phase == PortalDungeonPhase.Idle)
                return false;
            return true;
        }
        return false;
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Cancel();
    }
}
