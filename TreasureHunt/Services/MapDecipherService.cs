using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TreasureHunt.Helpers;
using TreasureHunt.Models;

namespace TreasureHunt.Services;

public class DecipherResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public TreasureMapData? MapData { get; set; }
    public GargantuaskinLocationEntry? MatchedLocation { get; set; }
}

public class MapDecipherService : IDisposable
{
    private readonly Plugin _plugin;
    private CancellationTokenSource? _cts;

    public event Action<string>? OnLog;

    // 解读动作 ID (Decipher)
    private const uint DecipherActionId = 12897;

    // 挖掘动作 ID (Dig)
    private const uint DigActionId = 12898;

    // 藏宝图旗帜标记默认图标 ID
    private const uint TreasureFlagIconId = 0xEC91;

    public MapDecipherService(Plugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>
    /// 在背包中查找 Gargantuaskin 藏宝图
    /// </summary>
    public unsafe bool FindMapInInventory(out InventoryItem item, out int slot)
    {
        item = default;
        slot = -1;

        var invManager = InventoryManager.Instance();
        if (invManager == null) return false;

        var inventory = invManager->GetInventoryContainer(InventoryType.Inventory1);
        for (var i = 0; i < inventory->Size; i++)
        {
            var invItem = inventory->GetInventorySlot(i);
            if (invItem->ItemId == TreasureMapConstants.GargantuaskinItemId)
            {
                item = *invItem;
                slot = i;
                OnLog?.Invoke($"在背包槽位 {i} 找到藏宝图");
                return true;
            }
        }

        // 检查其他背包
        var inventory2 = invManager->GetInventoryContainer(InventoryType.Inventory2);
        for (var i = 0; i < inventory2->Size; i++)
        {
            var invItem = inventory2->GetInventorySlot(i);
            if (invItem->ItemId == TreasureMapConstants.GargantuaskinItemId)
            {
                item = *invItem;
                slot = i;
                OnLog?.Invoke($"在背包2槽位 {i} 找到藏宝图");
                return true;
            }
        }

        // 检查 chocobo saddlebag (陆行鸟鞍囊)
        var saddlebag = invManager->GetInventoryContainer(InventoryType.SaddleBag1);
        for (var i = 0; i < saddlebag->Size; i++)
        {
            var invItem = saddlebag->GetInventorySlot(i);
            if (invItem->ItemId == TreasureMapConstants.GargantuaskinItemId)
            {
                item = *invItem;
                slot = i;
                OnLog?.Invoke($"在鞍囊槽位 {i} 找到藏宝图");
                return true;
            }
        }

        OnLog?.Invoke("背包中未找到 Gargantuaskin 藏宝图");
        return false;
    }

    /// <summary>
    /// 解读藏宝图
    /// </summary>
    public async Task<DecipherResult> DecipherMapAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // 检查是否已有解读中的地图
            if (HasDecipheredMap())
            {
                OnLog?.Invoke("已有一张解读过的地图，先处理它");
                return new DecipherResult { Success = false, ErrorMessage = "已有一张解读过的地图" };
            }

            // 查找未解读的藏宝图
            if (!FindMapInInventory(out _, out _))
            {
                return new DecipherResult { Success = false, ErrorMessage = "背包中未找到藏宝图" };
            }

            // 使用解读技能
            if (!ExecuteDecipher())
            {
                return new DecipherResult { Success = false, ErrorMessage = "执行解读失败" };
            }

            // 等待旗帜标记出现（解读后游戏会在 AgentMap 上放置一个 flag marker）
            if (!await WaitForFlagMarker(token))
            {
                return new DecipherResult { Success = false, ErrorMessage = "解读后未检测到地图标记" };
            }

            // 读取解读后的地图信息
            var mapData = ReadDecipheredMap(token);
            if (mapData == null)
            {
                return new DecipherResult { Success = false, ErrorMessage = "读取地图信息失败" };
            }

            // 匹配已知坐标
            var matchedLoc = MapLocationDatabase.FindByCoordinates(mapData.Location?.MapX ?? 0, mapData.Location?.MapY ?? 0);
            if (matchedLoc != null)
            {
                OnLog?.Invoke($"匹配到点位: ({matchedLoc.MapX}, {matchedLoc.MapY}) 水晶: {matchedLoc.NearestAetheryteNameCN}");
            }

            // 标记位置（如果启用）
            if (_plugin.Configuration.EnableMarkLocation)
            {
                MarkMapLocation();
                OnLog?.Invoke("已在大地图标记位置");
            }

            return new DecipherResult
            {
                Success = true,
                MapData = mapData,
                MatchedLocation = matchedLoc
            };
        }
        catch (OperationCanceledException)
        {
            return new DecipherResult { Success = false, ErrorMessage = "已取消" };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"解读异常: {ex.Message}");
            return new DecipherResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 等待藏宝图旗帜标记出现在 AgentMap 上。
    /// 解读后游戏会通过 SetFlagMapMarker 在 AgentMap 上放置一个标记。
    /// </summary>
    private async Task<bool> WaitForFlagMarker(CancellationToken token)
    {
        OnLog?.Invoke("等待地图标记出现...");
        var timeout = TimeSpan.FromSeconds(8);
        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime) < timeout)
        {
            token.ThrowIfCancellationRequested();

            bool found;
            unsafe { found = TryGetFlagMarker(out _, out _); }
            if (found)
            {
                OnLog?.Invoke("检测到地图标记");
                return true;
            }

            await Task.Delay(200, token);
        }

        OnLog?.Invoke("等待地图标记超时");
        return false;
    }

    /// <summary>
    /// 检查玩家是否有一张已解读的地图。
    /// 解读后的藏宝图会在 AgentMap 上设置一个 flag marker（FlagMarkerCount > 0），
    /// 同时 UIState 会记录下一次可解读的时间戳 (NextMapAllowanceTimestamp)。
    /// </summary>
    public unsafe bool HasDecipheredMap()
    {
        // 优先检查 AgentMap 上的 flag marker —— 解读后游戏会放置一个旗帜标记
        if (TryGetFlagMarker(out _, out _))
            return true;

        // 回退：检查 UIState 的下一次藏宝图解读时间戳。
        // 当玩家解读了一张图，NextMapAllowanceTimestamp 会被设置为 18 小时后的时间；
        // 若时间戳大于当前 Unix 时间，说明玩家有一张正在冷却中的藏宝图。
        try
        {
            var uiState = UIState.Instance();
            if (uiState != null)
            {
                var nextAllowance = uiState->NextMapAllowanceTimestamp;
                if (nextAllowance > 0)
                {
                    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (nextAllowance > now)
                        return true;
                }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"检查解读状态失败: {ex.Message}");
        }

        return false;
    }

    private unsafe bool ExecuteDecipher()
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null) return false;

            // 使用解读技能
            var actionType = ActionType.GeneralAction;
            actionManager->UseAction(actionType, DecipherActionId);

            OnLog?.Invoke("执行解读技能");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"执行解读失败: {ex.Message}");
            return false;
        }
    }

    public unsafe bool ExecuteDig()
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null) return false;

            var actionType = ActionType.GeneralAction;
            actionManager->UseAction(actionType, DigActionId);

            OnLog?.Invoke("执行挖掘技能");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"执行挖掘失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 读取解读后的地图信息，包括领土 ID、地图 ID、世界坐标与显示坐标。
    /// </summary>
    private unsafe TreasureMapData? ReadDecipheredMap(CancellationToken token)
    {
        try
        {
            var mapData = new TreasureMapData
            {
                ItemId = TreasureMapConstants.GargantuaskinItemId,
                ItemName = TreasureMapConstants.GargantuaskinItemName,
                Grade = TreasureMapConstants.GargantuaskinGrade,
                RequiredLevel = TreasureMapConstants.GargantuaskinLevel,
                IsDeciphered = true,
                Location = new TreasureMapLocation
                {
                    TerritoryName = "Living Memory",
                }
            };

            // 读取地图坐标
            if (TryGetFlagMarker(out var flag, out var agentMap))
            {
                var territoryId = flag.TerritoryId;
                var mapId = flag.MapId;
                // FlagMapMarker 的 XFloat/YFloat 存储的是世界坐标
                // (XFloat = world X, YFloat = world Z，由 SetFlagMapMarker 写入)
                var worldX = flag.XFloat;
                var worldZ = flag.YFloat;

                if (mapData.Location != null)
                {
                    mapData.Location.TerritoryId = territoryId;
                    mapData.Location.MapX = 0;
                    mapData.Location.MapY = 0;
                    mapData.Location.WorldPosition = new Vector3(worldX, 0, worldZ);
                    mapData.Location.NearestAetheryteName = ResolveTerritoryName(agentMap, territoryId, mapId);
                }

                // 将世界坐标转换为地图显示坐标 (如 9.3, 10.5)
                var (mapX, mapY) = ReadTreasureMapCoordinates();
                if (mapData.Location != null)
                {
                    mapData.Location.MapX = mapX;
                    mapData.Location.MapY = mapY;
                }

                OnLog?.Invoke($"读取地图坐标: ({mapX}, {mapY}) 领土: {territoryId} 世界: ({worldX:F1}, {worldZ:F1})");
            }
            else
            {
                // 回退：直接尝试读取坐标
                var (mapX, mapY) = ReadTreasureMapCoordinates();
                if (mapData.Location != null)
                {
                    mapData.Location.MapX = mapX;
                    mapData.Location.MapY = mapY;
                }
                OnLog?.Invoke($"读取地图坐标: ({mapX}, {mapY})");
            }

            return mapData;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"读取地图数据失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读取藏宝图标记的地图显示坐标 (X, Y)。
    /// 藏宝图解读后，游戏会在 AgentMap 的 FlagMapMarkers 中放置一个标记，
    /// 其 XFloat/YFloat 字段存储世界坐标 (worldX, worldZ)，
    /// 需通过 Map Excel 表的 OffsetX/OffsetY/SizeFactor 转换为显示坐标。
    /// </summary>
    public unsafe (float mapX, float mapY) ReadTreasureMapCoordinates()
    {
        if (!TryGetFlagMarker(out var flag, out _))
            return (0, 0);

        var worldX = flag.XFloat;
        var worldZ = flag.YFloat;
        var mapId = flag.MapId;

        // 通过 Map Excel 表获取 OffsetX/OffsetY/SizeFactor，将世界坐标转换为显示坐标
        var mapSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
        if (mapSheet != null)
        {
            var mapRow = mapSheet.GetRow(mapId);
            // Dalamud 的 MapUtil.WorldToMap 接受 (worldX, worldZ) 并返回显示坐标 (X, Y)
            var mapCoords = MapUtil.WorldToMap(new Vector2(worldX, worldZ), mapRow);
            return (mapCoords.X, mapCoords.Y);
        }

        // 回退公式：mapX = (worldX * sizeFactor / 100 + offsetX) * 0.02 + 1
        // 无法获取 Map 表时，使用 flag marker 中 MapMarkerBase 的短整型坐标 (position * 16)
        return (flag.MapMarker.X / 16.0f, flag.MapMarker.Y / 16.0f);
    }

    /// <summary>
    /// 在大地图上放置旗帜标记并打开地图窗口。
    /// 通过 MapLinkPayload 创建一个可交互的地图链接，调用 GameGui.OpenMapWithMapLink 打开。
    /// </summary>
    public unsafe void MarkMapLocation()
    {
        if (!TryGetFlagMarker(out var flag, out _))
        {
            OnLog?.Invoke("无可标记的藏宝图位置");
            return;
        }

        try
        {
            var (mapX, mapY) = ReadTreasureMapCoordinates();
            // 使用人类可读的显示坐标创建 MapLinkPayload
            var payload = new MapLinkPayload(
                flag.TerritoryId,
                flag.MapId,
                mapX,
                mapY);

            Plugin.GameGui.OpenMapWithMapLink(payload);
            OnLog?.Invoke($"已标记地图位置: ({mapX}, {mapY}) 领土 {flag.TerritoryId}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"标记地图失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从 AgentMap 读取藏宝图旗帜标记。
    /// 返回 false 表示当前没有旗帜标记（FlagMarkerCount == 0）。
    /// </summary>
    internal unsafe bool TryGetFlagMarker(out FlagMapMarker flag, out AgentMap* agentMap)
    {
        flag = default;
        agentMap = GetAgentMap();
        if (agentMap == null) return false;

        // FlagMarkerCount 为 0 表示没有旗帜标记
        if (agentMap->FlagMarkerCount == 0) return false;

        var markers = agentMap->FlagMapMarkers;
        if (markers.Length == 0) return false;

        flag = markers[0];
        return true;
    }

    /// <summary>
    /// 获取 AgentMap 单例指针。AgentMap 通过 AgentModule 按 AgentId.Map 获取。
    /// </summary>
    internal static unsafe AgentMap* GetAgentMap()
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null) return null;

            var agent = agentModule->GetAgentByInternalId(AgentId.Map);
            if (agent == null) return null;

            return (AgentMap*)agent;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 根据领土 ID 查询领土名称。
    /// </summary>
    private static unsafe string ResolveTerritoryName(AgentMap* agentMap, uint territoryId, uint mapId)
    {
        // 优先从 TerritoryType Excel 表读取名称
        try
        {
            var ttSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (ttSheet != null)
            {
                var row = ttSheet.GetRow(territoryId);
                var name = row.Name.ToString();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch { }

        // 回退到 AgentMap 当前选中的领土
        if (agentMap != null && agentMap->SelectedTerritoryId == territoryId)
            return "Living Memory";

        return "Living Memory";
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Cancel();
    }
}
