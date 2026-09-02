using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace TreasureHunt.Helpers;

public static unsafe class GameObjectHelper
{
    public static IGameObject? FindNearestObjectByDataId(uint dataId, Vector3? fromPosition = null)
    {
        var pos = fromPosition ?? Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        IGameObject? nearest = null;
        var minDist = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.BaseId != dataId) continue;

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
        var pos = fromPosition ?? Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
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
            if (obj.BaseId == dataId)
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
            // 参考 Untarnished Heart: 先设置目标再交互，提高交互可靠性
            SetTarget(obj);

            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null) return false;

            targetSystem->InteractWithObject(
                (GameObject*)obj.Address, false);

            // 参考 vsatisfy: 交互后推进可能出现的 Talk 对话框
            // 宝箱/传送门/洞内机关交互后可能有 NPC 对话需要推进
            System.Threading.Thread.Sleep(200);
            if (GameHelper.IsTalkOpen())
            {
                GameHelper.AdvanceTalkUntilClosed();
            }

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
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        return Vector3.Distance(player.Position, obj.Position) <= maxDistance;
    }

    public static IGameObject? GetTreasureCoffer()
    {
        // 参考 Untarnished Heart: 使用 ObjectKind.Treasure 精确查找宝箱
        // 比名字匹配更可靠（不受语言设置影响）
        var player = Plugin.ObjectTable.LocalPlayer;
        var pos = player?.Position ?? Vector3.Zero;
        IGameObject? nearest = null;
        var minDist = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;

            // 优先使用 ObjectKind.Treasure（最可靠）
            if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure)
            {
                var dist = Vector3.Distance(pos, obj.Position);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = obj;
                }
            }
        }

        if (nearest != null)
            return nearest;

        // 回退到名字匹配（兼容旧版本）
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
