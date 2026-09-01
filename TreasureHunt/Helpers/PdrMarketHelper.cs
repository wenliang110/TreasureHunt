using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TreasureHunt.Helpers;

/// <summary>
/// 更好的市场布告板 (PDR / BetterMarketBoard) 辅助类
/// 通过 /pdr market &lt;物品ID/物品名称&gt; 命令直接打开远程交易板，无需跑到主城
/// 
/// 插件命令参考：
/// /pdr market &lt;物品ID/物品名称&gt; - 开关市场布告板
/// 
/// 购买原理：
/// PDR 使用 ImGui 自定义界面，但底层仍然使用游戏的 ItemSearch 系统。
/// 我们通过 InfoProxyItemSearch 直接读取搜索结果并调用 SendPurchaseRequestPacket() 完成购买，
/// 无需操作 UI 节点。
/// </summary>
public static class PdrMarketHelper
{
    /// <summary>
    /// 可能的市场窗口名称（原生 addon）
    /// PDR 主体是 ImGui 窗口，但如果它触发了原生 ItemSearch 也能用
    /// </summary>
    private static readonly string[] MarketAddonNames = new[]
    {
        "ItemSearch",
        "ShopExchangeItem",
        "PDRMarket",
        "BetterMarket",
    };

    /// <summary>
    /// 打开远程交易板并搜索指定物品（通过 /pdr market 命令）
    /// </summary>
    public static bool OpenMarket(uint itemId)
    {
        try
        {
            var command = $"/pdr market {itemId}";
            Plugin.Log.Debug($"执行 PDR 命令: {command}");
            Chat.ExecuteCommand(command);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"PDR 打开交易板失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 切换远程交易板开关（不带参数）
    /// </summary>
    public static bool ToggleMarket()
    {
        try
        {
            Chat.ExecuteCommand("/pdr market");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"PDR 切换交易板失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取当前打开的市场窗口名称（原生 addon）
    /// </summary>
    public static string GetMarketAddonName()
    {
        try
        {
            foreach (var name in MarketAddonNames)
            {
                var addon = Plugin.GameGui.GetAddonByName(name);
                if (addon.Address != IntPtr.Zero)
                {
                    unsafe
                    {
                        var atk = (AtkUnitBase*)addon.Address;
                        if (atk->IsVisible) return name;
                    }
                }
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 检查市场窗口是否已打开（原生 addon）
    /// </summary>
    public static bool IsMarketOpen()
    {
        return !string.IsNullOrEmpty(GetMarketAddonName());
    }

    /// <summary>
    /// 等待市场窗口出现
    /// </summary>
    public static async Task<bool> WaitForMarketOpen(int timeoutMs = 8000, CancellationToken token = default)
    {
        var start = DateTime.Now;
        while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
        {
            if (token.IsCancellationRequested) return false;
            if (IsMarketOpen()) return true;
            await Task.Delay(200, token);
        }
        return false;
    }

    // ========== 以下是基于 InfoProxyItemSearch 的直接购买 API ==========

    /// <summary>
    /// 获取 AgentItemSearch 指针
    /// </summary>
    public static unsafe AgentItemSearch* GetAgentItemSearch()
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null) return null;
            var agent = agentModule->GetAgentByInternalId(AgentId.ItemSearch);
            if (agent == null) return null;
            return (AgentItemSearch*)agent;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取 AgentItemSearch 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取 InfoProxyItemSearch 指针
    /// </summary>
    public static unsafe InfoProxyItemSearch* GetInfoProxyItemSearch()
    {
        try
        {
            var agent = GetAgentItemSearch();
            if (agent == null) return null;
            return agent->InfoProxyItemSearch;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取 InfoProxyItemSearch 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 检查 ItemSearch Agent 是否激活
    /// </summary>
    public static unsafe bool IsAgentActive()
    {
        try
        {
            var agent = GetAgentItemSearch();
            if (agent == null) return false;
            return agent->IsAgentActive();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取当前搜索结果的 listing 数量
    /// </summary>
    public static unsafe int GetListingCount()
    {
        try
        {
            var proxy = GetInfoProxyItemSearch();
            if (proxy == null) return 0;
            return (int)proxy->ListingCount;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 获取指定索引的 listing 价格
    /// </summary>
    public static unsafe uint GetListingPrice(int index)
    {
        try
        {
            var proxy = GetInfoProxyItemSearch();
            if (proxy == null || index < 0 || index >= (int)proxy->ListingCount) return 0;
            var listing = GetListingAtIndex(proxy, index);
            return listing->UnitPrice;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 获取第一个 listing 的详细信息（价格、数量等）
    /// </summary>
    public static unsafe (uint price, uint quantity, uint itemId, bool isValid) GetFirstListing()
    {
        try
        {
            var proxy = GetInfoProxyItemSearch();
            if (proxy == null || proxy->ListingCount == 0) return (0, 0, 0, false);
            var listing = GetListingAtIndex(proxy, 0);
            return (listing->UnitPrice, listing->Quantity, listing->ItemId, true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"获取第一个 listing 失败: {ex.Message}");
            return (0, 0, 0, false);
        }
    }

    /// <summary>
    /// 获取指定索引的 MarketBoardListing 指针
    /// 由于不同版本 FFXIVClientStructs 可能没有 ListingsSpan，直接通过指针计算偏移
    /// _listings 字段偏移量为 0x30，每个 MarketBoardListing 大小为 0xB8
    /// </summary>
    private static unsafe MarketBoardListing* GetListingAtIndex(InfoProxyItemSearch* proxy, int index)
    {
        // 直接通过 _listings 字段访问（FixedSizeArray100<MarketBoardListing>）
        // 字段偏移 0x30，每个元素大小 0xB8
        byte* basePtr = (byte*)proxy + 0x30;
        return (MarketBoardListing*)(basePtr + index * 0xB8);
    }

    /// <summary>
    /// 等待搜索结果加载完成
    /// </summary>
    public static async Task<bool> WaitForSearchResults(int timeoutMs = 10000, CancellationToken token = default)
    {
        var start = DateTime.Now;
        while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
        {
            if (token.IsCancellationRequested) return false;
            var count = GetListingCount();
            if (count > 0) return true;
            await Task.Delay(300, token);
        }
        return GetListingCount() > 0;
    }

    /// <summary>
    /// 购买第一个 listing
    /// 通过 InfoProxyItemSearch.SetLastPurchasedItem + SendPurchaseRequestPacket 直接购买
    /// </summary>
    public static unsafe bool PurchaseFirstListing(uint maxPrice)
    {
        try
        {
            var proxy = GetInfoProxyItemSearch();
            if (proxy == null)
            {
                Plugin.Log.Error("购买失败: InfoProxyItemSearch 为空");
                return false;
            }

            if (proxy->ListingCount == 0)
            {
                Plugin.Log.Error("购买失败: 没有搜索结果");
                return false;
            }

            var listing = GetListingAtIndex(proxy, 0);
            Plugin.Log.Debug($"购买第一个 listing: ItemId={listing->ItemId} Price={listing->UnitPrice} Quantity={listing->Quantity}");

            if (listing->UnitPrice > maxPrice)
            {
                Plugin.Log.Warning($"价格超出上限: {listing->UnitPrice} > {maxPrice}");
                return false;
            }

            // 设置要购买的物品
            bool setOk = proxy->SetLastPurchasedItem(listing);
            if (!setOk)
            {
                Plugin.Log.Error("设置购买物品失败");
                return false;
            }

            Plugin.Log.Debug("SetLastPurchasedItem 成功，准备发送购买请求...");

            // 发送购买请求包
            bool sendOk = proxy->SendPurchaseRequestPacket();
            Plugin.Log.Debug($"SendPurchaseRequestPacket 返回: {sendOk}");

            return sendOk;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"购买异常: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 调试：获取 PDR 市场状态的详细信息
    /// </summary>
    public static unsafe string GetDebugInfo()
    {
        try
        {
            var agent = GetAgentItemSearch();
            var proxy = GetInfoProxyItemSearch();

            var info = new List<string>();
            info.Add($"AgentItemSearch: {(agent != null ? "存在" : "空")}");
            if (agent != null)
            {
                info.Add($"  IsAgentActive: {agent->IsAgentActive()}");
                info.Add($"  ResultItemId: {agent->ResultItemId}");
                info.Add($"  ListingPageItemCount: {agent->ListingPageItemCount}");
            }

            info.Add($"InfoProxyItemSearch: {(proxy != null ? "存在" : "空")}");
            if (proxy != null)
            {
                info.Add($"  SearchItemId: {proxy->SearchItemId}");
                info.Add($"  ListingCount: {proxy->ListingCount}");
                info.Add($"  WaitingForListings: {proxy->WaitingForListings}");

                if (proxy->ListingCount > 0)
                {
                    var first = GetListingAtIndex(proxy, 0);
                    info.Add($"  第一个 listing: ItemId={first->ItemId} Price={first->UnitPrice} Qty={first->Quantity}");
                }
            }

            return string.Join("\n", info);
        }
        catch (Exception ex)
        {
            return $"调试信息获取失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 调试：列出当前所有可见的原生 addon 名称
    /// </summary>
    public static List<string> ListVisibleAddons()
    {
        var result = new List<string>();
        try
        {
            var commonNames = new[]
            {
                "ItemSearch", "ShopExchangeItem", "Shop", "ShopCard",
                "Inventory", "Character", "Teleport", "TeleportTown",
                "PDRMarket", "BetterMarket", "PandaDutyMarket",
                "PandaDuty", "PDR", "BetterMarketBoard",
                "Config", "ConfigChara", "ConfigSystem",
                "ContextMenu", "ContextIconMenu", "Hud",
                "TeleportDocomo", "TeleportTown", "Telepot",
            };
            foreach (var name in commonNames)
            {
                try
                {
                    var addon = Plugin.GameGui.GetAddonByName(name);
                    if (addon.Address != IntPtr.Zero)
                    {
                        unsafe
                        {
                            var atk = (AtkUnitBase*)addon.Address;
                            if (atk->IsVisible) result.Add(name);
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"列出可见 addon 失败: {ex.Message}");
        }
        return result;
    }
}
