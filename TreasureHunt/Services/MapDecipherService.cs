using System;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
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
            if (!FindMapInInventory(out var item, out _))
            {
                return new DecipherResult { Success = false, ErrorMessage = "背包中未找到藏宝图" };
            }

            // 使用解读技能
            if (!ExecuteDecipher())
            {
                return new DecipherResult { Success = false, ErrorMessage = "执行解读失败" };
            }

            await Task.Delay(1000, token);

            // 读取解读后的地图信息
            var mapData = await ReadDecipheredMap(token);
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

    public unsafe bool HasDecipheredMap()
    {
        // 检查 event item 中是否已有解读的地图
        var invManager = InventoryManager.Instance();
        if (invManager == null) return false;

        var eventItems = invManager->GetInventoryContainer(InventoryType.KeyItems);
        for (var i = 0; i < eventItems->Size; i++)
        {
            var item = eventItems->GetInventorySlot(i);
            // 解读后的地图会变成 event item
            // 需要检查是否是 Gargantuaskin 解读后的 item
            if (item->ItemId != 0 && item->ItemId != TreasureMapConstants.GargantuaskinItemId)
            {
                // 检查是否是解读过的 G18 地图
                // 解读后的地图 itemId 会变化
                // 这个需要根据实际游戏数据确认
            }
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

    private async Task<TreasureMapData?> ReadDecipheredMap(CancellationToken token)
    {
        await Task.Delay(500, token);

        // 读取解读后地图的位置信息
        // 地图数据存储在游戏内存中，需要通过 FFXIVClientStructs 读取
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
                    TerritoryId = 0, // 需要从游戏数据读取
                    TerritoryName = "Living Memory",
                }
            };

            // 读取地图坐标
            // 通过 AgentMap 或直接读取游戏内存获取藏宝图标记位置
            var (mapX, mapY) = ReadTreasureMapCoordinates();
            if (mapData.Location != null)
            {
                mapData.Location.MapX = mapX;
                mapData.Location.MapY = mapY;
            }

            OnLog?.Invoke($"读取地图坐标: ({mapX}, {mapY})");
            return mapData;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"读取地图数据失败: {ex.Message}");
            return null;
        }
    }

    private unsafe (float mapX, float mapY) ReadTreasureMapCoordinates()
    {
        // 通过 AgentMap 读取藏宝图标记位置
        // 这需要逆向工程确认具体的内存偏移量
        // 目前返回 0,0 作为占位，实际使用时需要调试确认
        var agentMap = AgentMap.Instance();
        if (agentMap == null) return (0, 0);

        // 读取地图标记信息
        // AgentMap 中有当前地图的标记数据
        // 需要遍历 map markers 查找 treasure marker
        return (0, 0);
    }

    private unsafe void MarkMapLocation()
    {
        // 在大地图上放置旗帜标记
        // 通过 AgentMap 或直接修改地图标记数据
        var agentMap = AgentMap.Instance();
        if (agentMap == null) return;

        // 设置旗帜标记到藏宝图位置
        // 使用 /flag 命令或直接调用 AgentMap 方法
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
