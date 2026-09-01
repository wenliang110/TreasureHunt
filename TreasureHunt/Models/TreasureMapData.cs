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
}
