using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace TreasureHunt.Helpers;

public static class GameObjectHelper
{
    public static unsafe IGameObject? FindNearestObjectByDataId(uint dataId, Vector3? fromPosition = null)
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

    public static unsafe IGameObject? FindNearestObjectByName(string name, Vector3? fromPosition = null)
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

    /// <summary>
    /// 按多个名称关键词查找最近的对象（任一匹配即可）
    /// </summary>
    public static IGameObject? FindNearestObjectByNames(string[] names, Vector3? fromPosition = null)
    {
        var pos = fromPosition ?? Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        IGameObject? nearest = null;
        var minDist = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            var objName = obj.Name.ToString();
            if (string.IsNullOrEmpty(objName)) continue;

            bool matched = false;
            foreach (var name in names)
            {
                if (objName.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) continue;

            var dist = Vector3.Distance(pos, obj.Position);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = obj;
            }
        }
        return nearest;
    }

    /// <summary>
    /// 查找所有可交互对象（排除玩家）
    /// 用于洞内搜索未知 DataId 的对象
    /// </summary>
    public static List<IGameObject> FindInteractableObjects(Vector3? fromPosition = null, float maxDist = 30.0f)
    {
        var pos = fromPosition ?? Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var results = new List<IGameObject>();

        var player = Plugin.ObjectTable.LocalPlayer;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            // 排除玩家角色（通过地址比较）
            if (player != null && obj.Address == player.Address) continue;

            var dist = Vector3.Distance(pos, obj.Position);
            if (dist > maxDist) continue;

            // 只收集特定 ObjectKind 的对象（这些类型不包括玩家）
            var kind = obj.ObjectKind;
            if (kind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure ||
                kind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj ||
                kind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte ||
                kind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc)
            {
                results.Add(obj);
            }
        }

        results.Sort((a, b) => Vector3.Distance(pos, a.Position).CompareTo(Vector3.Distance(pos, b.Position)));
        return results;
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

    /// <summary>
    /// 交互对象（同步版本 - 仅执行交互）
    /// </summary>
    public static unsafe bool InteractWithObject(IGameObject obj)
    {
        try
        {
            SetTarget(obj);

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

    /// <summary>
    /// 交互对象并自动处理后续所有对话框序列（异步版本）
    /// 完整流程: 交互 → 等待对话框 → 处理 Talk/SelectString/SelectIconString/SelectYesno → 等待过场动画 → 处理二次对话框
    /// 
    /// 对话框处理顺序（游戏实际弹出顺序）:
    /// 1. Talk (NPC 对话) - 推进直到关闭
    /// 2. SelectString (文字选择菜单) - 选择指定选项
    /// 3. SelectIconString (图标选择菜单) - 选择指定选项  
    /// 4. SelectYesno (确认对话框) - 自动确认(是)
    /// 5. 可能循环以上步骤
    /// </summary>
    public static async Task<bool> InteractWithObjectAsync(IGameObject obj, CancellationToken token,
        int selectStringIndex = 0, int selectIconStringIndex = 0, bool autoConfirmYesno = true,
        int totalTimeoutMs = 30000)
    {
        try
        {
            // 执行交互
            if (!InteractWithObject(obj))
                return false;

            // 等待并处理对话框序列
            await ProcessDialogSequence(token, selectStringIndex, selectIconStringIndex, autoConfirmYesno, totalTimeoutMs);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"异步交互对象失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 处理对话框序列（交互后调用）
    /// 轮询检测各种对话框并自动处理
    /// </summary>
    public static async Task ProcessDialogSequence(CancellationToken token,
        int selectStringIndex = 0, int selectIconStringIndex = 0, bool autoConfirmYesno = true,
        int totalTimeoutMs = 30000)
    {
        var startTime = DateTime.Now;
        var processedAny = false;

        while ((DateTime.Now - startTime).TotalMilliseconds < totalTimeoutMs)
        {
            token.ThrowIfCancellationRequested();

            bool foundDialog = false;

            // 1. Talk 对话框 - 推进直到关闭
            if (GameHelper.IsTalkOpen())
            {
                Plugin.Log.Debug("检测到 Talk 对话框，推进...");
                GameHelper.AdvanceTalkUntilClosed();
                foundDialog = true;
                processedAny = true;
                await Task.Delay(300, token);
                continue;
            }

            // 2. SelectString 文字选择菜单
            if (GameHelper.IsSelectStringOpen())
            {
                Plugin.Log.Debug($"检测到 SelectString，选择第 {selectStringIndex} 项...");
                GameHelper.SelectStringOption(selectStringIndex);
                foundDialog = true;
                processedAny = true;
                await Task.Delay(500, token);
                continue;
            }

            // 3. SelectIconString 图标选择菜单
            if (GameHelper.IsSelectIconStringOpen())
            {
                Plugin.Log.Debug($"检测到 SelectIconString，选择第 {selectIconStringIndex} 项...");
                GameHelper.SelectIconStringOption(selectIconStringIndex);
                foundDialog = true;
                processedAny = true;
                await Task.Delay(500, token);
                continue;
            }

            // 4. SelectYesno 确认对话框
            if (autoConfirmYesno && GameHelper.IsSelectYesnoOpen())
            {
                Plugin.Log.Debug("检测到 SelectYesno，确认(是)...");
                GameHelper.SelectYesnoOption(0);
                foundDialog = true;
                processedAny = true;
                await Task.Delay(500, token);
                continue;
            }

            // 5. 过场动画检测
            if (GameHelper.IsCutsceneActive())
            {
                Plugin.Log.Debug("检测到过场动画，等待结束...");
                await AsyncHelper.WaitForCutsceneAndDialogsAsync(token);
                foundDialog = true;
                processedAny = true;
                continue;
            }

            if (foundDialog)
                continue;

            // 如果之前处理过对话框，额外等待确保没有延迟弹出的新对话框
            if (processedAny)
            {
                await Task.Delay(1000, token);
                if (GameHelper.IsTalkOpen() || GameHelper.IsSelectStringOpen() ||
                    GameHelper.IsSelectIconStringOpen() || GameHelper.IsSelectYesnoOpen())
                continue;
            }

            break;
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

    /// <summary>
    /// 查找宝箱（使用 ObjectKind.Treasure + 名称匹配）
    /// </summary>
    public static IGameObject? GetTreasureCoffer()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var pos = player?.Position ?? Vector3.Zero;
        IGameObject? nearest = null;
        var minDist = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;

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

        // 回退到名字匹配
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            var name = obj.Name.ToString();
            if (name.Contains("treasure", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("宝箱", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("coffer", StringComparison.OrdinalIgnoreCase))
            {
                var dist = Vector3.Distance(pos, obj.Position);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = obj;
                }
            }
        }
        return nearest;
    }

    /// <summary>
    /// 查找传送魔纹（転送魔紋）
    /// </summary>
    public static IGameObject? GetPortalTransferCircle()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var pos = player?.Position ?? Vector3.Zero;
        IGameObject? nearest = null;
        var minDist = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            var name = obj.Name.ToString();
            if (name.Contains("転送魔紋", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("portal", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("传送", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("魔纹", StringComparison.OrdinalIgnoreCase))
            {
                var dist = Vector3.Distance(pos, obj.Position);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = obj;
                }
            }
        }
        return nearest;
    }

    /// <summary>
    /// 查找洞内机关/交互对象
    /// 洞内机关的名称因语言不同而异，使用多关键词匹配
    /// </summary>
    public static IGameObject? GetDungeonObject()
    {
        var dungeonObjectNames = new[]
        {
            "魔法的装置", "magical", "device", "装置", "机关",
            "仕掛け", "contrivance", "mechanism"
        };

        var obj = FindNearestObjectByNames(dungeonObjectNames);
        if (obj != null) return obj;

        // 回退：找附近所有 EventObj 类型对象（洞内机关通常是 EventObj）
        var interactables = FindInteractableObjects(maxDist: 50.0f);
        var filtered = interactables.Where(o =>
        {
            var name = o.Name.ToString();
            return !name.Contains("宝箱", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("treasure", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("coffer", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("転送魔紋", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("transfer", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("portal", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("袋", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("bag", StringComparison.OrdinalIgnoreCase);
        });

        return filtered.FirstOrDefault();
    }

    /// <summary>
    /// 查找洞内宝箱
    /// </summary>
    public static IGameObject? GetDungeonChest()
    {
        var coffer = GetTreasureCoffer();
        if (coffer != null) return coffer;

        var chestNames = new[] { "宝箱", "treasure", "coffer", "chest", "宝物" };
        return FindNearestObjectByNames(chestNames);
    }

    /// <summary>
    /// 查找进入下一层的入口/装置
    /// </summary>
    public static IGameObject? GetNextFloorEntrance()
    {
        var nextFloorNames = new[]
        {
            "次の階", "next floor", "下层", "下一层",
            "進む", "proceed", "先へ", "奥へ"
        };

        var obj = FindNearestObjectByNames(nextFloorNames);
        if (obj != null) return obj;

        // 回退：查找附近非宝箱非传送门的可交互对象
        var interactables = FindInteractableObjects(maxDist: 50.0f);
        var filtered = interactables.Where(o =>
        {
            var name = o.Name.ToString();
            return !name.Contains("宝箱", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("treasure", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("coffer", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("袋", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("bag", StringComparison.OrdinalIgnoreCase);
        });

        return filtered.FirstOrDefault();
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
            if (name.Contains("輝く袋", StringComparison.OrdinalIgnoreCase))
            {
                bags.Add(obj);
            }
        }
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
