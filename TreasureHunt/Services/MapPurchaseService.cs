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

            // 优先使用 PDR 远程交易板（无需跑到主城）
            var usePdr = _plugin.Configuration.UsePdrMarket;
            OnLog?.Invoke($"PDR 模式: 配置启用={usePdr}，将尝试 /pdr market 命令");

            if (usePdr)
            {
                OnLog?.Invoke("使用 PDR 远程交易板购买...");
                var pdrResult = await PurchaseMapViaPdr(token);
                if (pdrResult.Success)
                {
                    return pdrResult;
                }
                OnLog?.Invoke($"PDR 购买失败: {pdrResult.ErrorMessage}，回退到传统方式...");
                usePdr = false;
            }

            // 传统方式：原生交易板 UI 操作
            if (!usePdr)
            {
                if (!await OpenMarketBoard(token))
                    return new PurchaseResult { Success = false, ErrorMessage = "无法打开交易板" };

                await WaitAndSetState(PurchaseState.SearchingItem, token);
                if (!await SearchForMap(token))
                    return new PurchaseResult { Success = false, ErrorMessage = "搜索藏宝图失败" };

                await WaitAndSetState(PurchaseState.SelectingResult, token);
                if (!await SelectSearchResult(token))
                    return new PurchaseResult { Success = false, ErrorMessage = "选择搜索结果失败" };

                await WaitAndSetState(PurchaseState.CheckingPrice, token);
                var (valid, price) = await CheckPrice(token);
                if (!valid)
                    return new PurchaseResult { Success = false, ErrorMessage = $"价格超出上限({price} > {_plugin.Configuration.MaxPurchasePrice})" };

                await WaitAndSetState(PurchaseState.ConfirmingPurchase, token);
                if (!await ConfirmPurchase(token))
                    return new PurchaseResult { Success = false, ErrorMessage = "确认购买失败" };

                await WaitAndSetState(PurchaseState.WaitingForPurchase, token);
                await Task.Delay(2000, token);

                State = PurchaseState.Done;
                OnLog?.Invoke($"购买完成，价格: {price}");
                return new PurchaseResult { Success = true, ItemId = TreasureMapConstants.GargantuaskinItemId, Price = price };
            }

            return new PurchaseResult { Success = false, ErrorMessage = "未知错误" };
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

    /// <summary>
    /// 通过 PDR + InfoProxyItemSearch 直接购买（无需操作原生 UI）
    /// PDR 打开 ImGui 窗口并搜索物品后，我们直接通过底层 API 读取列表并购买
    /// </summary>
    private async Task<PurchaseResult> PurchaseMapViaPdr(CancellationToken token)
    {
        try
        {
            var itemId = _plugin.Configuration.TreasureMapItemId;
            var maxPrice = _plugin.Configuration.MaxPurchasePrice;

            // 1. 打开 PDR 市场并搜索物品
            OnLog?.Invoke($"执行 /pdr market {itemId} ...");
            PdrMarketHelper.OpenMarket(itemId);

            // 2. 等待搜索结果加载（PDR 建立连接 + 获取列表数据）
            State = PurchaseState.SearchingItem;
            OnLog?.Invoke("等待 PDR 搜索结果加载...");

            var debugInfoBefore = PdrMarketHelper.GetDebugInfo();
            Plugin.Log.Debug($"PDR 打开前状态:\n{debugInfoBefore}");

            var resultsLoaded = await PdrMarketHelper.WaitForSearchResults(12000, token);

            var debugInfoAfter = PdrMarketHelper.GetDebugInfo();
            Plugin.Log.Debug($"PDR 等待后状态:\n{debugInfoAfter}");

            if (!resultsLoaded)
            {
                var count = PdrMarketHelper.GetListingCount();
                OnLog?.Invoke($"搜索结果加载超时，当前列表数: {count}");
                OnLog?.Invoke("可能原因：PDR 未正确连接市场，或物品名称/ID有误");
                return new PurchaseResult { Success = false, ErrorMessage = "搜索结果加载超时" };
            }

            var listingCount = PdrMarketHelper.GetListingCount();
            OnLog?.Invoke($"搜索完成，找到 {listingCount} 个结果");

            // 3. 检查价格
            State = PurchaseState.CheckingPrice;
            var (price, quantity, resultItemId, isValid) = PdrMarketHelper.GetFirstListing();

            if (!isValid)
            {
                OnLog?.Invoke("无法获取第一个商品信息");
                return new PurchaseResult { Success = false, ErrorMessage = "无法获取商品信息" };
            }

            OnLog?.Invoke($"当前最低价格: {price} (数量: {quantity})");

            if (price > maxPrice)
            {
                OnLog?.Invoke($"价格超出上限: {price} > {maxPrice}");
                return new PurchaseResult { Success = false, ErrorMessage = $"价格超出上限({price} > {maxPrice})" };
            }

            // 4. 直接购买（通过 InfoProxyItemSearch API）
            State = PurchaseState.ConfirmingPurchase;
            OnLog?.Invoke($"正在购买，价格: {price} ...");

            bool purchaseOk = PdrMarketHelper.PurchaseFirstListing((uint)maxPrice);

            if (!purchaseOk)
            {
                OnLog?.Invoke("购买请求发送失败");
                return new PurchaseResult { Success = false, ErrorMessage = "购买请求发送失败" };
            }

            // 5. 等待购买完成
            State = PurchaseState.WaitingForPurchase;
            OnLog?.Invoke("购买请求已发送，等待交易完成...");
            await Task.Delay(2500, token);

            State = PurchaseState.Done;
            OnLog?.Invoke($"购买完成，价格: {price}");
            return new PurchaseResult { Success = true, ItemId = resultItemId, Price = (int)price };
        }
        catch (OperationCanceledException)
        {
            return new PurchaseResult { Success = false, ErrorMessage = "已取消" };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"PDR 购买异常: {ex.Message}");
            Plugin.Log.Error($"PDR 购买异常: {ex}");
            return new PurchaseResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        State = PurchaseState.Idle;
    }

    /// <summary>
    /// 使用 PDR (更好的市场布告板) 打开远程交易板
    /// 通过 /pdr market &lt;物品ID&gt; 直接打开，无需跑到主城
    /// PDR 是 ImGui 窗口，检测不到原生 ItemSearch addon
    /// 但 PDR 可能已激活了 ItemSearch Agent，我们尝试用原生 API 购买
    /// </summary>
    private async Task<bool> OpenMarketBoardPdr(CancellationToken token)
    {
        try
        {
            if (IsMarketBoardOpen())
            {
                OnLog?.Invoke("交易板已打开");
                return true;
            }

            var itemId = _plugin.Configuration.TreasureMapItemId;
            OnLog?.Invoke($"执行 /pdr market {itemId} ...");

            PdrMarketHelper.OpenMarket(itemId);

            // 等待 PDR 加载（PDR是ImGui窗口，等它建立市场连接）
            await Task.Delay(2000, token);

            // 尝试激活原生 ItemSearch Agent
            // PDR 建立远程连接后，ItemSearch Agent 应该可以工作了
            OnLog?.Invoke("尝试激活原生 ItemSearch Agent...");
            OpenItemSearchAgent();
            await Task.Delay(1500, token);

            if (IsMarketBoardOpen())
            {
                OnLog?.Invoke("原生交易板已打开（PDR 远程连接已建立）");
                return true;
            }

            // 如果原生窗口没打开，但 Agent 可能已激活，还是可以继续尝试搜索购买
            OnLog?.Invoke("原生窗口未显示，但继续尝试使用 ItemSearch Agent");
            return true; // 继续，后续步骤尝试直接用 Agent API
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"PDR 打开交易板失败: {ex.Message}");
            Plugin.Log.Error($"PDR 打开交易板异常: {ex}");
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

                // 调试：列出前 10 个已解锁水晶的名称
                for (int i = 0; i < Math.Min(10, unlocked.Count); i++)
                {
                    Plugin.Log.Debug($"水晶[{i}]: ID={unlocked[i].aetheryteId} Name={unlocked[i].name} Terr={unlocked[i].territoryId}");
                }

                uint teleportTarget = 0;
                string cityName = "";
                // 第一优先：利姆萨·罗敏萨下层甲板（海都，交易板最近）
                var limsaKeywords = new[] { "利姆萨", "Limsa" };
                var gridaniaKeywords = new[] { "格里达尼亚", "Gridania" };
                var uldahKeywords = new[] { "乌尔达哈", "Ul'dah", "Uldah" };

                // 优先1：利姆萨下层甲板（精确匹配关键词组合）
                foreach (var (id, name, _) in unlocked)
                {
                    if (ContainsAny(name, limsaKeywords) && name.Contains("下层", StringComparison.OrdinalIgnoreCase))
                    {
                        teleportTarget = id;
                        cityName = name;
                        break;
                    }
                }

                // 优先2：利姆萨任意区域
                if (teleportTarget == 0)
                {
                    foreach (var (id, name, _) in unlocked)
                    {
                        if (ContainsAny(name, limsaKeywords))
                        {
                            teleportTarget = id;
                            cityName = name;
                            break;
                        }
                    }
                }

                // 优先3：乌尔达哈现世回廊（交易板近）
                if (teleportTarget == 0)
                {
                    foreach (var (id, name, _) in unlocked)
                    {
                        if (ContainsAny(name, uldahKeywords) && name.Contains("现世", StringComparison.OrdinalIgnoreCase))
                        {
                            teleportTarget = id;
                            cityName = name;
                            break;
                        }
                    }
                }

                // 优先4：乌尔达哈任意区域
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

                // 优先5：格里达尼亚旧街（交易板近）
                if (teleportTarget == 0)
                {
                    foreach (var (id, name, _) in unlocked)
                    {
                        if (ContainsAny(name, gridaniaKeywords) && name.Contains("旧", StringComparison.OrdinalIgnoreCase))
                        {
                            teleportTarget = id;
                            cityName = name;
                            break;
                        }
                    }
                }

                // 优先6：格里达尼亚任意区域
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

                // 等待传送完成（分阶段）
                OnLog?.Invoke("等待区域加载...");

                // 阶段1: 等待加载画面出现（传送后会有黑屏）
                var phase1Start = DateTime.Now;
                var loadingStarted = false;
                while ((DateTime.Now - phase1Start).TotalSeconds < 15)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(300, token);
                    if (Plugin.Condition[ConditionFlag.BetweenAreas] ||
                        Plugin.Condition[ConditionFlag.BetweenAreas51])
                    {
                        loadingStarted = true;
                        break;
                    }
                }

                if (!loadingStarted)
                {
                    // 没检测到加载画面，可能已经在目标区域或传送失败
                    OnLog?.Invoke("未检测到加载画面，继续等待...");
                }

                // 阶段2: 等待加载画面消失
                var phase2Start = DateTime.Now;
                while ((DateTime.Now - phase2Start).TotalSeconds < 30)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(500, token);
                    if (!Plugin.Condition[ConditionFlag.BetweenAreas] &&
                        !Plugin.Condition[ConditionFlag.BetweenAreas51])
                    {
                        break;
                    }
                }

                // 阶段3: 等待玩家对象加载
                var phase3Start = DateTime.Now;
                while ((DateTime.Now - phase3Start).TotalSeconds < 10)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(500, token);
                    if (Plugin.ObjectTable.LocalPlayer != null)
                        break;
                }

                // 阶段4: 额外等待 5 秒让场景对象完全加载
                OnLog?.Invoke("场景加载中，等待对象刷新...");
                await Task.Delay(5000, token);

                // 阶段5: 等待 vnavmesh 网格就绪
                var vnavWaitStart = DateTime.Now;
                while ((DateTime.Now - vnavWaitStart).TotalSeconds < 15)
                {
                    token.ThrowIfCancellationRequested();
                    if (VnavmeshHelper.IsAvailable())
                        break;
                    await Task.Delay(1000, token);
                }

                // 阶段6: 重试查找交易板（对象可能渐进加载）
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

            // 等待交易板窗口出现
            var waitStart2 = DateTime.Now;
            while ((DateTime.Now - waitStart2).TotalSeconds < 5)
            {
                token.ThrowIfCancellationRequested();
                if (IsMarketBoardOpen())
                {
                    OnLog?.Invoke("交易板已打开");
                    return true;
                }
                await Task.Delay(200, token);
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
    private bool IsMarketBoardOpen()
    {
        var addon = Plugin.GameGui.GetAddonByName("ItemSearch");
        if (addon.Address == IntPtr.Zero) return false;
        unsafe
        {
            var atkUnitBase = (AtkUnitBase*)addon.Address;
            return atkUnitBase->IsVisible;
        }
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

    private async Task<bool> SearchForMap(CancellationToken token)
    {
        try
        {
            var searchKeyword = SearchKeywordCN;
            OnLog?.Invoke($"搜索: {searchKeyword}");

            // 设置搜索文本
            var setTextOk = SetSearchTextSafe(searchKeyword);
            if (!setTextOk)
            {
                OnLog?.Invoke("设置搜索文本失败");
            }

            await Task.Delay(500, token);

            // 触发搜索
            FireItemSearchCallback(0);
            OnLog?.Invoke("执行搜索");

            await Task.Delay(1500, token);
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"搜索失败: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> SelectSearchResult(CancellationToken token)
    {
        try
        {
            // 选择第一个搜索结果 (callback index 1)
            FireItemSearchCallback(1, 0);
            OnLog?.Invoke("选择第一个搜索结果");

            await Task.Delay(1000, token);
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"选择搜索结果失败: {ex.Message}");
            return false;
        }
    }

    private async Task<(bool valid, int price)> CheckPrice(CancellationToken token)
    {
        await Task.Delay(500, token);

        var price = ReadLowestPriceSafe();
        OnLog?.Invoke($"当前最低价格: {price}");

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

    private async Task<bool> ConfirmPurchase(CancellationToken token)
    {
        try
        {
            // 点击购买按钮 (callback index 2)
            FireItemSearchCallback(2, 0);
            OnLog?.Invoke("点击购买按钮");

            await Task.Delay(800, token);

            // 确认购买弹窗 (SelectYesno)
            ConfirmPurchaseDialog();
            await Task.Delay(800, token);

            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"确认购买失败: {ex.Message}");
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

    private int ReadLowestPriceSafe()
    {
        unsafe
        {
            var atkUnitBase = GetItemSearchAddonPtr();
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
            var addon = Plugin.GameGui.GetAddonByName("SelectYesno");
            if (addon.Address == IntPtr.Zero)
            {
                OnLog?.Invoke("未找到确认弹窗");
                return;
            }

            var selectYesno = (AtkUnitBase*)addon.Address;
            var atkValues = stackalloc AtkValue[1];
            atkValues[0].Type = AtkValueType.Bool;
            atkValues[0].Byte = 1; // true = Yes
            selectYesno->FireCallback(0, atkValues, false);
            OnLog?.Invoke("确认购买");
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
