using System.Numerics;

namespace TreasureHunt.Models;

public class TreasureMapLocation
{
    public uint TerritoryId { get; set; }
    public string TerritoryName { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
    public float MapX { get; set; }
    public float MapY { get; set; }
    public Vector3 WorldPosition { get; set; }
    public uint NearestAetheryteId { get; set; }
    public string NearestAetheryteName { get; set; } = string.Empty;
    public Vector3 NearestAetherytePosition { get; set; }
}
