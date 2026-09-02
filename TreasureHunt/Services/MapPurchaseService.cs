using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using TreasureHunt.Helpers;
using TreasureHunt.Models;

namespace TreasureHunt.Services;

public enum PurchaseState
{
    Idle,
    OpeningMarketBoard,
    SearchingItem,
    SelectingResult,
    CheckingPrice,
    ConfirmingPurchase,
    WaitingForPurchase,
    Done,
    Error
}

public class PurchaseResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public uint ItemId { get; set; }
    public int Price { get; set; }
}

public class MapPurchaseService : IDisposable
{
    private readonly Plugin _plugin;
    private PurchaseState _state = PurchaseState.Idle;
    private CancellationTokenSource? _cts;
    private DateTime _lastActionTime = DateTime.MinValue;

    private const int ActionCooldownMs = 500;

    // 交易板搜索关键字 (国服陈旧的卡冈图亚革地图)
    private const string SearchKeywordCN = "陈旧的卡冈图亚革地图";
    private const string SearchKeywordEN = "Timeworn Gargantuaskin Map";

    // 市场布告板相关名称关键词
    private static readonly string[] MarketBoardKeywords = new[]
    {
        "市场布告板", "市场", "布告板", "交易板",
        "Market Board", "Market", "Retainer Bell",
        "市場", "掲示板", "リテイナーベル"
    };

    public event Action<PurchaseState>? StateChanged;
    public event Action<string>? OnLog;

    public PurchaseState State
    {
        get => _state;
        private set
        {
            _state = value;
            StateChanged?.Invoke(_state);
            OnLog?.Invoke($"购买状态: {value}");
        }
    }

    public MapPurchaseService(Plugin plugin)
    {
        _plugin = plugin;
    }

    public async Task<PurchaseResult> PurchaseMapAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            State = PurchaseState.OpeningMarketBoard;

            int initialMapCount = GetMapCountInInventory();
            OnLog?.Invoke($"购买前藏宝图数量: {initialMapCount}");

            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                OnLog?.Invoke($"购买尝试 {attempt}/{maxRetries}");

                // 先关闭所有可能打开的窗口
                CloseAllMarketWindows();
                await Task.Delay(500, token);

                // 步骤1：寻路到交易板并交互（激活 ItemSearch Agent）
                if (!await OpenMarketBoard(token))
                {
                    OnLog?.Invoke($"打开交易板失败 (尝试 {attempt}/{maxRetries})");
                    if (attempt < maxRetries)
                    {
                        OnLog?.Invoke("等待 3 秒后重试...");
                        await Task.Delay(3000, token);
                    }
                    continue;
                }

