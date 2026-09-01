using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace TreasureHunt.Helpers;

/// <summary>
/// 水晶/传送辅助工具类
/// </summary>
public static unsafe class AetheryteHelper
{
    /// <summary>
    /// 获取当前附近可见的水晶对象（仅用于近距离导航参考）
    /// </summary>
    public static List<(uint aetheryteId, string name, Vector3 position)> GetNearbyVisibleAetherytes(Vector3 targetPos)
    {
        var result = new List<(uint aetheryteId, string name, Vector3 position)>();

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte) continue;

            var name = obj.Name.ToString();
            var pos = obj.Position;
            result.Add((obj.BaseId, name, pos));
        }

        result.Sort((a, b) =>
            Vector3.Distance(targetPos, a.position).CompareTo(Vector3.Distance(targetPos, b.position)));

        return result;
    }

    /// <summary>
    /// 获取最近的可见水晶（仅用于近距离导航参考）
    /// </summary>
    public static (uint aetheryteId, string name, Vector3 position)? GetNearestVisibleAetheryte(Vector3 targetPos)
    {
        var aetherytes = GetNearbyVisibleAetherytes(targetPos);
        return aetherytes.Count > 0 ? aetherytes[0] : null;
    }

    /// <summary>
    /// 传送到指定水晶 ID
    /// </summary>
    public static bool TeleportToAetheryte(uint aetheryteId)
    {
        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return false;

            // 检查是否正在传送中
            if (IsTeleporting())
            {
                Plugin.Log.Warning("正在传送中，忽略新的传送请求");
                return false;
            }

            // 检查水晶是否已解锁
            if (!IsAetheryteUnlocked(aetheryteId))
            {
                Plugin.Log.Warning($"水晶 {aetheryteId} 未解锁，无法传送");
                return false;
            }

            telepo->Teleport(aetheryteId, 0);
            Plugin.Log.Information($"传送到水晶 ID: {aetheryteId}");
            return true;
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
            var telepo = Telepo.Instance();
            if (telepo == null) return false;

            // 遍历已解锁的水晶列表
            for (var i = 0; i < telepo->TeleportList.Length; i++)
            {
                ref readonly var tp = ref telepo->TeleportList[i];
                if (tp.AetheryteId == aetheryteId)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"检查水晶解锁状态失败: {ex.Message}");
            // 出错时默认返回 true，避免误判
            return true;
        }
    }

    /// <summary>
    /// 获取所有已解锁的水晶列表
    /// </summary>
    public static List<(uint aetheryteId, string name, uint territoryId)> GetUnlockedAetherytes()
    {
        var result = new List<(uint, string, uint)>();
        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return result;

            for (var i = 0; i < telepo->TeleportList.Length; i++)
            {
                ref readonly var tp = ref telepo->TeleportList[i];
                var name = tp.PlaceName.Value.Name.ToString();
                result.Add((tp.AetheryteId, name, tp.TerritoryId));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取已解锁水晶列表失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// 通过名称查找已解锁的水晶 ID
    /// </summary>
    public static uint FindAetheryteIdByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;

        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return 0;

            for (var i = 0; i < telepo->TeleportList.Length; i++)
            {
                ref readonly var tp = ref telepo->TeleportList[i];
                var placeName = tp.PlaceName.Value.Name.ToString();
                var aethernetName = string.Empty;

                // 也可以从 Aetheryte Excel 表读取 AethernetName
                try
                {
                    var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
                    if (aetheryteSheet != null)
                    {
                        var row = aetheryteSheet.GetRow(tp.AetheryteId);
                        if (row.IsAetheryte)
                        {
                            aethernetName = row.AethernetName.Value.Name.ToString();
                        }
                    }
                }
                catch { }

                if (placeName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    aethernetName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(placeName, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(aethernetName, StringComparison.OrdinalIgnoreCase))
                {
                    return tp.AetheryteId;
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
    /// 检查是否正在传送中（区域切换）
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

        // 传送网使用券的 ItemId
        const uint aetheryteTicketId = 21073;
        var count = inventory->GetInventoryItemCount(aetheryteTicketId);
        return count > 0;
    }

    /// <summary>
    /// 获取传送费用
    /// </summary>
    public static int GetTeleportCost(uint aetheryteId)
    {
        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return 999;

            for (var i = 0; i < telepo->TeleportList.Length; i++)
            {
                ref readonly var tp = ref telepo->TeleportList[i];
                if (tp.AetheryteId == aetheryteId)
                {
                    return tp.GilCost;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取传送费用失败: {ex.Message}");
        }
        return 999;
    }
}
