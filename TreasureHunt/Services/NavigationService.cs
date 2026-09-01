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
    }

    /// <summary>
    /// 传送到指定位置最近的晶石，然后使用 vnavmesh 导航到目标点
    /// </summary>
    public async Task<NavigationResult> NavigateToAsync(Vector3 destination, string destinationName = "")
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var player = _plugin.ClientState.LocalPlayer;
            if (player == null)
                return new NavigationResult { Success = false, ErrorMessage = "角色不存在" };

            // 检查是否已在同一区域
            var currentPos = player.Position;
            var distance = Vector3.Distance(currentPos, destination);

            if (distance <= _plugin.Configuration.NavigationStopDistance)
            {
                OnLog?.Invoke("已在目标点附近");
                State = NavigationState.AtDestination;
                return new NavigationResult { Success = true, FinalState = State };
            }

            // 步骤1: 传送到最近晶石（如果启用且距离较远）
            if (_plugin.Configuration.EnableAutoTeleport && distance > 100.0f)
            {
                if (!await TeleportToNearestAetheryte(destination, token))
                    return new NavigationResult { Success = false, ErrorMessage = "传送失败" };

                State = NavigationState.WaitingForLoad;
                await WaitForAreaLoad(token);

                await Task.Delay(1000, token); // 等待地图加载稳定
            }

            // 步骤2: 使用 vnavmesh 导航到具体位置
            State = NavigationState.Navigating;
            if (!await NavigateWithVnavmesh(destination, token))
                return new NavigationResult { Success = false, ErrorMessage = "vnavmesh 导航失败" };

            State = NavigationState.AtDestination;
            OnLog?.Invoke($"已到达目标点: {destinationName}");
            return new NavigationResult { Success = true, FinalState = State };
        }
        catch (OperationCanceledException)
        {
            VnavmeshHelper.StopAutoRunning();
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
            OnLog?.Invoke($"传送到晶石 ID: {aetheryteId}");

            if (!AetheryteHelper.TeleportToAetheryte(aetheryteId))
                return new NavigationResult { Success = false, ErrorMessage = "传送指令失败" };

            State = NavigationState.WaitingForLoad;
            await WaitForAreaLoad(token);
            await Task.Delay(1000, token);

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

    private async Task<bool> TeleportToNearestAetheryte(Vector3 destination, CancellationToken token)
    {
        State = NavigationState.Teleporting;

        var nearest = AetheryteHelper.GetNearestAetheryte(destination);
        if (nearest == null)
        {
            // 如果附近没有晶石，尝试直接导航
            OnLog?.Invoke("附近无晶石，直接导航");
            return true;
        }

        OnLog?.Invoke($"传送到最近晶石: {nearest.Value.name}");
        if (!AetheryteHelper.TeleportToAetheryte(nearest.Value.aetheryteId))
        {
            OnLog?.Invoke("传送失败");
            return false;
        }

        return true;
    }

    private async Task WaitForAreaLoad(CancellationToken token)
    {
        OnLog?.Invoke("等待区域加载...");
        var timeout = TimeSpan.FromSeconds(30);
        var startTime = DateTime.Now;

        // 等待传送开始
        while (!AetheryteHelper.IsTeleporting() && (DateTime.Now - startTime) < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(100, token);
        }

        // 等待传送完成
        while (AetheryteHelper.IsTeleporting() && (DateTime.Now - startTime) < timeout)
        {
            await Task.Delay(200, token);
        }

        // 等待角色完全加载
        while (_plugin.ClientState.LocalPlayer == null && (DateTime.Now - startTime) < timeout)
        {
            await Task.Delay(200, token);
        }

        OnLog?.Invoke("区域加载完成");
    }

    private async Task<bool> NavigateWithVnavmesh(Vector3 destination, CancellationToken token)
    {
        // 检查 vnavmesh 是否可用
        if (!VnavmeshHelper.IsAvailable())
        {
            OnLog?.Invoke("vnavmesh 不可用，请确保已安装 vnavmesh 插件");
            return false;
        }

        OnLog?.Invoke($"开始导航到 ({destination.X:F1}, {destination.Y:F1}, {destination.Z:F1})");
        VnavmeshHelper.PathfindAndMoveTo(destination);

        // 等待导航完成
        var timeout = TimeSpan.FromMinutes(5);
        var startTime = DateTime.Now;
        var lastCheckTime = DateTime.Now;

        while ((DateTime.Now - startTime) < timeout)
        {
            token.ThrowIfCancellationRequested();

            if (VnavmeshHelper.IsAtDestination(destination, _plugin.Configuration.NavigationStopDistance))
            {
                VnavmeshHelper.StopAutoRunning();
                OnLog?.Invoke("已到达目的地");
                return true;
            }

            if (!VnavmeshHelper.IsAutoRunning())
            {
                // 可能已到达或路径中断，重新尝试
                var elapsed = (DateTime.Now - lastCheckTime).TotalMilliseconds;
                if (elapsed > 3000)
                {
                    OnLog?.Invoke("导航中断，重新寻路");
                    VnavmeshHelper.PathfindAndMoveTo(destination);
                    lastCheckTime = DateTime.Now;
                }
            }

            await Task.Delay(500, token);
        }

        OnLog?.Invoke("导航超时");
        VnavmeshHelper.StopAutoRunning();
        return false;
    }

    /// <summary>
    /// 瞬移到指定位置（用于 TP 钱袋子等特殊场景）
    /// </summary>
    public unsafe bool TeleportToPosition(Vector3 position)
    {
        // 使用游戏内部瞬移机制（主要用于洞内钱袋子）
        // 通过修改玩家位置或使用 GM 指令实现
        // 注意：这可能需要特殊权限或特定的 hook
        try
        {
            // 通过 FFXIVClientStructs 修改玩家位置
            var player = _plugin.ClientState.LocalPlayer;
            if (player == null) return false;

            // 这里需要使用 unsafe 代码修改游戏内部位置
            // 具体实现取决于游戏版本和 Dalamud API

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
        VnavmeshHelper.StopAutoRunning();
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Cancel();
    }
}
