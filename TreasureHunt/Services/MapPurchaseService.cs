using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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

    private async Task<bool> OpenMarketBoard(CancellationToken token)
    {
        try
        {
            // 检查是否已在交易板附近
            bool alreadyOpen;
            unsafe
            {
                var mbAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.ItemSearch);
                alreadyOpen = mbAgent != null && mbAgent->IsAgentActive();
            }
            if (alreadyOpen)
            {
                OnLog?.Invoke("交易板已打开");
                return true;
            }

            // 通过 RaptureTeleport 或直接调用 NPC 交互打开交易板
            // 这里需要找到最近的交易板 NPC 并交互
            var mbNpc = FindMarketBoardNpc();
            if (mbNpc == null)
            {
                OnLog?.Invoke("附近未找到交易板 NPC，请在市场区域使用");
                return false;
            }

            // 使用 Agent 系统直接打开交易板
            unsafe
            {
                var agentInterface = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
                agentInterface->GetAgentByInternalId(AgentId.ItemSearch)->Show();
            }

            await Task.Delay(1000, token);
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"打开交易板失败: {ex.Message}");
            return false;
        }
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindMarketBoardNpc()
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            var name = obj.Name.ToString();
            if (name.Contains("市场", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Market", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(" retainer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("雇员", StringComparison.OrdinalIgnoreCase))
            {
                return obj;
            }
        }
        return null;
    }

    private async Task<bool> SearchForMap(CancellationToken token)
    {
        try
        {
            // 获取交易板 Addon
            IntPtr atkPtr = IntPtr.Zero;
            unsafe
            {
                var atkUnitBase = GetMarketBoardAddon();
                if (atkUnitBase != null)
                    atkPtr = (IntPtr)atkUnitBase;
            }
            if (atkPtr == IntPtr.Zero)
            {
                OnLog?.Invoke("交易板 UI 未找到");
                return false;
            }

            // 查找搜索输入框组件并输入搜索关键字
            var searchKeyword = SearchKeywordCN;
            // 使用 AtkValue 系统设置搜索文本
            unsafe { SetMarketBoardSearchText((AtkUnitBase*)atkPtr, searchKeyword); }

            await Task.Delay(1000, token);

            // 触发搜索
            unsafe { TriggerMarketBoardSearch((AtkUnitBase*)atkPtr); }

            await Task.Delay(2000, token);
            OnLog?.Invoke($"搜索完成: {searchKeyword}");
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
            IntPtr atkPtr = IntPtr.Zero;
            unsafe
            {
                var atkUnitBase = GetMarketBoardAddon();
                if (atkUnitBase != null)
                    atkPtr = (IntPtr)atkUnitBase;
            }
            if (atkPtr == IntPtr.Zero) return false;

            // 选择第一个搜索结果（藏宝图）
            unsafe { SelectFirstSearchResult((AtkUnitBase*)atkPtr); }
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

        // 从交易板 UI 读取当前最低价
        var price = ReadLowestPrice();
        OnLog?.Invoke($"当前最低价格: {price}");

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
            IntPtr atkPtr = IntPtr.Zero;
            unsafe
            {
                var atkUnitBase = GetMarketBoardAddon();
                if (atkUnitBase != null)
                    atkPtr = (IntPtr)atkUnitBase;
            }
            if (atkPtr == IntPtr.Zero) return false;

            // 点击购买按钮
            unsafe { ClickPurchaseButton((AtkUnitBase*)atkPtr); }
            await Task.Delay(500, token);

            // 确认购买弹窗
            ConfirmPurchaseDialog();
            await Task.Delay(500, token);

            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"确认购买失败: {ex.Message}");
            return false;
        }
    }

    private unsafe AtkUnitBase* GetMarketBoardAddon()
    {
        var addon = Plugin.GameGui.GetAddonByName("MarketBoard");
        return (AtkUnitBase*)addon.Address;
    }

    private unsafe void SetMarketBoardSearchText(AtkUnitBase* atkUnitBase, string text)
    {
        var uldManager = &atkUnitBase->UldManager;
        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var node = uldManager->NodeList[i];
            if (node == null) continue;

            // 使用 GetAsAtkComponentTextInput 安全转换
            var textInput = node->GetAsAtkComponentTextInput();
            if (textInput == null) continue;

            // SetText 接受 CStringPointer
            var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
            fixed (byte* pText = bytes)
            {
                textInput->SetText((InteropGenerator.Runtime.CStringPointer)pText);
            }
            OnLog?.Invoke($"设置搜索文本: {text}");
            return;
        }
        OnLog?.Invoke("未找到搜索输入框组件");
    }

    private unsafe void TriggerMarketBoardSearch(AtkUnitBase* atkUnitBase)
    {
        // 通过 FireCallback 触发搜索
        atkUnitBase->FireCallback(0, null, false);
        OnLog?.Invoke("触发搜索");
    }

    private unsafe void SelectFirstSearchResult(AtkUnitBase* atkUnitBase)
    {
        // 通过 AtkUnitBase FireCallback 选择第一个搜索结果
        var atkValues = stackalloc AtkValue[1];
        atkValues[0].Type = AtkValueType.Int;
        atkValues[0].Int = 0;
        atkUnitBase->FireCallback(1, atkValues, false);
        OnLog?.Invoke("选择第一个搜索结果");
    }

    private unsafe int ReadLowestPrice()
    {
        // 从交易板 UI 读取价格信息
        var atkUnitBase = GetMarketBoardAddon();
        if (atkUnitBase == null) return 0;

        var uldManager = &atkUnitBase->UldManager;
        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var node = uldManager->NodeList[i];
            if (node == null) continue;
            if ((uint)node->Type != (uint)NodeType.Text) continue;

            var textNode = (AtkTextNode*)node;
            var text = textNode->NodeText.ToString();
            // 价格通常为纯数字或包含逗号
            if (text.Length > 0 && char.IsDigit(text[0]))
            {
                var clean = text.Replace(",", "").Replace(".", "").Replace(" ", "");
                if (int.TryParse(clean, out var price))
                    return price;
            }
        }
        return 0;
    }

    private unsafe void ClickPurchaseButton(AtkUnitBase* atkUnitBase)
    {
        // 通过 AtkUnitBase FireCallback 点击购买按钮
        var atkValues = stackalloc AtkValue[1];
        atkValues[0].Type = AtkValueType.Int;
        atkValues[0].Int = 0;
        atkUnitBase->FireCallback(1, atkValues, false);
        OnLog?.Invoke("点击购买按钮");
    }

    private unsafe void ConfirmPurchaseDialog()
    {
        // 确认 SelectYesno 弹窗 (点击"是")
        var addon = Plugin.GameGui.GetAddonByName("SelectYesno");
        if (addon.Address == IntPtr.Zero)
        {
            OnLog?.Invoke("未找到确认弹窗");
            return;
        }

        var selectYesno = (AtkUnitBase*)addon.Address;
        // SelectYesno 的 FireCallback 接受一个 bool: true=Yes, false=No
        var atkValues = stackalloc AtkValue[1];
        atkValues[0].Type = AtkValueType.Bool;
        atkValues[0].Byte = 1; // true = Yes
        selectYesno->FireCallback(1, atkValues, false);
        OnLog?.Invoke("确认购买");
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
