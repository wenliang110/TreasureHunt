using System;
using System.Numerics;
using ECommons.Interop;

namespace TreasureHunt.Helpers;

/// <summary>
/// vnavmesh IPC 集成 - 通过 IPC 调用 vnavmesh 插件进行自动导航
/// </summary>
public static class VnavmeshHelper
{
    private const string VnavmeshLabel = "vnavmesh";

    private static bool _isInitialized = false;

    public static bool IsAvailable()
    {
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>($"{VnavmeshLabel}.Nav.IsReady");
            var action = sub.InvokeFunc();
            _isInitialized = true;
            return action;
        }
        catch
        {
            return false;
        }
    }

    public static bool PathfindAndMoveTo(Vector3 destination)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;

            var sub = Plugin.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>(
                $"{VnavmeshLabel}.Nav.PathfindMoveTo");
            sub.InvokeFunc(player.Position, destination);
            return true;
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
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>(
                $"{VnavmeshLabel}.Nav.IsAutoRunning");
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
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>(
                $"{VnavmeshLabel}.Nav.StopAutoRunning");
            sub.InvokeFunc();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"停止 vnavmesh 自动运行失败: {ex.Message}");
        }
    }

    public static bool PathfindTo(Vector3 destination)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;

            var sub = Plugin.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>(
                $"{VnavmeshLabel}.Nav.Pathfind");
            return sub.InvokeFunc(player.Position, destination);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"vnavmesh 路径计算失败: {ex.Message}");
            return false;
        }
    }

    public static bool IsAtDestination(Vector3 destination, float tolerance = 3.0f)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        return Vector3.Distance(player.Position, destination) <= tolerance;
    }
}
