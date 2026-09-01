using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TreasureHunt.Models;

namespace TreasureHunt.Helpers;

public class GargantuaskinLocationEntry
{
    public int Id { get; set; }
    public float MapX { get; set; }
    public float MapY { get; set; }
    public string NearestAetheryteName { get; set; } = string.Empty;
    public string NearestAetheryteNameCN { get; set; } = string.Empty;
}

public static class MapLocationDatabase
{
    private static List<GargantuaskinLocationEntry>? _locations;

    public static List<GargantuaskinLocationEntry> GetLocations()
    {
        if (_locations != null) return _locations;

        var path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory?.FullName!,
            "Data", "gargantuaskin_locations.json");
        if (!File.Exists(path))
        {
            Plugin.Log.Error($"藏宝图点位数据库文件不存在: {path}");
            _locations = new List<GargantuaskinLocationEntry>();
            return _locations;
        }

        var json = File.ReadAllText(path);
        _locations = JsonSerializer.Deserialize<List<GargantuaskinLocationEntry>>(json) ?? new();
        Plugin.Log.Information($"加载了 {_locations.Count} 个 Gargantuaskin 藏宝图点位");
        return _locations;
    }

    public static GargantuaskinLocationEntry? FindByCoordinates(float mapX, float mapY, float tolerance = 1.0f)
    {
        foreach (var loc in GetLocations())
        {
            if (System.Math.Abs(loc.MapX - mapX) <= tolerance &&
                System.Math.Abs(loc.MapY - mapY) <= tolerance)
            {
                return loc;
            }
        }
        return null;
    }
}
