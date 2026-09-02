using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ECommons.GameHelpers;

namespace TreasureHunt.Helpers;

/// <summary>
/// vnavmesh 辅助类 - 参考 AutoDuty 的实现模式
/// </summary>
public static class VnavmeshHelper
{
    private const string VnavmeshLabel = "vnavmesh";

    /// <summary>
    /// 检查 vnavmesh 是否可用
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>($"{VnavmeshLabel}.Nav.IsReady");
            return sub.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查寻路是否正在进行中
    /// </summary>
    public static bool IsPathfindInProgress()
    {
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>(
                $"{VnavmeshLabel}.SimpleMove.PathfindInProgress");
            return sub.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查是否正在自动移动
    /// </summary>
    public static bool IsPathRunning()
    {
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>(
                $"{VnavmeshLabel}.Path.IsRunning");
            return sub.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取当前路径点数量
    /// </summary>
    public static int GetNumWaypoints()
    {
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<int>(
                $"{VnavmeshLabel}.Path.NumWaypoints");
            return sub.InvokeFunc();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 设置路径容差
    /// </summary>
    public static void SetTolerance(float tolerance)
    {
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<float, object>(
                $"{VnavmeshLabel}.Path.SetTolerance");
            sub.InvokeAction(tolerance);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"设置路径容差失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 开始寻路并移动到目标位置（异步等待到达）
    /// 参考 AutoDuty 的 Move 模式
    /// </summary>
    /// <param name="destination">目标位置</param>
    /// <param name="tolerance">到达容差（米）</param>
    /// <param name="fly">是否飞行</param>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <param name="token">取消令牌</param>
    /// <returns>是否成功到达</returns>
    public static async Task<bool> MoveToAsync(Vector3 destination, float tolerance = 2.0f,
        bool fly = false, int timeoutMs = 30000, CancellationToken token = default)
    {
        try
        {
            if (!IsAvailable())
            {
                Plugin.Log.Error("vnavmesh 不可用");
                return false;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;

            // 已经在目标附近
            if (Vector3.Distance(player.Position, destination) <= tolerance)
            {
                return true;
            }

            // 等待 vnavmesh 就绪（不在寻路中且没有路径点）
            var waitStart = DateTime.Now;
            while ((IsPathfindInProgress() || GetNumWaypoints() > 0) && 
                   (DateTime.Now - waitStart).TotalMilliseconds < 2000)
            {
                await Task.Delay(100, token);
                if (token.IsCancellationRequested) return false;
            }

            // 设置容差并开始寻路
            SetTolerance(tolerance);

            var pathfindSub = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, Task<bool>>(
                $"{VnavmeshLabel}.SimpleMove.PathfindAndMoveTo");
            var result = await pathfindSub.InvokeFunc(destination, fly);

            if (!result)
            {
                Plugin.Log.Warning($"vnavmesh 寻路失败，无法到达目标");
                return false;
            }

            // 等待到达目标
            var startTime = DateTime.Now;
            var lastStuckCheck = DateTime.Now;
            var lastPosition = player.Position;

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (token.IsCancellationRequested)
                {
                    Stop();
                    return false;
                }

                player = Plugin.ObjectTable.LocalPlayer;
                if (player == null)
                {
                    await Task.Delay(100, token);
                    continue;
                }

                var distance = Vector3.Distance(player.Position, destination);

                // 到达目标
                if (distance <= tolerance)
                {
                    Stop();
                    return true;
                }

                // 防卡死检测：如果5秒没移动超过0.5米，重新寻路
                if ((DateTime.Now - lastStuckCheck).TotalMilliseconds > 5000)
                {
                    var moved = Vector3.Distance(lastPosition, player.Position);
                    if (moved < 0.5f && !IsPathfindInProgress())
                    {
                        Plugin.Log.Warning($"检测到卡死（移动{moved:F1}m），重新寻路...");
                        Stop();
                        await Task.Delay(500, token);

                        // 重新发起寻路
                        SetTolerance(tolerance);
                        await pathfindSub.InvokeFunc(destination, fly);
                    }
                    lastStuckCheck = DateTime.Now;
                    lastPosition = player.Position;
                }

                await Task.Delay(100, token);
            }

            // 超时
            Stop();
            var finalDist = player != null ? Vector3.Distance(player.Position, destination) : -1;
            Plugin.Log.Warning($"寻路超时（{timeoutMs}ms），距离目标还有 {finalDist:F1}m");
            return false;
        }
        catch (OperationCanceledException)
        {
            Stop();
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"vnavmesh 移动异常: {ex.Message}");
            Stop();
            return false;
        }
    }

    /// <summary>
    /// 停止自动移动
    /// </summary>
    public static void Stop()
    {
        try
        {
            var pathStopSub = Plugin.PluginInterface.GetIpcSubscriber<object>(
                $"{VnavmeshLabel}.Path.Stop");
            pathStopSub.InvokeAction();
        }
        catch { }

        try
        {
            var simpleMoveStopSub = Plugin.PluginInterface.GetIpcSubscriber<object>(
                $"{VnavmeshLabel}.SimpleMove.Stop");
            simpleMoveStopSub.InvokeAction();
        }
        catch { }
    }

    /// <summary>
    /// 检查玩家是否在移动
    /// </summary>
    public static bool IsPlayerMoving()
    {
        try
        {
            return IsPathRunning() || IsPathfindInProgress() || GetNumWaypoints() > 0;
        }
        catch
        {
            return false;
        }
    }

    #region 兼容旧 API（其他服务还在使用，逐步迁移到 MoveToAsync）

    [Obsolete("请使用 MoveToAsync 替代")]
    public static bool PathfindAndMoveTo(Vector3 destination, bool fly = false)
    {
        try
        {
            if (!IsAvailable()) return false;
            SetTolerance(2.0f);
            var sub = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, Task<bool>>(
                $"{VnavmeshLabel}.SimpleMove.PathfindAndMoveTo");
            return sub.InvokeFunc(destination, fly).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"vnavmesh 寻路失败: {ex.Message}");
            return false;
        }
    }

    [Obsolete("请使用 Stop() 替代")]
    public static void StopAutoRunning() => Stop();

    [Obsolete("请使用 IsPlayerMoving() 或 IsPathRunning() 替代")]
    public static bool IsAutoRunning() => IsPlayerMoving();

    [Obsolete("请使用 MoveToAsync 并检查返回值，或直接计算距离")]
    public static bool IsAtDestination(Vector3 destination, float tolerance = 3.0f)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        return Vector3.Distance(player.Position, destination) <= tolerance;
    }

    #endregion
}