                // 步骤2：使用 InfoProxyItemSearch 底层 API 搜索（参考 Daily Routines 反编译）
                // 不需要操作 UI 窗口，直接调用数据代理
                await WaitAndSetState(PurchaseState.SearchingItem, token);
                var (searchOk, listings) = await SearchViaInfoProxy(token);
                if (!searchOk || listings == null || listings.Count == 0)
                {
                    OnLog?.Invoke($"搜索失败，未找到上架列表 (尝试 {attempt}/{maxRetries})");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(2000, token);
                    }
                    continue;
                }

                // 步骤3：检查价格
                await WaitAndSetState(PurchaseState.CheckingPrice, token);
                var cheapest = listings[0];
                OnLog?.Invoke($"最低价: {cheapest.PricePerUnit} Gil x{cheapest.Quantity} (HQ={cheapest.IsHq})");

                if (cheapest.PricePerUnit > _plugin.Configuration.MaxPurchasePrice)
                {
                    OnLog?.Invoke($"价格超出上限: {cheapest.PricePerUnit} > {_plugin.Configuration.MaxPurchasePrice}");
                    CloseAllMarketWindows();
                    return new PurchaseResult { Success = false, ErrorMessage = $"价格超出上限({cheapest.PricePerUnit} > {_plugin.Configuration.MaxPurchasePrice})" };
                }

                // 步骤4：直接发送购买请求包（参考 Daily Routines 的 SendBuyRequest）
                await WaitAndSetState(PurchaseState.ConfirmingPurchase, token);
                OnLog?.Invoke($"发送购买请求: {cheapest.PricePerUnit} Gil x1...");
                if (!PurchaseViaInfoProxy(cheapest))
                {
                    OnLog?.Invoke($"购买请求发送失败 (尝试 {attempt}/{maxRetries})");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(2000, token);
                    }
                    continue;
                }

                // 步骤5：等待购买完成并验证
                await WaitAndSetState(PurchaseState.WaitingForPurchase, token);
                await Task.Delay(2500, token);

                int currentMapCount = GetMapCountInInventory();
                OnLog?.Invoke($"购买后藏宝图数量: {currentMapCount}");

                if (currentMapCount > initialMapCount)
                {
                    State = PurchaseState.Done;
                    OnLog?.Invoke($"购买成功！数量从 {initialMapCount} 增加到 {currentMapCount}");
                    CloseAllMarketWindows();
                    return new PurchaseResult { Success = true, ItemId = TreasureMapConstants.GargantuaskinItemId, Price = (int)cheapest.PricePerUnit };
                }
                else
                {
                    OnLog?.Invoke($"购买后背包数量未增加 (尝试 {attempt}/{maxRetries})");
                    if (attempt < maxRetries)
                    {
                        OnLog?.Invoke("关闭窗口后重试...");
                        CloseAllMarketWindows();
                        await Task.Delay(2000, token);
                    }
                }
            }

            State = PurchaseState.Idle;
            OnLog?.Invoke("购买失败，已达最大重试次数");
            return new PurchaseResult { Success = false, ErrorMessage = "购买失败，已达最大重试次数" };
        }
        catch (OperationCanceledException)
        {
            OnLog?.Invoke("购买已取消");
            return new PurchaseResult { Success = false, ErrorMessage = "已取消" };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"购买异常: {ex.Message}");
            return new PurchaseResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            State = PurchaseState.Idle;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        State = PurchaseState.Idle;
    }

    /// <summary>
    /// 市场上架信息（简化版）
    /// </summary>
    public struct MarketListing
    {
        public uint PricePerUnit;
        public uint Quantity;
        public bool IsHq;
        public ulong RetainerId;
    }

    /// <summary>
    /// 使用 InfoProxyItemSearch 底层 API 搜索物品
    /// 参考 Daily Routines 反编译代码:
    ///   EndRequest() → SearchItemId = itemID → RequestData() → 等待数据
    ///   → Listings 读取上架列表
    /// </summary>
    private async Task<(bool success, List<MarketListing> listings)> SearchViaInfoProxy(CancellationToken token)
    {
        var itemId = _plugin.Configuration.TreasureMapItemId;
        OnLog?.Invoke($"InfoProxy 搜索: {SearchKeywordCN} (ID={itemId})");

        try
        {
            unsafe
            {
                var infoProxy = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch.Instance();
                if (infoProxy == null)
                {
                    OnLog?.Invoke("InfoProxyItemSearch 不可用");
                    return (false, new List<MarketListing>());
                }

                // 设置搜索物品 ID
                infoProxy->SearchItemId = itemId;

                // 通过 AgentItemSearch 触发搜索请求
                var agent = AgentItemSearch.Instance();
                if (agent != null)
                {
                    agent->Show();
                }

                OnLog?.Invoke("搜索请求已发送，等待服务器响应...");
            }

            // 等待数据返回
            var waitStart = DateTime.Now;
            while ((DateTime.Now - waitStart).TotalSeconds < 15)
            {
                token.ThrowIfCancellationRequested();

                unsafe
                {
                    var infoProxy = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch.Instance();
                    if (infoProxy != null && infoProxy->ListingCount > 0)
                    {
                        OnLog?.Invoke($"服务器数据已返回，上架数量: {infoProxy->ListingCount}");
                        break;
                    }
                }

                await Task.Delay(500, token);
            }

            // 读取上架列表
            unsafe
            {
                var infoProxy = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch.Instance();
                if (infoProxy == null || infoProxy->ListingCount == 0)
                {
                    OnLog?.Invoke("等待数据超时或无上架");
                    return (false, new List<MarketListing>());
                }

                // 转换为排序列表（按单价升序，取最低价）
                var result = new List<MarketListing>();
                var listings = infoProxy->Listings;
                for (int i = 0; i < (int)infoProxy->ListingCount && i < listings.Length; i++)
                {
                    var l = listings[i];
                    result.Add(new MarketListing
                    {
                        PricePerUnit = l.UnitPrice,
                        Quantity = l.Quantity,
                        IsHq = l.IsHqItem,
                        RetainerId = l.RetainerId,
                    });
                }

                result.Sort((a, b) => a.PricePerUnit.CompareTo(b.PricePerUnit));
                return (true, result);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"搜索异常: {ex.Message}");
            Plugin.Log.Error($"InfoProxy 搜索异常: {ex}");
            return (false, new List<MarketListing>());
        }
    }

    /// <summary>
    /// 使用 InfoProxyItemSearch 底层 API 购买
    /// 参考 Daily Routines: SetLastPurchasedItem → SendPurchaseRequestPacket
    /// </summary>
    private bool PurchaseViaInfoProxy(MarketListing listing)
    {
        try
        {
            unsafe
            {
                var infoProxy = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch.Instance();
                if (infoProxy == null)
                {
                    OnLog?.Invoke("InfoProxyItemSearch 不可用");
                    return false;
                }

                // 遍历 Listings 找到匹配的上架项
                var listings = infoProxy->Listings;
                for (int i = 0; i < (int)infoProxy->ListingCount && i < listings.Length; i++)
                {
                    var l = listings[i];
                    if (l.UnitPrice == listing.PricePerUnit && l.RetainerId == listing.RetainerId)
                    {
                        OnLog?.Invoke($"匹配到上架项: {l.UnitPrice} Gil x{l.Quantity}");

                        // 使用静态函数指针购买
                        var itemPtr = &l;
                        var setOk = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch.MemberFunctionPointers.SetLastPurchasedItem(infoProxy, itemPtr);
                        if (!setOk)
                        {
                            OnLog?.Invoke("SetLastPurchasedItem 失败");
                            return false;
                        }

                        var sendOk = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch.MemberFunctionPointers.SendPurchaseRequestPacket(infoProxy);
                        OnLog?.Invoke($"购买请求包已发送: {sendOk}");
                        return sendOk;
                    }
                }

                OnLog?.Invoke("未在 Listings 中找到匹配的上架项");
                return false;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"购买异常: {ex.Message}");
            Plugin.Log.Error($"InfoProxy 购买异常: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 打开交易板：寻路到最近的市场布告板 → 交互打开
    /// 参考 AutoDuty 寻路模式重写
    /// </summary>
    private async Task<bool> OpenMarketBoard(CancellationToken token)
    {
        try
        {
            if (IsMarketBoardOpen())
            {
                OnLog?.Invoke("交易板已打开");
                return true;
            }

            // 查找最近的交易板
            var mbObj = FindNearestMarketBoard();

            // 没找到交易板 → 自动传送到主城再重试
            if (mbObj == null)
            {
                OnLog?.Invoke("当前区域无交易板，尝试传送到主城...");

                // 动态获取所有已解锁水晶（含名称），单次刷新列表
                var unlocked = AetheryteHelper.GetUnlockedAetherytesWithNames();
                OnLog?.Invoke($"已解锁水晶数量: {unlocked.Count}");

                // 列出前 5 个已解锁水晶的名称（用于调试）
                for (int i = 0; i < Math.Min(5, unlocked.Count); i++)
                {
                    OnLog?.Invoke($"水晶[{i}]: ID={unlocked[i].aetheryteId} Name={unlocked[i].name} Terr={unlocked[i].territoryId}");
                }

                // 方式1: 使用水晶 ID 直接匹配主城（不受语言/名称格式影响）
                uint teleportTarget = 0;
                string cityName = "";
                int bestPriority = int.MaxValue;

                foreach (var (id, name, _) in unlocked)
                {
                    var priority = AetheryteHelper.GetMainCityPriority(id);
                    if (priority > 0 && priority < bestPriority)
                    {
                        bestPriority = priority;
                        teleportTarget = id;
                        cityName = name;
                    }
                }

                // 方式2: 如果 ID 匹配失败，回退到名称匹配
                if (teleportTarget == 0)
                {
                    OnLog?.Invoke("ID 匹配未找到主城，尝试名称匹配...");
                    var limsaKeywords = new[] { "利姆萨", "Limsa", "リムサ" };
                    var gridaniaKeywords = new[] { "格里达尼亚", "Gridania", "グリダニア" };
                    var uldahKeywords = new[] { "乌尔达哈", "Ul'dah", "Uldah", "ウルダハ" };

                    foreach (var (id, name, _) in unlocked)
                    {
                        if (ContainsAny(name, limsaKeywords))
                        {
                            teleportTarget = id;
                            cityName = name;
                            break;
                        }
                    }

                    if (teleportTarget == 0)
                    {
                        foreach (var (id, name, _) in unlocked)
                        {
                            if (ContainsAny(name, uldahKeywords))
                            {
                                teleportTarget = id;
                                cityName = name;
                                break;
                            }
                        }
                    }

                    if (teleportTarget == 0)
                    {
                        foreach (var (id, name, _) in unlocked)
                        {
                            if (ContainsAny(name, gridaniaKeywords))
                            {
                                teleportTarget = id;
                                cityName = name;
                                break;
                            }
                        }
                    }
                }

                if (teleportTarget == 0)
                {
                    OnLog?.Invoke("未找到已解锁的主城水晶，请手动前往主城");
                    OnLog?.Invoke("调试: 请使用 /thunt debug 查看附近对象，或检查 Dalamud 日志中的水晶列表");
                    return false;
                }

                OnLog?.Invoke($"选定传送目标: {cityName} (水晶ID={teleportTarget})");
                if (!AetheryteHelper.TeleportToAetheryte(teleportTarget))
                {
                    OnLog?.Invoke("传送失败，可能正在冷却中");
                    return false;
                }

                // 等待传送完成（使用 AsyncHelper 统一模式）
                OnLog?.Invoke("等待区域加载...");
                await AsyncHelper.WaitForTeleportCompleteAsync(token, 30000);

                // 额外等待 5 秒让场景对象完全加载
                OnLog?.Invoke("场景加载中，等待对象刷新...");
                await Task.Delay(5000, token);

                // 等待 vnavmesh 网格就绪
                var vnavWaitStart = DateTime.Now;
                while ((DateTime.Now - vnavWaitStart).TotalSeconds < 15)
                {
                    token.ThrowIfCancellationRequested();
                    if (VnavmeshHelper.IsAvailable())
                        break;
                    await Task.Delay(1000, token);
                }

                // 重试查找交易板（对象可能渐进加载）
                IGameObject? board = null;
                var retryStart = DateTime.Now;
                while ((DateTime.Now - retryStart).TotalSeconds < 15)
                {
                    token.ThrowIfCancellationRequested();
                    board = FindNearestMarketBoard();
                    if (board != null) break;
                    OnLog?.Invoke($"查找交易板中... ({(DateTime.Now - retryStart).TotalSeconds:F0}s)");
                    await Task.Delay(2000, token);
                }

                mbObj = board;
                if (mbObj == null)
                {
                    OnLog?.Invoke($"到达 {cityName} 后仍未找到交易板");
                    OnLog?.Invoke("请使用 /thunt debug 查看附近对象列表");
                    return false;
                }
                OnLog?.Invoke($"到达 {cityName}，已找到交易板: {mbObj.Name}");
            }

            var player2 = Plugin.ObjectTable.LocalPlayer;
            if (player2 == null)
            {
                OnLog?.Invoke("无法获取玩家位置");
                return false;
            }

            var distance = Vector3.Distance(player2.Position, mbObj.Position);

            // 如果距离较远，用 vnavmesh 寻路
            if (distance > 3.5f)
            {
                if (!VnavmeshHelper.IsAvailable())
                {
                    OnLog?.Invoke("vnavmesh 不可用，无法自动寻路");
                    return false;
                }

                OnLog?.Invoke($"距离 {distance:F1}m，开始自动寻路...");

                var arrived = await VnavmeshHelper.MoveToAsync(
                    mbObj.Position,
                    tolerance: 2.5f,
                    fly: false,
                    timeoutMs: 60000,
                    token: token);

                if (!arrived)
                {
                    OnLog?.Invoke("寻路未能到达目标，请检查路径是否被阻挡");
                    return false;
                }

                OnLog?.Invoke("已到达交易板附近");
                await Task.Delay(300, token);
            }

            // 到达后，与交易板交互
            OnLog?.Invoke("正在打开交易板...");
            var interacted = InteractWithObject(mbObj);
            if (!interacted)
            {
                OnLog?.Invoke("直接交互失败，尝试通过 Agent 打开...");
                OpenItemSearchAgent();
            }

            // 参考 SND 脚本：交互后先等 3 秒让窗口加载
            await Task.Delay(3000, token);

            // 等待交易板窗口出现（参考 SND：轮询 10 秒，每 0.5 秒检查一次）
            var waitStart2 = DateTime.Now;
            while ((DateTime.Now - waitStart2).TotalSeconds < 10)
            {
                token.ThrowIfCancellationRequested();
                if (IsMarketBoardOpen())
                {
                    OnLog?.Invoke("交易板已打开");
                    return true;
                }
                await Task.Delay(500, token);
            }

            OnLog?.Invoke("等待交易板打开超时");
            return IsMarketBoardOpen();
        }
        catch (OperationCanceledException)
        {
            VnavmeshHelper.Stop();
            OnLog?.Invoke("操作已取消");
            return false;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"打开交易板失败: {ex.Message}");
            Plugin.Log.Error($"打开交易板异常: {ex}");
            return false;
        }
    }

    // 市场布告板 DataId 列表（已知的市场布告板/召唤铃 DataId）
    // 参考：市场布告板约 2000736，召唤铃约 2000735
    // 不同城市可能有不同的 DataId，所以用名称匹配作为主要方式
    private static readonly uint[] MarketBoardDataIds = new uint[]
    {
        2000735, // 召唤铃
        2000736, // 市场布告板
    };

    /// <summary>
    /// 查找最近的市场布告板对象
    /// 优先用 DataId 匹配，其次用名称匹配
    /// </summary>
    private IGameObject? FindNearestMarketBoard()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        IGameObject? nearest = null;
        var minDistance = float.MaxValue;

        // 加载 EventObj Excel 表用于名称查找
        // Lumina 中 EventObj 类型可能不存在，改为用 DataId + Name 双重检测

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.ObjectKind != ObjectKind.EventObj &&
                obj.ObjectKind != ObjectKind.EventNpc) continue;

            uint dataId = GetDataId(obj);
            bool isMarketBoard = false;

            // 1. 检查已知 DataId
            foreach (var id in MarketBoardDataIds)
            {
                if (dataId == id) { isMarketBoard = true; break; }
            }

            // 2. 检查 obj.Name
            if (!isMarketBoard)
            {
                var name = obj.Name.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    foreach (var kw in MarketBoardKeywords)
                    {
                        if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        { isMarketBoard = true; break; }
                    }
                }
            }

            // 3. 如果 Name 为空，记录 DataId 供调试
            if (!isMarketBoard && dataId > 0)
            {
                Plugin.Log.Debug($"EventObj DataId={dataId} Name=\"{obj.Name}\" Kind={obj.ObjectKind}");
            }

            if (!isMarketBoard) continue;

            var dist = Vector3.Distance(player.Position, obj.Position);
            if (dist < minDistance)
            {
                minDistance = dist;
                nearest = obj;
            }
        }

        return nearest;
    }

    /// <summary>
    /// 获取游戏对象的 DataId（BaseId）
    /// </summary>
    private static unsafe uint GetDataId(IGameObject obj)
    {
        try
        {
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            return go->BaseId;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 调试：列出附近所有 EventObj（用于诊断找不到交易板的问题）
    /// </summary>
    public List<(string name, uint dataId, float distance, ObjectKind kind)> GetNearbyObjectsDebug()
    {
        var result = new List<(string, uint, float, ObjectKind)>();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return result;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;

            var name = obj.Name.ToString();
            var dist = Vector3.Distance(player.Position, obj.Position);

            // 只列出 100 米内的对象
            if (dist > 100f) continue;

            var dataId = GetDataId(obj);
            result.Add((name, dataId, dist, obj.ObjectKind));
        }

        result.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        return result;
    }

    /// <summary>
    /// 检查交易板是否已打开
    /// </summary>
    /// <summary>
    /// 检查交易板是否已打开
    /// 参考 SND 脚本：Addons.GetAddon("ItemSearch").Exists
    /// 检查 addon 是否存在（Address != Zero），不严格要求 IsVisible
    /// 因为窗口刚加载时 IsVisible 可能为 false，但 addon 已经存在
    /// </summary>
    private bool IsMarketBoardOpen()
    {
        var addon = Plugin.GameGui.GetAddonByName("ItemSearch");
        if (addon.Address == IntPtr.Zero) return false;
        // addon 已加载即可视为打开（与 SND 脚本 .Exists 一致）
        // 不检查 IsVisible，因为窗口加载动画期间 IsVisible 可能为 false
        return true;
    }

    /// <summary>
    /// 与游戏对象交互
    /// 使用 ECommons 的 GameObjectHelper（经过验证的标准实现）
    /// </summary>
    private bool InteractWithObject(IGameObject obj)
    {
        try
        {
            // 先设为目标
            GameObjectHelper.SetTarget(obj);

            // 然后交互
            return GameObjectHelper.InteractWithObject(obj);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"与对象交互失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 直接打开 ItemSearch Agent (回退方案)
    /// </summary>
    private void OpenItemSearchAgent()
    {
        unsafe
        {
            var agentInterface = AgentModule.Instance();
            if (agentInterface != null)
            {
                var itemSearchAgent = agentInterface->GetAgentByInternalId(AgentId.ItemSearch);
                if (itemSearchAgent != null)
                {
                    itemSearchAgent->Show();
                }
            }
        }
    }

    /// <summary>
    /// 搜索藏宝图（参考 SND 脚本完整流程）
    /// SND: /callback ItemSearch true 9 false false 陈旧的卡冈图亚革地图 46185 false false false
    /// 流程：callback 9 搜索 → 等3秒 → callback 5 轮询等待 ItemSearchResult
    /// </summary>
    private async Task<bool> SearchForMap(CancellationToken token)
    {
        try
        {
            var itemId = _plugin.Configuration.TreasureMapItemId;
            OnLog?.Invoke($"搜索藏宝图: {SearchKeywordCN} (ID={itemId})");

            // 通过 callback 9 搜索（带物品名称和 ID）
            // 参考 SND: /callback ItemSearch true 9 false false 陈旧的卡冈图亚革地图 46185 false false false
            // 参数：[isHQ=false, isExact=false, itemName, itemId(数字), unknown=false, unknown=false, unknown=false]
            unsafe
            {
                var addon = GetItemSearchAddonPtr();
                if (addon == null)
                {
                    OnLog?.Invoke("ItemSearch addon 不存在");
                    return false;
                }

                var atkValues = stackalloc AtkValue[7];
                atkValues[0].Type = AtkValueType.Bool;
                atkValues[0].Byte = 0; // isHQ = false
                atkValues[1].Type = AtkValueType.Bool;
                atkValues[1].Byte = 0; // isExact = false (部分一致)

                // 物品名称（字符串）
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(SearchKeywordCN + "\0");
                fixed (byte* pName = nameBytes)
                {
                    atkValues[2].Type = AtkValueType.String;
                    atkValues[2].String = (InteropGenerator.Runtime.CStringPointer)pName;

                    // 物品 ID — SND 脚本传的是数字 46185
                    atkValues[3].Type = AtkValueType.Int;
                    atkValues[3].Int = (int)itemId;

                    atkValues[4].Type = AtkValueType.Bool;
                    atkValues[4].Byte = 0;
                    atkValues[5].Type = AtkValueType.Bool;
                    atkValues[5].Byte = 0;
                    atkValues[6].Type = AtkValueType.Bool;
                    atkValues[6].Byte = 0;

                    addon->FireCallback(9, atkValues, true);
                }
            }

            OnLog?.Invoke("搜索指令已发送，等待3秒...");

            // 等待搜索结果加载（SND 脚本：yield("/wait 3")）
            await Task.Delay(3000, token);

            // 轮询等待 ItemSearchResult 窗口出现
            // 参考 SND: while not ItemSearchResult.Exists do /callback ItemSearch true 5 0/ /wait 0.5/ end
            // callback 5 = 选择/点击第一个搜索结果，会打开 ItemSearchResult 窗口
            var waitStart = DateTime.Now;
            var pollCount = 0;
            while ((DateTime.Now - waitStart).TotalSeconds < 10)
            {
                token.ThrowIfCancellationRequested();

                if (IsItemSearchResultOpen())
                {
                    OnLog?.Invoke($"搜索结果窗口已打开（轮询{pollCount}次）");
                    return true;
                }

                // 发送 callback 5 选择第一个结果（会触发 ItemSearchResult 窗口打开）
                pollCount++;
                unsafe
                {
                    var addon = GetItemSearchAddonPtr();
                    if (addon != null)
                    {
                        var pollValues = stackalloc AtkValue[1];
                        pollValues[0].Type = AtkValueType.Int;
                        pollValues[0].Int = 0; // 选择第一个结果
                        addon->FireCallback(5, pollValues, true);
                    }
                }

                await Task.Delay(500, token);
            }

            OnLog?.Invoke($"等待搜索结果超时（轮询{pollCount}次）");
            return IsItemSearchResultOpen();
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"搜索失败: {ex.Message}");
            Plugin.Log.Error($"搜索异常: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 选择第一个搜索结果
    /// 搜索阶段已通过 callback 5 打开 ItemSearchResult 窗口
    /// 这里只需确认 ItemSearchResult 已打开，并等待数据加载
    /// </summary>
    private async Task<bool> SelectSearchResult(CancellationToken token)
    {
        try
        {
            if (!IsItemSearchResultOpen())
            {
                // 如果还没打开，再尝试 callback 5
                unsafe
                {
                    var addon = GetItemSearchAddonPtr();
                    if (addon == null)
                    {
                        OnLog?.Invoke("ItemSearch addon 不存在");
                        return false;
                    }

                    var atkValues = stackalloc AtkValue[1];
                    atkValues[0].Type = AtkValueType.Int;
                    atkValues[0].Int = 0;

                    addon->FireCallback(5, atkValues, true);
                }

                await Task.Delay(2000, token);

                if (!IsItemSearchResultOpen())
                {
                    OnLog?.Invoke("ItemSearchResult 窗口未打开");
                    return false;
                }
            }

            OnLog?.Invoke("搜索结果窗口已确认打开，等待数据加载...");
            await Task.Delay(1500, token);
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"选择结果失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查价格（从 ItemSearchResult 读取最低价）
    /// </summary>
    private async Task<(bool valid, int price)> CheckPrice(CancellationToken token)
    {
        await Task.Delay(500, token);

        // 先尝试从 InfoProxyItemSearch 读取（最准确）
        var proxyPrice = PdrMarketHelper.GetListingPrice(0);
        if (proxyPrice > 0)
        {
            OnLog?.Invoke($"当前最低价格 (InfoProxy): {proxyPrice}");
            if (proxyPrice > _plugin.Configuration.MaxPurchasePrice)
                return (false, (int)proxyPrice);
            return (true, (int)proxyPrice);
        }

        // 回退：从 UI 文本读取
        var price = ReadLowestPriceFromResult();
        OnLog?.Invoke($"当前最低价格 (UI): {price}");

        if (price == 0)
        {
            OnLog?.Invoke("未能读取价格，可能搜索无结果");
            return (false, 0);
        }

        if (price > _plugin.Configuration.MaxPurchasePrice)
        {
            return (false, price);
        }
        return (true, price);
    }

    /// <summary>
    /// 确认购买（参考 SND：/callback ItemSearchResult true 2 0 + /callback SelectYesno true 0）
    /// 注意：购买按钮在 ItemSearchResult addon 上，不是 ItemSearch！
    /// callback 2 = 购买按钮，参数 0 = 第一个列表项
    /// 改进：等待 SelectYesno 弹窗出现后再确认（避免弹窗还没出来就点击）
    /// </summary>
    private async Task<bool> ConfirmPurchase(CancellationToken token)
    {
        try
        {
            OnLog?.Invoke("点击购买按钮 (ItemSearchResult)...");

            unsafe
            {
                var resultAddon = GetItemSearchResultAddonPtr();
                if (resultAddon == null)
                {
                    OnLog?.Invoke("ItemSearchResult addon 不存在，无法购买");
                    return false;
                }

                // 点击购买按钮（ItemSearchResult callback 2）
                var atkValues = stackalloc AtkValue[1];
                atkValues[0].Type = AtkValueType.Int;
                atkValues[0].Int = 0; // 第一个列表项

                resultAddon->FireCallback(2, atkValues, true); // isEventBubbled = true
            }

            // 等待 SelectYesno 弹窗出现（SND 脚本等 3 秒）
            OnLog?.Invoke("等待确认购买弹窗...");
            var yesnoWaitStart = DateTime.Now;
            while ((DateTime.Now - yesnoWaitStart).TotalSeconds < 3)
            {
                token.ThrowIfCancellationRequested();
                if (IsSelectYesnoOpen())
                {
                    OnLog?.Invoke("确认购买弹窗已出现");
                    break;
                }
                await Task.Delay(200, token);
            }

            // 确认购买 (SelectYesno callback 0 = 是)
            if (IsSelectYesnoOpen())
            {
                OnLog?.Invoke("确认购买...");
                ConfirmPurchaseDialog();
                await Task.Delay(1500, token);
                return true;
            }
            else
            {
                OnLog?.Invoke("未出现确认购买弹窗，可能自动购买成功或购买失败");
                // 即使没有弹窗也返回 true，让后续背包验证来判断是否成功
                await Task.Delay(1000, token);
                return true;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"确认购买失败: {ex.Message}");
            Plugin.Log.Error($"确认购买异常: {ex}");
            return false;
        }
    }

    // === 以下是 unsafe 辅助方法，避免在 async 方法中直接使用 unsafe ===

    private unsafe AtkUnitBase* GetItemSearchAddonPtr()
    {
        var addon = Plugin.GameGui.GetAddonByName("ItemSearch");
        if (addon.Address == IntPtr.Zero) return null;
        return (AtkUnitBase*)addon.Address;
    }

    /// <summary>
    /// 获取 ItemSearchResult addon 指针（搜索结果窗口）
    /// </summary>
    private unsafe AtkUnitBase* GetItemSearchResultAddonPtr()
    {
        var addon = Plugin.GameGui.GetAddonByName("ItemSearchResult");
        if (addon.Address == IntPtr.Zero) return null;
        return (AtkUnitBase*)addon.Address;
    }

    /// <summary>
    /// 检查搜索结果窗口是否已打开
    /// 参考 SND 脚本：Addons.GetAddon("ItemSearchResult").Exists
    /// 检查 addon 是否存在，不严格要求 IsVisible
    /// </summary>
    private bool IsItemSearchResultOpen()
    {
        var addon = Plugin.GameGui.GetAddonByName("ItemSearchResult");
        if (addon.Address == IntPtr.Zero) return false;
        // addon 已加载即可视为打开（与 SND 脚本 .Exists 一致）
        return true;
    }

    /// <summary>
    /// 检查 SelectYesno 确认弹窗是否已打开
    /// 参考 SND 脚本：Addons.GetAddon("SelectYesno").Exists
    /// </summary>
    private bool IsSelectYesnoOpen()
    {
        var addon = Plugin.GameGui.GetAddonByName("SelectYesno");
        if (addon.Address == IntPtr.Zero) return false;
        // addon 已加载即可视为打开（与 SND 脚本 .Exists 一致）
        return true;
    }

    private bool SetSearchTextSafe(string text)
    {
        unsafe
        {
            var atkUnitBase = GetItemSearchAddonPtr();
            if (atkUnitBase == null) return false;

            var uldManager = &atkUnitBase->UldManager;
            for (var i = 0; i < uldManager->NodeListCount; i++)
            {
                var node = uldManager->NodeList[i];
                if (node == null) continue;

                var textInput = node->GetAsAtkComponentTextInput();
                if (textInput == null) continue;

                var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
                fixed (byte* pText = bytes)
                {
                    textInput->SetText((InteropGenerator.Runtime.CStringPointer)pText);
                }
                OnLog?.Invoke($"设置搜索文本: {text}");
                return true;
            }
            OnLog?.Invoke("未找到搜索输入框组件");
            return false;
        }
    }

    private void FireItemSearchCallback(uint callbackIndex, int value = 0)
    {
        unsafe
        {
            var atkUnitBase = GetItemSearchAddonPtr();
            if (atkUnitBase == null) return;

            var atkValues = stackalloc AtkValue[2];
            atkValues[0].Type = AtkValueType.Int;
            atkValues[0].Int = value;
            atkValues[1].Type = AtkValueType.Int;
            atkValues[1].Int = 0;

            atkUnitBase->FireCallback(callbackIndex, atkValues, false);
        }
    }

    /// <summary>
    /// 从 ItemSearchResult 窗口读取最低价格
    /// </summary>
    private int ReadLowestPriceFromResult()
    {
        unsafe
        {
            var atkUnitBase = GetItemSearchResultAddonPtr();
            if (atkUnitBase == null) return 0;

            var prices = new List<int>();
            var uldManager = &atkUnitBase->UldManager;

            for (var i = 0; i < uldManager->NodeListCount; i++)
            {
                var node = uldManager->NodeList[i];
                if (node == null) continue;
                if ((uint)node->Type != (uint)NodeType.Text) continue;

                var textNode = (AtkTextNode*)node;
                var text = textNode->NodeText.ToString();
                if (text.Length == 0) continue;

                // 价格格式：纯数字、带逗号、带 g/G 后缀
                var clean = text.Replace(",", "").Replace(".", "").Replace(" ", "")
                               .Replace("g", "").Replace("G", "").Replace("Ｇ", "");

                if (clean.Length > 0 && long.TryParse(clean, out var price))
                {
                    if (price > 0 && price < 100000000) // 合理范围
                        prices.Add((int)price);
                }
            }

            if (prices.Count == 0) return 0;
            prices.Sort();
            return prices[0]; // 返回最低价
        }
    }

    private void ConfirmPurchaseDialog()
    {
        unsafe
        {
            // 确认 SelectYesno 弹窗 (点击"是")
            // 参考 SND: /callback SelectYesno true 0
            var addon = Plugin.GameGui.GetAddonByName("SelectYesno");
            if (addon.Address == IntPtr.Zero)
            {
                OnLog?.Invoke("未找到确认弹窗");
                return;
            }

            var selectYesno = (AtkUnitBase*)addon.Address;
            var atkValues = stackalloc AtkValue[1];
            atkValues[0].Type = AtkValueType.Int;
            atkValues[0].Int = 0; // 0 = 是 (Yes)
            selectYesno->FireCallback(0, atkValues, true); // isEventBubbled = true
            OnLog?.Invoke("确认购买 (SelectYesno)");
        }
    }

    /// <summary>
    /// 关闭所有市场相关窗口（重试前清理）
    /// </summary>
    /// <summary>
    /// 关闭所有市场相关窗口
    /// 参考 SND 脚本：用 callback -1 正确关闭窗口（而不是 Hide）
    /// SND: /callback ItemSearchResult true -1, /callback ItemSearch true -1, /callback SelectYesno true 1
    /// </summary>
    private void CloseAllMarketWindows()
    {
        unsafe
        {
            // 关闭 ItemSearchResult（用 callback -1，与 SND 脚本一致）
            // 不检查 IsVisible，只要 addon 存在就关闭（PDR 可能留下不可见的残留窗口）
            var resultAddon = GetItemSearchResultAddonPtr();
            if (resultAddon != null)
            {
                resultAddon->FireCallback(unchecked((uint)-1), null, true);
                Plugin.Log.Debug("已关闭 ItemSearchResult (callback -1)");
            }

            // 关闭 ItemSearch（用 callback -1）
            var searchAddon = GetItemSearchAddonPtr();
            if (searchAddon != null)
            {
                searchAddon->FireCallback(unchecked((uint)-1), null, true);
                Plugin.Log.Debug("已关闭 ItemSearch (callback -1)");
            }

            // 关闭 SelectYesno（用 callback 1 = 选择"否"，与 SND 脚本一致）
            var yesnoAddon = Plugin.GameGui.GetAddonByName("SelectYesno");
            if (yesnoAddon.Address != IntPtr.Zero)
            {
                var atk = (AtkUnitBase*)yesnoAddon.Address;
                atk->FireCallback(1, null, true);
                Plugin.Log.Debug("已关闭 SelectYesno (callback 1)");
            }
        }

        // 清除目标
        try
        {
            Plugin.TargetManager.Target = null;
        }
        catch { }
    }

    /// <summary>
    /// 统计背包中藏宝图的总数量（所有背包页）
    /// 参考 SND 脚本：Inventory.GetItemCount
    /// </summary>
    private int GetMapCountInInventory()
    {
        try
        {
            var itemId = _plugin.Configuration.TreasureMapItemId;
            unsafe
            {
                var invManager = InventoryManager.Instance();
                if (invManager == null) return 0;

                int count = 0;
                // 检查 4 个背包页: Inventory1 ~ Inventory4
                var inventoryTypes = new[]
                {
                    InventoryType.Inventory1,
                    InventoryType.Inventory2,
                    InventoryType.Inventory3,
                    InventoryType.Inventory4
                };
                foreach (var invType in inventoryTypes)
                {
                    var inventory = invManager->GetInventoryContainer(invType);
                    if (inventory == null) continue;

                    for (var i = 0; i < inventory->Size; i++)
                    {
                        var invItem = inventory->GetInventorySlot(i);
                        if (invItem->ItemId == itemId)
                        {
                            count += invItem->Quantity;
                        }
                    }
                }
                return count;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"统计藏宝图数量失败: {ex.Message}");
            return 0;
        }
    }

    private async Task WaitAndSetState(PurchaseState state, CancellationToken token)
    {
        var elapsed = (DateTime.Now - _lastActionTime).TotalMilliseconds;
        if (elapsed < ActionCooldownMs)
            await Task.Delay((int)(ActionCooldownMs - elapsed), token);

        _lastActionTime = DateTime.Now;
        State = state;
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        Cancel();
    }
}
