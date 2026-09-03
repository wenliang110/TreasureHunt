using System.Collections.Generic;
using System.Numerics;

namespace TreasureHunt.Models;

public class TreasureMapData
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public uint Grade { get; set; }
    public uint RequiredLevel { get; set; }
    public uint MapIconId { get; set; }
    public bool IsDeciphered { get; set; }
    public TreasureMapLocation? Location { get; set; }
}

public static class TreasureMapConstants
{
    public const uint GargantuaskinItemId = 46185;
    public const uint GargantuaskinDecipheredItemId = 2003785;
    public const string GargantuaskinItemName = "陈旧的卡冈图亚革地图";
    public const string GargantuaskinItemNameEN = "Timeworn Gargantuaskin Map";
    public const uint GargantuaskinGrade = 18;
    public const uint GargantuaskinLevel = 100;

    public const uint PortalDungeonTerritoryId = 1200;
    public const string PortalDungeonName = "Oneiron宝物库";
    public const string PortalDungeonNameEN = "Vault Oneiron";

    // G18 挖宝地图区域（记忆之野）
    public const uint GargantuaskinTerritoryId = 1185;
    public const string GargantuaskinTerritoryName = "记忆之野";
    // 默认传送目标（还没解读地图时，先传送到地图区域的记忆节点）
    public const string DefaultAetheryteNameCN = "记忆节点·记忆";
    public const string DefaultAetheryteNameEN = "Leynode Memoris";

    public const int PartyMaxSize = 8;
    public const int TreasureMapCooldownHours = 18;

    /// <summary>
    /// G18 (记忆之野) 8个宝箱预设位置
    /// 参考 SND 自动挖宝脚本，藏宝图解读后 flag 可能有偏差，
    /// 用这8个固定位置来修正导航目标，取最近的一个作为实际宝箱位置
    /// </summary>
    public static readonly Vector3[] G18ChestPositions = new[]
    {
        new Vector3(-549.7368f, -0.41204834f, 728.2977f),    // 第1组
        new Vector3(678.9197f, 7.6447144f, 694.8806f),       // 第2组
        new Vector3(851.13293f, 40.299072f, 332.29565f),     // 第3组
        new Vector3(480.70496f, 25.192627f, -166.43018f),    // 第4组
        new Vector3(179.2782f, 39.902344f, -728.81665f),     // 第5组
        new Vector3(-61.08191f, 37.979614f, 80.9491f),       // 第6组
        new Vector3(-147.29541f, 38.254395f, -279.68268f),   // 第7组
        new Vector3(-608.88074f, -5.0202637f, -535.5459f),   // 第8组
    };

    /// <summary>
    /// 获取距离目标位置最近的G18宝箱预设坐标
    /// </summary>
    public static Vector3 GetNearestG18ChestPosition(Vector3 referencePos)
    {
        var nearest = G18ChestPositions[0];
        var minDist = float.MaxValue;
        foreach (var pos in G18ChestPositions)
        {
            // XZ平面距离（忽略Y轴高度差）
            var dx = pos.X - referencePos.X;
            var dz = pos.Z - referencePos.Z;
            var dist = dx * dx + dz * dz;
            if (dist < minDist)
            {
                minDist = dist;
                nearest = pos;
            }
        }
        return nearest;
    }
}
