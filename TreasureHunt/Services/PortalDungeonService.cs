using System;
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

    // 洞内关键对象 DataId
    // 需要根据实际游戏版本调试确认
    private const uint DungeonObjectDataId = 0; // 洞内交互对象
    private const uint NextFloorButtonDataId = 0; // 下一层按钮
    private const uint TreasureChestDataId = 0; // 洞内宝箱

    private const int MaxFloors = 6;
    private const int RollWaitTimeoutSec = 60;

    public PortalDungeonService(Plugin plugin)
    {
        _plugin = plugin;
        _state = new PortalDungeonState();
    }

    /// <summary>
    /// 完整的洞内流程
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

            // 步骤1: 进入传送门
            _state.SetPhase(PortalDungeonPhase.EnteringPortal);
            if (!await EnterPortal(token))
                return new PortalDungeonResult { Success = false, ErrorMessage = "进入传送门失败" };

            // 步骤2: 洞内循环
            for (int floor = 1; floor <= MaxFloors; floor++)
            {
                _state.CurrentFloor = floor;
                token.ThrowIfCancellationRequested();

                // 2a: 交互洞内机关
                _state.SetPhase(PortalDungeonPhase.InteractingWithObject);
                OnLog?.Invoke($"第 {floor} 层: 交互机关");
                if (!await _plugin.TreasureCofferService.InteractWithDungeonObject(DungeonObjectDataId, token))
                {
                    // 可能是被踢出了
                    _state.SetPhase(PortalDungeonPhase.ExitingDungeon);
                    wasKickedOut = true;
                    OnLog?.Invoke($"第 {floor} 层: 被踢出");
                    break;
                }

                // 2b: 等待战斗
                _state.SetPhase(PortalDungeonPhase.WaitingForCombat);
                OnLog?.Invoke($"第 {floor} 层: 等待战斗");
                await WaitForDungeonCombat(token);

                // 2c: 开箱
                _state.SetPhase(PortalDungeonPhase.OpeningChest);
                OnLog?.Invoke($"第 {floor} 层: 开箱");
                await OpenDungeonChest(token);

                // 2d: 等待 roll 点
                _state.SetPhase(PortalDungeonPhase.WaitingForRoll);
                OnLog?.Invoke($"第 {floor} 层: 等待 roll 点");
                await WaitForRollComplete(token);

                floorsCleared++;

                // 2e: 检查是否进入奖励房（特殊梦境 - 宝箱图案3连）
                if (await IsBonusRoomTriggered(token))
                {
                    OnLog?.Invoke("检测到奖励房触发！");
                    reachedBonusRoom = true;
                    _state.SetPhase(PortalDungeonPhase.InBonusRoom);

                    // 交给 MoneyBagService 处理
                    // MoneyBagService 会在外部被调用
                    break;
                }

                // 2f: 交互进入下一层
                _state.SetPhase(PortalDungeonPhase.MovingToNextFloor);
                OnLog?.Invoke($"第 {floor} 层: 进入下一层");
                if (!await MoveToNextFloor(token))
                {
                    // 可能是被踢出了
                    _state.SetPhase(PortalDungeonPhase.ExitingDungeon);
                    wasKickedOut = true;
                    OnLog?.Invoke($"第 {floor} 层: 被踢出");
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

    private async Task<bool> EnterPortal(CancellationToken token)
    {
        // 查找传送门（転送魔紋）
        var portal = GameObjectHelper.GetPortalTransferCircle();
        if (portal == null)
        {
            OnLog?.Invoke("未找到传送门");
            return false;
        }

        OnLog?.Invoke("进入传送门");

        // 移动到传送门并交互
        if (!GameObjectHelper.IsInInteractRange(portal, 3.0f))
        {
            VnavmeshHelper.PathfindAndMoveTo(portal.Position);
            var timeout = TimeSpan.FromSeconds(30);
            var start = DateTime.Now;
            while (!GameObjectHelper.IsInInteractRange(portal, 3.0f) && (DateTime.Now - start) < timeout)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }
            VnavmeshHelper.StopAutoRunning();
        }

        GameObjectHelper.InteractWithObject(portal);
        await Task.Delay(2000, token); // 等待进入洞

        OnLog?.Invoke("已进入宝物库");
        return true;
    }

    private async Task WaitForDungeonCombat(CancellationToken token)
    {
        var timeout = TimeSpan.FromMinutes(5);
        var startTime = DateTime.Now;
        var inCombat = false;

        while ((DateTime.Now - startTime) < timeout)
        {
            token.ThrowIfCancellationRequested();

            var inCombatNow = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
            if (inCombatNow && !inCombat)
            {
                inCombat = true;
                OnLog?.Invoke("战斗开始");
            }
            else if (!inCombatNow && inCombat)
            {
                OnLog?.Invoke("战斗结束");
                await Task.Delay(_plugin.Configuration.CombatWaitDelay, token);
                return;
            }

            await Task.Delay(500, token);
        }

        OnLog?.Invoke("等待战斗超时");
    }

    private async Task OpenDungeonChest(CancellationToken token)
    {
        // 查找洞内宝箱
        var chest = GameObjectHelper.FindNearestObjectByDataId(TreasureChestDataId);
        if (chest == null)
        {
            chest = GameObjectHelper.GetTreasureCoffer();
        }

        if (chest == null)
        {
            OnLog?.Invoke("未找到洞内宝箱");
            return;
        }

        if (!GameObjectHelper.IsInInteractRange(chest, 3.0f))
        {
            VnavmeshHelper.PathfindAndMoveTo(chest.Position);
            var timeout = TimeSpan.FromSeconds(15);
            var start = DateTime.Now;
            while (!GameObjectHelper.IsInInteractRange(chest, 3.0f) && (DateTime.Now - start) < timeout)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }
            VnavmeshHelper.StopAutoRunning();
        }

        GameObjectHelper.InteractWithObject(chest);
        OnLog?.Invoke($"已交互宝箱: {chest.Name}");
        await Task.Delay(_plugin.Configuration.InteractionDelay, token);
    }

    private async Task WaitForRollComplete(CancellationToken token)
    {
        OnLog?.Invoke("等待 roll 点完成...");

        // 检查 LazyLoot 是否可用，可用的话自动触发 Need Roll
        if (LazyLootHelper.IsAvailable())
        {
            OnLog?.Invoke("检测到 LazyLoot，自动执行 Need Roll");
            var rolled = LazyLootHelper.RollNeed();
            if (rolled)
            {
                OnLog?.Invoke("已触发 LazyLoot Roll");
            }
            else
            {
                OnLog?.Invoke("LazyLoot Roll 触发失败，等待手动 Roll");
            }
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
        // 检查是否触发了特殊梦境（宝箱图案3连）
        // 这需要检测当前区域或 UI 状态
        // 特殊梦境会进入一个有90秒倒计时的场景
        await Task.Delay(500, token);

        // 检查是否有倒计时提示或进入了特殊区域
        // 可以通过检测 ObjectTable 中是否有"輝く袋"(Shining Bag)来判断
        var bags = GameObjectHelper.GetShiningBags();
        if (bags.Count > 0)
        {
            return true;
        }

        return false;
    }

    private async Task<bool> MoveToNextFloor(CancellationToken token)
    {
        // 查找下一层入口/按钮
        var nextFloor = GameObjectHelper.FindNearestObjectByDataId(NextFloorButtonDataId);
        if (nextFloor == null)
        {
            // 可能是被踢出了或已是最后一层
            OnLog?.Invoke("未找到下一层入口");
            return false;
        }

        if (!GameObjectHelper.IsInInteractRange(nextFloor, 3.0f))
        {
            VnavmeshHelper.PathfindAndMoveTo(nextFloor.Position);
            var timeout = TimeSpan.FromSeconds(15);
            var start = DateTime.Now;
            while (!GameObjectHelper.IsInInteractRange(nextFloor, 3.0f) && (DateTime.Now - start) < timeout)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }
            VnavmeshHelper.StopAutoRunning();
        }

        GameObjectHelper.InteractWithObject(nextFloor);
        OnLog?.Invoke("进入下一层");
        await Task.Delay(2000, token);
        return true;
    }

    /// <summary>
    /// 检测是否被踢出（强制退出）
    /// </summary>
    public bool IsKickedOut()
    {
        // 通过检测当前是否在洞外来判断
        // 或者检测特定的 UI 提示
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
