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
    public const uint GargantuaskinItemId = 43885;
    public const string GargantuaskinItemName = "古旧的巨兽皮地图";
    public const string GargantuaskinItemNameEN = "Timeworn Gargantuaskin Map";
    public const uint GargantuaskinGrade = 18;
    public const uint GargantuaskinLevel = 100;

    public const uint PortalDungeonTerritoryId = 1200;
    public const string PortalDungeonName = "Oneiron宝物库";
    public const string PortalDungeonNameEN = "Vault Oneiron";

    public const int PartyMaxSize = 8;
    public const int TreasureMapCooldownHours = 18;
}
