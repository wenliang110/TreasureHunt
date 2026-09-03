using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TreasureHunt.Helpers;

namespace TreasureHunt.Services;

public enum NavigationState
{
    Idle,
    Teleporting,
    WaitingForLoad,
    Navigating,
    AtDestination,
    Error
}

public class NavigationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public NavigationState FinalState { get; set; }
}

public class NavigationService : IDisposable
{
    private readonly Plugin _plugin;
    private CancellationTokenSource? _cts;
    private readonly AdvancedUnstuck _unstuck = new();
    private volatile bool _emergencyStop;

    public event Action<NavigationState>? StateChanged;
    public event Action<string>? OnLog;

    private NavigationState _state = NavigationState.Idle;
    public NavigationState State
    {
        get => _state;
        private set
        {
            _state = value;
            StateChanged?.Invoke(_state);
            OnLog?.Invoke($"导航状态: {value}");
        }
    }

    public NavigationService(Plugin plugin)
    {
        _plugin = plugin;
        _unstuck.OnLog += msg => OnLog?.Invoke(msg);
    }

    /// <summary>
    /// 使用 vnavmesh 导航到目标点（自动上坐骑）
    /// 注意：传送逻辑由 Orchestrator 负责，本方法只处理同区域内的步行/坐骑导航
    /// </summary>
    public async Task<NavigationResult> NavigateToAsync(Vector3 destination, string destinationName = "")
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
                return new NavigationResult { Success = false, ErrorMessage = "角色不存在" };

            // 检查是否已在目标附近
            var currentPos = player.Position;
            var distance = Vector3.Distance(currentPos, destination);

            if (distance <= _plugin.Configuration.NavigationStopDistance)
            {
                OnLog?.Invoke("已在目标点附近");
                State = NavigationState.AtDestination;
                return new NavigationResult { Success = true, FinalState = State };
            }

            OnLog?.Invoke($"距离目标 {distance:F1}m，开始导航...");

            // 导航前自动上坐骑（如果距离较远且不在坐骑上）
            if (distance > 50.0f)
            {
                await EnsureMounted(token);
            }

