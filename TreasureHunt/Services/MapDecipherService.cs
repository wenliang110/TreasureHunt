using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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

    // 解读动作 ID - GeneralAction 表行号 (19 = Decipher/解读)
    // 使用 ActionType.GeneralAction 时传 GeneralAction 表的行号，不是 Action 表 ID (1694)
    private const uint DecipherActionId = 19;

    // 挖掘动作 ID - GeneralAction 表行号 (20 = Dig/挖掘)
    private const uint DigActionId = 20;

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
    /// 如果已有解读过的地图（flag marker 存在），直接读取已有数据，不重复解读
    /// </summary>
    public async Task<DecipherResult> DecipherMapAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // 检查是否已有解读过的地图（flag marker 存在）
            if (HasDecipheredMap())
            {
                OnLog?.Invoke("检测到已有解读过的地图，直接读取坐标");

                // 直接读取已有 flag marker 数据
                var existingMapData = ReadDecipheredMap(token);
                if (existingMapData != null)
                {
                    var existingMatchedLoc = MapLocationDatabase.FindByCoordinates(
                        existingMapData.Location?.MapX ?? 0,
                        existingMapData.Location?.MapY ?? 0);

                    if (existingMatchedLoc != null)
                        OnLog?.Invoke($"匹配到点位: ({existingMatchedLoc.MapX}, {existingMatchedLoc.MapY}) 水晶: {existingMatchedLoc.NearestAetheryteNameCN}");

                    return new DecipherResult
                    {
                        Success = true,
                        MapData = existingMapData,
                        MatchedLocation = existingMatchedLoc
                    };
                }

                // flag marker 存在但读取失败，继续尝试重新解读
                OnLog?.Invoke("读取已有地图数据失败，尝试重新解读...");
            }

            // 查找未解读的藏宝图
            if (!FindMapInInventory(out _, out _))
            {
                return new DecipherResult { Success = false, ErrorMessage = "背包中未找到藏宝图" };
            }

            // 关闭可能阻挡解读的 UI 窗口（交易板等）
            CloseBlockingWindows();
            await Task.Delay(500, token);

            // 使用解读技能
            if (!ExecuteDecipher())
            {
                return new DecipherResult { Success = false, ErrorMessage = "执行解读失败" };
            }

            // 处理解读后弹出的选择窗口（选择要解读的藏宝图）
            // 使用 GeneralAction 方式会弹出 SelectString 窗口；使用 ActionType.Item 可能直接解读
            await HandleDecipherDialog(token);

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
        var timeout = TimeSpan.FromSeconds(15);
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
    /// 解读后的藏宝图会在 AgentMap 上设置一个 flag marker（FlagMarkerCount > 0）。
    /// 注意: NextMapAllowanceTimestamp 是采集藏宝图的18小时冷却，不是解读状态，不能用来判断是否已解读。
    /// </summary>
    public unsafe bool HasDecipheredMap()
    {
        // 检查 AgentMap 上的 flag marker —— 解读后游戏会放置一个旗帜标记
        if (TryGetFlagMarker(out _, out _))
            return true;

        return false;
    }

    private unsafe bool ExecuteDecipher()
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
            {
                OnLog?.Invoke("ActionManager 不可用");
                return false;
            }

            // 方式1: 直接使用藏宝图道具（参考 RotationSolver: ActionType.Item）
            // 使用道具直接解读，不弹选择窗口
            var itemId = TreasureMapConstants.GargantuaskinItemId;
            var itemStatus = actionManager->GetActionStatus(ActionType.Item, itemId);
            if (itemStatus == 0)
            {
                var itemResult = actionManager->UseAction(ActionType.Item, itemId);
                if (itemResult)
                {
                    OnLog?.Invoke($"使用藏宝图道具 (ActionType.Item, ID={itemId})");
                    return true;
                }
                OnLog?.Invoke("道具使用失败，回退到解读技能");
            }
            else
            {
                OnLog?.Invoke($"道具不可用 (status={itemStatus})，使用解读技能");
            }

            // 方式2: 使用解读技能 (GeneralAction 19)
            // 注意: 这种方式会弹出 SelectString 选择窗口，需要后续处理
            var status = actionManager->GetActionStatus(ActionType.GeneralAction, DecipherActionId);
            if (status != 0)
            {
                OnLog?.Invoke($"解读动作不可用 (status={status})");
                return false;
            }

            var result = actionManager->UseAction(ActionType.GeneralAction, DecipherActionId);
            if (!result)
            {
                OnLog?.Invoke("UseAction 返回 false，解读执行失败");
                return false;
            }

            OnLog?.Invoke($"执行解读技能 (GeneralAction ID={DecipherActionId})");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"执行解读失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 处理解读后的对话框序列
    /// 使用统一的 ProcessDialogSequence 处理所有对话框类型
    /// 完整流程: SelectIconString(选择地图) → SelectYesno(确认解读)
    /// </summary>
    private async Task HandleDecipherDialog(CancellationToken token)
    {
        OnLog?.Invoke("等待解读对话框...");
        await GameObjectHelper.ProcessDialogSequence(token,
            selectStringIndex: 0,
            selectIconStringIndex: 0,
            autoConfirmYesno: true,
            totalTimeoutMs: 15000);
    }

    public unsafe bool ExecuteDig()
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
            {
                OnLog?.Invoke("ActionManager 不可用");
                return false;
            }

            var status = actionManager->GetActionStatus(ActionType.GeneralAction, DigActionId);
            if (status != 0)
            {
                OnLog?.Invoke($"挖掘动作不可用 (status={status})");
                return false;
            }

            var result = actionManager->UseAction(ActionType.GeneralAction, DigActionId);
            if (!result)
            {
                OnLog?.Invoke("UseAction 返回 false，挖掘执行失败");
                return false;
            }

            OnLog?.Invoke($"执行挖掘技能 (GeneralAction ID={DigActionId})");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"执行挖掘失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 关闭可能阻挡解读/挖掘的 UI 窗口
    /// </summary>
    private unsafe void CloseBlockingWindows()
    {
        try
        {
            var addonNames = new[] {
                "ItemSearch", "ItemSearchResult",
                "SelectYesno", "SelectString", "SelectIconString",
                "Talk", "ContextMenu",
                "NeedGreed", "Loot",
                "Inventory", "InventoryExpansion",
                "Shop", "ShopDigit"
            };
            foreach (var name in addonNames)
            {
                var addon = Plugin.GameGui.GetAddonByName(name);
                if (addon.Address != IntPtr.Zero)
                {
                    var atk = (AtkUnitBase*)addon.Address;
                    atk->FireCallback(unchecked((uint)-1), null, true);
                    OnLog?.Invoke($"已关闭窗口: {name}");
                }
            }
        }
        catch { }
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
