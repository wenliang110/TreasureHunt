using System;
using System.Numerics;
using System.Threading.Tasks;

namespace TreasureHunt.Helpers;

public static class VnavmeshHelper
{
    private const string VnavmeshLabel = "vnavmesh";

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

    public static bool PathfindAndMoveTo(Vector3 destination, bool fly = false)
    {
        try
        {
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

    public static bool IsAutoRunning()
    {
        try
        {
            // 优先使用 Path.IsRunning 判断是否正在移动
            var pathRunningSub = Plugin.PluginInterface.GetIpcSubscriber<bool>(
                $"{VnavmeshLabel}.Path.IsRunning");
            bool isPathRunning = pathRunningSub.InvokeFunc();

            // 同时检查 SimpleMove.PathfindInProgress 作为补充
            var pathfindSub = Plugin.PluginInterface.GetIpcSubscriber<bool>(
                $"{VnavmeshLabel}.SimpleMove.PathfindInProgress");
            bool isPathfindInProgress = pathfindSub.InvokeFunc();

            return isPathRunning || isPathfindInProgress;
        }
        catch
        {
            return false;
        }
    }

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

    public static bool PathfindInProgress()
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

    public static void StopAutoRunning()
    {
        try
        {
            // 停止 Path 移动
            var pathStopSub = Plugin.PluginInterface.GetIpcSubscriber<object>($"{VnavmeshLabel}.Path.Stop");
            pathStopSub.InvokeAction();

            // 同时停止 SimpleMove
            var simpleMoveStopSub = Plugin.PluginInterface.GetIpcSubscriber<object>($"{VnavmeshLabel}.SimpleMove.Stop");
            simpleMoveStopSub.InvokeAction();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"停止 vnavmesh 自动运行失败: {ex.Message}");
        }
    }

    public static bool IsAtDestination(Vector3 destination, float tolerance = 3.0f)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        return Vector3.Distance(player.Position, destination) <= tolerance;
    }
}
