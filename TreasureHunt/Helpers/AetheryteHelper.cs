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
    /// 修复：不依赖 IsValid 检查，直接访问 Excel 数据，多种回退策略
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

            var placeNameSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>();

            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                ref readonly var tp = ref telepo->TeleportList[i];
                var row = aetheryteSheet.GetRow(tp.AetheryteId);

                // 策略1: 直接访问 PlaceName（不检查 IsValid）
                string displayName = "";
                try
                {
                    var pnRow = row.PlaceName;
                    if (pnRow.RowId > 0)
                    {
                        // 直接从 PlaceName sheet 获取名称（不依赖 IsValid）
                        if (placeNameSheet != null)
                        {
                            var pn = placeNameSheet.GetRow(pnRow.RowId);
                            displayName = pn.Name.ToString();
                        }
                    }
                }
                catch { }

                // 策略2: 如果 PlaceName 为空，尝试 AethernetName
                if (string.IsNullOrEmpty(displayName))
                {
                    try
                    {
                        var anRow = row.AethernetName;
                        if (anRow.RowId > 0 && placeNameSheet != null)
                        {
                            var an = placeNameSheet.GetRow(anRow.RowId);
                            displayName = an.Name.ToString();
                        }
                    }
                    catch { }
                }

                // 策略3: 如果仍然为空，用原始 IsValid 方式
                if (string.IsNullOrEmpty(displayName))
                {
                    try
                    {
                        if (row.PlaceName.IsValid)
                            displayName = row.PlaceName.Value.Name.ToString();
                        if (string.IsNullOrEmpty(displayName) && row.AethernetName.IsValid)
                            displayName = row.AethernetName.Value.Name.ToString();
                    }
                    catch { }
                }

                // 策略4: 如果所有方法都失败，用 ID 作为名称
                if (string.IsNullOrEmpty(displayName))
                    displayName = $"Aetheryte#{tp.AetheryteId}";

                result.Add((tp.AetheryteId, displayName, (uint)tp.TerritoryId));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取已解锁水晶(含名称)失败: {ex.Message}");
        }
        return result;
    }

    // 主城水晶 ID 常量（基于 FFXIV Aetheryte Excel sheet RowId）
    // 这些 ID 在所有语言版本中一致，不受名称格式影响
    private static readonly HashSet<uint> LimsaAetheryteIds = new()
    {
        1,   // 利姆萨·罗敏萨 (主水晶)
        8,   // 利姆萨·罗敏萨下层甲板
        9,   // 利姆萨·罗敏萨上层甲板
        10,  // 利姆萨·罗敏萨甲板材层
        11,  // 费雷萨德斯
    };

    private static readonly HashSet<uint> GridaniaAetheryteIds = new()
    {
        2,   // 格里达尼亚 (主水晶)
        19,  // 旧街
        20,  // 新街
        21,  // 龟甲胡同
        22,  // 橡木路
        23,  // 翠绿路
    };

    private static readonly HashSet<uint> UldahAetheryteIds = new()
    {
        3,   // 乌尔达哈 (主水晶)
        4,   // 现世回廊
        5,   // 碎日路
        6,   // 通货路
        7,   // 炽热路
    };

    private static readonly HashSet<uint> IshgardAetheryteIds = new()
    {
        13,  // 伊修加德基础层 (主水晶)
        14,  // 底层
        15,  // 圣贤道
        16,  // 天穹街
        17,  // 云墙街
        18,  // 莲华灵泉
    };

    private static readonly HashSet<uint> KuganeAetheryteIds = new()
    {
        56,  // 神拳痕 (主水晶)
        57,  // 比倍镇
        58,  // 命通座
        59,  // 雾纱洞
        60,  // 九十九九
    };

    private static readonly HashSet<uint> CrystariumAetheryteIds = new()
    {
        70,  // 水晶都 (主水晶)
        71,  // 神意之泉
        72,  // 历史庭园
        73,  // 水晶台阶
        74,  // 水晶路线
        75,  // 幻光院
    };

    private static readonly HashSet<uint> SharlayanAetheryteIds = new()
    {
        85,  // 沙利亚恩 (主水晶)
        86,  // 知见之门
        87,  // 阿帕利梅斯
        88,  // 智能之泉
        89,  // 药水院
    };

    private static readonly HashSet<uint> TuliyollalAetheryteIds = new()
    {
        100, // 图莱尤拉 (主水晶)
        101, // 翼梦路
        102, // 风乘路
        103, // 帆梯路
        104, // 真松路
        105, // 翔空路
    };

    /// <summary>
    /// 判断水晶 ID 是否属于主城
    /// </summary>
    public static bool IsMainCityAetheryte(uint aetheryteId)
    {
        return LimsaAetheryteIds.Contains(aetheryteId) ||
               GridaniaAetheryteIds.Contains(aetheryteId) ||
               UldahAetheryteIds.Contains(aetheryteId) ||
               IshgardAetheryteIds.Contains(aetheryteId) ||
               KuganeAetheryteIds.Contains(aetheryteId) ||
               CrystariumAetheryteIds.Contains(aetheryteId) ||
               SharlayanAetheryteIds.Contains(aetheryteId) ||
               TuliyollalAetheryteIds.Contains(aetheryteId);
    }

    /// <summary>
    /// 获取主城水晶优先级（利姆萨 > 乌尔达哈 > 格里达尼亚 > 其他）
    /// 利姆萨下层甲板交易板最近
    /// </summary>
    public static int GetMainCityPriority(uint aetheryteId)
    {
        if (LimsaAetheryteIds.Contains(aetheryteId)) return 1;
        if (UldahAetheryteIds.Contains(aetheryteId)) return 2;
        if (GridaniaAetheryteIds.Contains(aetheryteId)) return 3;
        if (IshgardAetheryteIds.Contains(aetheryteId)) return 4;
        if (KuganeAetheryteIds.Contains(aetheryteId)) return 5;
        if (CrystariumAetheryteIds.Contains(aetheryteId)) return 6;
        if (SharlayanAetheryteIds.Contains(aetheryteId)) return 7;
        if (TuliyollalAetheryteIds.Contains(aetheryteId)) return 8;
        return 0;
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
    /// 参考 vsatisfy: 同时检查 IsCastingTeleport 和 BetweenAreas
    /// 参考 Lifestream: 检查以太网传送状态
    /// </summary>
    public static bool IsTeleporting()
    {
        return Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
               Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51] ||
               Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent] ||
               GameHelper.IsCastingTeleport();
    }

    /// <summary>
    /// 检查 Lifestream 插件是否可用
    /// 参考 Lifestream: 用于优化以太网传送
    /// </summary>
    public static bool IsLifestreamAvailable()
    {
        try
        {
            var ipc = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsReady");
            return ipc.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试使用 Lifestream 传送到指定以太网
    /// 参考 Lifestream IPC: 可以更精确地传送到以太网碎片
    /// </summary>
    public static bool TryLifestreamTeleport(uint aetheryteId)
    {
        try
        {
            if (!IsLifestreamAvailable()) return false;
            var ipc = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.Teleport");
            return ipc.InvokeFunc(aetheryteId);
        }
        catch
        {
            return false;
        }
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
