using System;
using System.Numerics;

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
            var sub = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>(
                $"{VnavmeshLabel}.SimpleMove.PathfindAndMoveTo");
            return sub.InvokeFunc(destination, fly);
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
            var sub = Plugin.PluginInterface.GetIpcSubscriber<object>($"{VnavmeshLabel}.Path.Stop");
            sub.InvokeAction();
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
