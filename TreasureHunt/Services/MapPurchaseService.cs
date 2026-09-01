using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
    /// 打开交易板：寻路到最近的市场布告板 → 交互打开
    /// 参考 AutoDuty 寻路模式重写
    /// </summary>
    private async Task<bool> OpenMarketBoard(CancellationToken token)
    {
        try
        {
            // 检查交易板是否已打开
            if (IsMarketBoardOpen())
            {
                OnLog?.Invoke("交易板已打开");
                return true;
            }

            // 查找最近的交易板
            var mbObj = FindNearestMarketBoard();
            if (mbObj == null)
            {
                OnLog?.Invoke("当前区域未找到交易板，请前往主城使用");
                OnLog?.Invoke("提示：如在主城仍找不到，请使用 /thunt debug 查看附近对象列表");
                return false;
            }

            OnLog?.Invoke($"找到交易板: {mbObj.Name} (距离 {Vector3.Distance(Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero, mbObj.Position):F1}m)");

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                OnLog?.Invoke("无法获取玩家位置");
                return false;
            }

            var distance = Vector3.Distance(player.Position, mbObj.Position);

            // 如果距离较远，用 vnavmesh 寻路
            if (distance > 3.5f)
            {
                if (!VnavmeshHelper.IsAvailable())
                {
                    OnLog?.Invoke("vnavmesh 不可用，无法自动寻路");
                    return false;
                }

                OnLog?.Invoke($"距离 {distance:F1}m，开始自动寻路...");

                // 使用新的 MoveToAsync（参考 AutoDuty 实现）
                // 容差设为 2.5 米，确保在交互范围内
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
            var waitStart = DateTime.Now;
            while ((DateTime.Now - waitStart).TotalSeconds < 5)
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

        // 第一轮：用 DataId 精确查找（市场布告板是 EventObj）
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.ObjectKind != ObjectKind.EventObj) continue;

            uint dataId = GetDataId(obj);
            bool isMarketBoard = false;

            // 检查 DataId
            foreach (var id in MarketBoardDataIds)
            {
                if (dataId == id)
                {
                    isMarketBoard = true;
                    break;
                }
            }

            // 检查名称（作为补充）
            if (!isMarketBoard)
            {
                var name = obj.Name.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    foreach (var keyword in MarketBoardKeywords)
                    {
                        if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            isMarketBoard = true;
                            break;
                        }
                    }
                }
            }

            if (!isMarketBoard) continue;

            var dist = Vector3.Distance(player.Position, obj.Position);
            if (dist < minDistance)
            {
                minDistance = dist;
                nearest = obj;
            }
        }

        // 第二轮：如果没找到，放宽条件搜索所有 EventObj（名称包含关键字）
        if (nearest == null)
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null) continue;

                // 只搜索 EventObj 和 EventNpc
                if (obj.ObjectKind != ObjectKind.EventObj &&
                    obj.ObjectKind != ObjectKind.EventNpc)
                    continue;

                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                // 宽松匹配：包含市场/布告/交易/board/market 等关键词
                var lowerName = name.ToLower();
                if (lowerName.Contains("市场") || lowerName.Contains("布告") ||
                    lowerName.Contains("交易") || lowerName.Contains("market") ||
                    lowerName.Contains("board") || lowerName.Contains("掲示") ||
                    lowerName.Contains("売") || lowerName.Contains("shop"))
                {
                    var dist = Vector3.Distance(player.Position, obj.Position);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        nearest = obj;
                    }
                }
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

    public void Dispose()
    {
        Cancel();
    }
}
