using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace TreasureHunt.Helpers;

public static unsafe class GameObjectHelper
{
    public static IGameObject? FindNearestObjectByDataId(uint dataId, Vector3? fromPosition = null)
    {
        var pos = fromPosition ?? Plugin.ClientState.LocalPlayer?.Position ?? Vector3.Zero;
        IGameObject? nearest = null;
        var minDist = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.DataId != dataId) continue;

            var dist = Vector3.Distance(pos, obj.Position);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = obj;
            }
        }
        return nearest;
    }

    public static IGameObject? FindNearestObjectByName(string name, Vector3? fromPosition = null)
    {
        var pos = fromPosition ?? Plugin.ClientState.LocalPlayer?.Position ?? Vector3.Zero;
        IGameObject? nearest = null;
        var minDist = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (!obj.Name.ToString().Contains(name, StringComparison.OrdinalIgnoreCase)) continue;

            var dist = Vector3.Distance(pos, obj.Position);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = obj;
            }
        }
        return nearest;
    }

    public static List<IGameObject> FindAllObjectsByDataId(uint dataId)
    {
        var results = new List<IGameObject>();
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.DataId == dataId)
                results.Add(obj);
        }
        return results;
    }

    public static List<IGameObject> FindAllObjectsByName(string name)
    {
        var results = new List<IGameObject>();
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.Name.ToString().Contains(name, StringComparison.OrdinalIgnoreCase))
                results.Add(obj);
        }
        return results;
    }

    public static bool InteractWithObject(IGameObject obj)
    {
        try
        {
            var objIdx = obj.ObjectIndex;
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null) return false;

            targetSystem->InteractWithObject(
                (GameObject*)obj.Address, false);

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"交互对象失败: {ex.Message}");
            return false;
        }
    }

    public static unsafe void SetTarget(IGameObject? obj)
    {
        if (obj == null)
        {
            Plugin.TargetManager.Target = null;
            return;
        }
        Plugin.TargetManager.Target = obj;
    }

    public static bool IsInInteractRange(IGameObject obj, float maxDistance = 3.0f)
    {
        var player = Plugin.ClientState.LocalPlayer;
        if (player == null) return false;
        return Vector3.Distance(player.Position, obj.Position) <= maxDistance;
    }

    public static IGameObject? GetTreasureCoffer()
    {
        // 宝箱的 DataId 通常以特定前缀开头
        // 需要根据实际游戏版本调试确认
        return FindAllObjectsByDataId(0).FirstOrDefault(o =>
            o.Name.ToString().Contains("treasure", StringComparison.OrdinalIgnoreCase) ||
            o.Name.ToString().Contains("宝箱", StringComparison.OrdinalIgnoreCase) ||
            o.Name.ToString().Contains("coffer", StringComparison.OrdinalIgnoreCase));
    }

    public static IGameObject? GetPortalTransferCircle()
    {
        return FindAllObjectsByDataId(0).FirstOrDefault(o =>
            o.Name.ToString().Contains("転送魔紋", StringComparison.OrdinalIgnoreCase) ||
            o.Name.ToString().Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
            o.Name.ToString().Contains("portal", StringComparison.OrdinalIgnoreCase) ||
            o.Name.ToString().Contains("传送", StringComparison.OrdinalIgnoreCase));
    }

    public static List<IGameObject> GetMoneyBags()
    {
        var bags = new List<IGameObject>();
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            var name = obj.Name.ToString();
            if (name.Contains("輝く袋", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("袋", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("bag", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("geld", StringComparison.OrdinalIgnoreCase))
            {
                bags.Add(obj);
            }
        }
        return bags;
    }

    public static List<IGameObject> GetShiningBags()
    {
        var bags = new List<IGameObject>();
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            var name = obj.Name.ToString();
            // 輝く袋 = Shining Bag, 金の輝く袋 = Golden Shining Bag (3x)
            if (name.Contains("輝く袋", StringComparison.OrdinalIgnoreCase))
            {
                bags.Add(obj);
            }
        }
        // 优先金色袋
        bags.Sort((a, b) =>
        {
            bool aGold = a.Name.ToString().Contains("金");
            bool bGold = b.Name.ToString().Contains("金");
            if (aGold && !bGold) return -1;
            if (!aGold && bGold) return 1;
            return 0;
        });
        return bags;
    }
}