            // 等待 vnavmesh 就绪（传送后 navmesh 可能需要时间加载）
            var vnavWaitStart = DateTime.Now;
            while (!VnavmeshHelper.IsAvailable() && (DateTime.Now - vnavWaitStart).TotalSeconds < 5)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(500, token);
            }

            if (!VnavmeshHelper.IsAvailable())
            {
                OnLog?.Invoke("警告: vnavmesh 不可用，尝试继续导航...");
            }

            // 使用 vnavmesh 导航到具体位置
            State = NavigationState.Navigating;
            if (!await NavigateWithVnavmesh(destination, token))
                return new NavigationResult { Success = false, ErrorMessage = "vnavmesh 导航失败" };

            State = NavigationState.AtDestination;
            OnLog?.Invoke($"已到达目标点: {destinationName}");
            return new NavigationResult { Success = true, FinalState = State };
        }
        catch (OperationCanceledException)
        {
            VnavmeshHelper.Stop();
            return new NavigationResult { Success = false, ErrorMessage = "已取消" };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"导航异常: {ex.Message}");
            return new NavigationResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            State = NavigationState.Idle;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 仅传送（不导航到具体位置）
    /// </summary>
    public async Task<NavigationResult> TeleportOnlyAsync(uint aetheryteId)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            State = NavigationState.Teleporting;
            OnLog?.Invoke($"传送到水晶 ID: {aetheryteId}");

            if (!AetheryteHelper.TeleportToAetheryte(aetheryteId, OnLog))
                return new NavigationResult { Success = false, ErrorMessage = "传送指令失败" };

            State = NavigationState.WaitingForLoad;
            await WaitForAreaLoad(token);
            await Task.Delay(1500, token);

            OnLog?.Invoke("传送完成");
            return new NavigationResult { Success = true, FinalState = NavigationState.AtDestination };
        }
        catch (OperationCanceledException)
        {
            return new NavigationResult { Success = false, ErrorMessage = "已取消" };
        }
        catch (Exception ex)
        {
            return new NavigationResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            State = NavigationState.Idle;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 尝试传送到离目标点最近的已解锁水晶
    /// </summary>
    private async Task<bool> TeleportToNearestAetheryte(Vector3 destination, uint targetTerritoryId, CancellationToken token)
    {
        State = NavigationState.Teleporting;

        // 从 MapLocationDatabase 或配置中获取目标区域的推荐水晶
        // 这里我们尝试通过领土 ID 查找合适的水晶
        var nearestAetheryteId = FindBestAetheryteForTerritory(targetTerritoryId);
        if (nearestAetheryteId == 0)
        {
            OnLog?.Invoke("未找到目标区域的可用水晶，跳过传送");
            return false;
        }

        OnLog?.Invoke($"选择水晶 ID: {nearestAetheryteId} ({AetheryteHelper.GetAetheryteName(nearestAetheryteId)})");

        // 检查传送费用
        var cost = AetheryteHelper.GetTeleportCost(nearestAetheryteId);
        OnLog?.Invoke($"传送费用: {cost} gil");

        if (!AetheryteHelper.TeleportToAetheryte(nearestAetheryteId, OnLog))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 根据领土 ID 查找最合适的传送水晶
    /// </summary>
    private uint FindBestAetheryteForTerritory(uint territoryId)
    {
        try
        {
            // 从已解锁水晶中查找同领土的（最可靠）
            var unlocked = AetheryteHelper.GetUnlockedAetherytes();
            foreach (var (id, terrId, gilCost) in unlocked)
            {
                if (terrId == territoryId)
                {
                    return id;
                }
            }

            // 如果没找到，尝试从 Aetheryte Excel 表中搜索（返回第一个同领土的已解锁水晶）
            var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (aetheryteSheet != null)
            {
                foreach (var row in aetheryteSheet)
                {
                    if (!row.IsAetheryte) continue;
                    // Territory 是 RowRef，通过 Value.RowId 获取领土 ID
                    try
                    {
                        if (row.Territory.Value.RowId == territoryId)
                        {
                            if (AetheryteHelper.IsAetheryteUnlocked(row.RowId))
                            {
                                return row.RowId;
                            }
                        }
                    }
                    catch { continue; }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"查找目标区域水晶失败: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// 根据目标位置猜测目标领土 ID
    /// 这是一个简化实现，实际使用中应该从藏宝图数据中获取领土 ID
    /// </summary>
    private uint GuessTargetTerritory(Vector3 destination)
    {
        // 默认返回当前领土，表示同区域导航
        return Plugin.ClientState.TerritoryType;
    }

    /// <summary>
    /// 确保玩家在坐骑上（导航远距离时自动上坐骑）
    /// 如果已经在坐骑上或在飞行中则直接返回
    /// 参考 GatherBuddy: 使用 GetActionStatus 检查后 UseAction
    /// </summary>
    private async Task EnsureMounted(CancellationToken token)
    {
        try
        {
            if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight])
            {
                OnLog?.Invoke("已在坐骑上");
                return;
            }

            // 检查是否在战斗中或其他不能上坐骑的状态
            if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Jumping] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Jumping61])
            {
                OnLog?.Invoke("当前状态无法上坐骑，跳过");
                return;
            }

            // 先检查坐骑技能是否可用
            var (statusOk, statusValue) = CheckMountActionStatus();
            if (!statusOk)
            {
                OnLog?.Invoke($"坐骑技能不可用 (status={statusValue})，尝试继续步行导航");
                return;
            }

            OnLog?.Invoke("正在上坐骑...");

            // 尝试上坐骑（最多重试 3 次）
            var mountSuccess = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();

                // 重新检查状态
                if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
                {
                    mountSuccess = true;
                    break;
                }

                OnLog?.Invoke($"上坐骑尝试 {attempt + 1}/3");
                ExecuteMountAction();

                // 等待上坐骑完成（每次最多 5 秒）
                var waitStart = DateTime.Now;
                while ((DateTime.Now - waitStart).TotalSeconds < 5)
                {
                    token.ThrowIfCancellationRequested();
                    if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
                    {
                        mountSuccess = true;
                        OnLog?.Invoke("已上坐骑");
                        break;
                    }
                    await Task.Delay(200, token);
                }

                if (mountSuccess) break;

                // 等待 1 秒后重试
                if (attempt < 2)
                {
                    OnLog?.Invoke("上坐骑未成功，等待后重试...");
                    await Task.Delay(1000, token);
                }
            }

            if (!mountSuccess)
            {
                OnLog?.Invoke("上坐骑失败（3次重试均未成功），继续步行导航");
            }
            else
            {
                // 额外等待 1 秒让坐骑稳定
                await Task.Delay(1000, token);
                OnLog?.Invoke("坐骑已稳定，开始导航");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OnLog?.Invoke($"上坐骑异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查坐骑技能状态（unsafe 操作隔离）
    /// </summary>
    private unsafe (bool ok, uint status) CheckMountActionStatus()
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (actionManager == null)
            return (false, 9999);

        var actionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction;
        var actionId = 23u;
        var status = actionManager->GetActionStatus(actionType, actionId);
        return (status == 0, status);
    }

    /// <summary>
    /// 执行上坐骑动作（unsafe 操作隔离）
    /// </summary>
    private unsafe void ExecuteMountAction()
    {
        var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (actionManager == null) return;

        actionManager->UseAction(
            FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 23);
    }

    /// <summary>
    /// 等待区域加载完成
    /// 参考 Untarnished Heart: WaitForAreaReadyAsync 和 GatherBuddy: 传送等待模式
    /// </summary>
    private async Task WaitForAreaLoad(CancellationToken token)
    {
        OnLog?.Invoke("等待区域加载...");

        // 使用通用助手等待传送完成
        var success = await AsyncHelper.WaitForTeleportCompleteAsync(token, 45000);
        if (!success)
        {
            // 传送可能已经在目标区域，检查玩家是否已加载
            if (Plugin.ObjectTable.LocalPlayer != null)
            {
                OnLog?.Invoke("已在目标区域");
                return;
            }
            OnLog?.Invoke("等待区域加载超时");
            return;
        }

        OnLog?.Invoke("区域加载完成");
    }

    /// <summary>
    /// 使用 vnavmesh 导航到目标位置 - 使用异步 MoveToAsync 避免死锁
    /// </summary>
    private async Task<bool> NavigateWithVnavmesh(Vector3 destination, CancellationToken token)
    {
        if (!VnavmeshHelper.IsAvailable())
        {
            OnLog?.Invoke("vnavmesh 不可用，请确保已安装 vnavmesh 插件");
            return false;
        }

        OnLog?.Invoke($"开始导航到 ({destination.X:F1}, {destination.Y:F1}, {destination.Z:F1})");

        var stopDistance = _plugin.Configuration.NavigationStopDistance;
        var timeout = TimeSpan.FromMinutes(5);
        var startTime = DateTime.Now;
        var retryCount = 0;
        const int maxRetries = 3;
        _unstuck.Reset();
        _emergencyStop = false;

        // 使用 MoveToAsync 进行异步导航
        while ((DateTime.Now - startTime) < timeout && !_emergencyStop)
        {
            token.ThrowIfCancellationRequested();
            if (_emergencyStop) break;

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                await Task.Delay(500, token);
                continue;
            }

            // 检查是否到达目的地
            var distToDest = Vector3.Distance(player.Position, destination);
            if (distToDest <= stopDistance)
            {
                VnavmeshHelper.Stop();
                OnLog?.Invoke("已到达目的地");
                return true;
            }

            // 发起异步导航
            var navSuccess = await VnavmeshHelper.MoveToAsync(
                destination, tolerance: stopDistance, fly: false,
                timeoutMs: 10000, token: token);

            if (navSuccess)
            {
                VnavmeshHelper.Stop();
                OnLog?.Invoke("已到达目的地");
                return true;
            }

            // 导航未到达，检查卡住并重试
            var isPathing = VnavmeshHelper.IsPlayerMoving();
            _unstuck.Check(isPathing);

            // 如果 vnavmesh 已停止且未到达，重新寻路
            if (!VnavmeshHelper.IsPlayerMoving())
            {
                if (retryCount < maxRetries)
                {
                    retryCount++;
                    OnLog?.Invoke($"导航中断 {retryCount}/{maxRetries}，重新寻路...");
                    await Task.Delay(800, token);
                    _unstuck.Reset();
                }
                else
                {
                    OnLog?.Invoke("多次重试后仍无法到达，导航失败");
                    break;
                }
            }

            await Task.Delay(500, token);
        }

        OnLog?.Invoke("导航超时");
        VnavmeshHelper.Stop();
        return false;
    }

    /// <summary>
    /// 瞬移到指定位置（用于 TP 钱袋子等特殊场景）
    /// 注意：普通瞬移在大多数情况下无效，此方法主要用于洞内特殊机制
    /// </summary>
    public unsafe bool TeleportToPosition(Vector3 position)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;

            // 获取游戏内部的 Player 对象指针
            var playerObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            if (playerObj == null) return false;

            // 修改位置
            playerObj->SetPosition(position.X, position.Y, position.Z);

            OnLog?.Invoke($"瞬移到 ({position.X:F1}, {position.Y:F1}, {position.Z:F1})");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"瞬移失败: {ex.Message}");
            return false;
        }
    }

    public void Cancel()
    {
        _emergencyStop = true;
        VnavmeshHelper.Stop();
        _cts?.Cancel();
        _unstuck.Dispose();
    }

    public void Dispose()
    {
        _emergencyStop = true;
        VnavmeshHelper.Stop();
        _cts?.Cancel();
        _unstuck.Dispose();
    }
}
