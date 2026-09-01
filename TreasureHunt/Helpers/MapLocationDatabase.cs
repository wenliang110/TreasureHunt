using System;
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
    public uint TerritoryId { get; set; }
    public uint NearestAetheryteId { get; set; }
    public string NearestAetheryteName { get; set; } = string.Empty;
    public string NearestAetheryteNameCN { get; set; } = string.Empty;
}

public static class MapLocationDatabase
{
    private static List<GargantuaskinLocationEntry>? _locations;
    private static Dictionary<uint, uint>? _aetheryteNameCache;

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

    /// <summary>
    /// Resolve the aetheryte (teleport) ID for a matched location entry.
    /// If the entry already carries a hardcoded NearestAetheryteId, use it directly;
    /// otherwise look the aetheryte up by name in the Aetheryte Excel sheet.
    /// </summary>
    public static uint ResolveAetheryteId(GargantuaskinLocationEntry? entry)
    {
        if (entry == null) return 0;
        if (entry.NearestAetheryteId != 0) return entry.NearestAetheryteId;

        var name = string.IsNullOrEmpty(entry.NearestAetheryteName)
            ? entry.NearestAetheryteNameCN
            : entry.NearestAetheryteName;
        if (string.IsNullOrEmpty(name)) return 0;

        return LookupAetheryteIdByName(name);
    }

    /// <summary>
    /// Iterate the Aetheryte Excel sheet and return the row ID (teleport ID) of the
    /// aetheryte whose place name matches the supplied string, ignoring case.
    /// </summary>
    public static uint LookupAetheryteIdByName(string name)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (sheet == null) return 0;

            foreach (var row in sheet)
            {
                if (!row.IsAetheryte) continue;
                var placeName = row.PlaceName;
                var aethernetName = row.AethernetName;
                var pn = placeName.Value.Name.ToString();
                var an = aethernetName.Value.Name.ToString();
                if (pn.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    an.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(pn, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(an, StringComparison.OrdinalIgnoreCase))
                {
                    return row.RowId;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"查找晶石 ID 失败 ({name}): {ex.Message}");
        }
        return 0;
    }
}
