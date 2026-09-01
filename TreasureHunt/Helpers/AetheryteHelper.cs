using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace TreasureHunt.Helpers;

/// <summary>
/// 水晶/传送辅助工具类
/// 参考 TeleporterPlugin 的实现：先 UpdateAetheryteList 再操作
/// </summary>
public static unsafe class AetheryteHelper
{
    /// <summary>
    /// 获取附近可见的水晶对象
    /// </summary>
    public static List<(uint aetheryteId, string name, Vector3 position)> GetNearbyVisibleAetherytes(Vector3 targetPos)
    {
        var result = new List<(uint aetheryteId, string name, Vector3 position)>();

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.ObjectKind != ObjectKind.Aetheryte) continue;

            var name = obj.Name.ToString();
            var pos = obj.Position;
            result.Add((obj.BaseId, name, pos));
        }

        result.Sort((a, b) =>
            Vector3.Distance(targetPos, a.position).CompareTo(Vector3.Distance(targetPos, b.position)));

        return result;
    }

    public static (uint aetheryteId, string name, Vector3 position)? GetNearestVisibleAetheryte(Vector3 targetPos)
    {
        var aetherytes = GetNearbyVisibleAetherytes(targetPos);
        return aetherytes.Count > 0 ? aetherytes[0] : null;
    }

    /// <summary>
    /// 刷新水晶列表（必须在读取 TeleportList 前调用）
    /// </summary>
    private static bool RefreshAetheryteList()
    {
        try
        {
            if (Control.GetLocalPlayer() == null)
            {
                Plugin.Log.Warning("刷新水晶列表失败: 本地玩家为空");
                return false;
            }

            var telepo = Telepo.Instance();
            if (telepo == null) return false;

            // 关键：必须先刷新列表，否则 TeleportList 可能为空或过期
            var result = telepo->UpdateAetheryteList();
            if (result == null)
            {
                Plugin.Log.Warning("UpdateAetheryteList 返回空");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"刷新水晶列表异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 传送到指定水晶 ID
    /// </summary>
    public static bool TeleportToAetheryte(uint aetheryteId)
    {
        try
        {
            // 1. 检查本地玩家
            if (Control.GetLocalPlayer() == null)
            {
                Plugin.Log.Warning("传送失败: 本地玩家为空");
                return false;
            }

            // 2. 检查传送技能是否可用 (Action 5 = Teleport)
            var status = ActionManager.Instance()->GetActionStatus(ActionType.Action, 5);
            if (status != 0)
            {
                Plugin.Log.Warning($"传送技能不可用，状态码: {status}");
                return false;
            }

            // 3. 检查是否正在传送中
            if (IsTeleporting())
            {
                Plugin.Log.Warning("正在传送中，忽略新的传送请求");
                return false;
            }

            // 4. 刷新水晶列表
            if (!RefreshAetheryteList())
            {
                Plugin.Log.Warning("刷新水晶列表失败");
                return false;
            }

            // 5. 在列表中查找目标水晶，获取 SubIndex
            var telepo = Telepo.Instance();
            if (telepo == null) return false;

            byte subIndex = 0;
            bool found = false;
            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                if (telepo->TeleportList[i].AetheryteId == aetheryteId)
                {
                    subIndex = telepo->TeleportList[i].SubIndex;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Plugin.Log.Warning($"水晶 {aetheryteId} 未在传送列表中找到");
                return false;
            }

            // 6. 执行传送
            var result = telepo->Teleport(aetheryteId, subIndex);
            Plugin.Log.Information($"传送结果: {result} (水晶ID={aetheryteId}, SubIndex={subIndex})");
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"传送失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查水晶是否已解锁
    /// </summary>
    public static bool IsAetheryteUnlocked(uint aetheryteId)
    {
        try
        {
            if (!RefreshAetheryteList()) return false;

            var telepo = Telepo.Instance();
            if (telepo == null) return false;

            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                if (telepo->TeleportList[i].AetheryteId == aetheryteId)
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"检查水晶解锁状态失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取所有已解锁的水晶列表
    /// </summary>
    public static List<(uint aetheryteId, uint territoryId, int gilCost)> GetUnlockedAetherytes()
    {
        var result = new List<(uint, uint, int)>();
        try
        {
            if (!RefreshAetheryteList()) return result;

            var telepo = Telepo.Instance();
            if (telepo == null) return result;

            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                ref readonly var tp = ref telepo->TeleportList[i];
                result.Add((tp.AetheryteId, (uint)tp.TerritoryId, (int)tp.GilCost));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取已解锁水晶列表失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// 获取所有已解锁的水晶及其名称（单次刷新列表，避免重复调用）
    /// </summary>
    public static List<(uint aetheryteId, string name, uint territoryId)> GetUnlockedAetherytesWithNames()
    {
        var result = new List<(uint, string, uint)>();
        try
        {
            if (!RefreshAetheryteList()) return result;

            var telepo = Telepo.Instance();
            if (telepo == null) return result;

            var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (aetheryteSheet == null) return result;

            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                ref readonly var tp = ref telepo->TeleportList[i];
                var row = aetheryteSheet.GetRow(tp.AetheryteId);
                var placeName = row.PlaceName.IsValid ? row.PlaceName.Value.Name.ToString() : "";
                var aethernetName = row.AethernetName.IsValid ? row.AethernetName.Value.Name.ToString() : "";
                var displayName = !string.IsNullOrEmpty(aethernetName) ? aethernetName : placeName;
                result.Add((tp.AetheryteId, displayName, (uint)tp.TerritoryId));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取已解锁水晶(含名称)失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// 通过名称查找已解锁的水晶 ID（支持中英文）
    /// </summary>
    public static uint FindAetheryteIdByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;

        try
        {
            if (!RefreshAetheryteList()) return 0;

            var telepo = Telepo.Instance();
            if (telepo == null) return 0;

            // 建立已解锁水晶 ID 的 hash set
            var unlockedIds = new HashSet<uint>();
            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                unlockedIds.Add(telepo->TeleportList[i].AetheryteId);
            }

            // 从 Aetheryte Excel 表中查找匹配的水晶
            var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (aetheryteSheet == null) return 0;

            foreach (var row in aetheryteSheet)
            {
                if (!row.IsAetheryte) continue;
                if (!unlockedIds.Contains(row.RowId)) continue;

                var placeName = row.PlaceName.Value.Name.ToString();
                var aethernetName = row.AethernetName.Value.Name.ToString();

                if (placeName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    aethernetName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(placeName, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(aethernetName, StringComparison.OrdinalIgnoreCase))
                {
                    return row.RowId;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"按名称查找水晶失败 ({name}): {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// 获取水晶名称
    /// </summary>
    public static string GetAetheryteName(uint aetheryteId)
    {
        try
        {
            var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (aetheryteSheet == null) return string.Empty;

            var row = aetheryteSheet.GetRow(aetheryteId);
            if (row.RowId != aetheryteId) return string.Empty;

            var aethernetName = row.AethernetName.Value.Name.ToString();
            var placeName = row.PlaceName.Value.Name.ToString();
            return !string.IsNullOrEmpty(aethernetName) ? aethernetName : placeName;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取水晶名称失败 ({aetheryteId}): {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 检查是否正在传送中
    /// </summary>
    public static bool IsTeleporting()
    {
        return Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
               Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51] ||
               Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent];
    }

    /// <summary>
    /// 检查是否有传送网使用券
    /// </summary>
    public static bool HasAetheryteTicket()
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null) return false;

        const uint aetheryteTicketId = 21073;
        return inventory->GetInventoryItemCount(aetheryteTicketId) > 0;
    }

    /// <summary>
    /// 获取传送费用
    /// </summary>
    public static int GetTeleportCost(uint aetheryteId)
    {
        try
        {
            if (!RefreshAetheryteList()) return 999;

            var telepo = Telepo.Instance();
            if (telepo == null) return 999;

            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                ref readonly var tp = ref telepo->TeleportList[i];
                if (tp.AetheryteId == aetheryteId)
                    return (int)tp.GilCost;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取传送费用失败: {ex.Message}");
        }
        return 999;
    }
}
