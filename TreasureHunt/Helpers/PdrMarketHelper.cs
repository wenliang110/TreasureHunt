using System;
using System.Threading;
using System.Threading.Tasks;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TreasureHunt.Helpers;

/// <summary>
/// 更好的市场布告板 (PDR / BetterMarketBoard) 辅助类
/// 通过 /pdr market &lt;物品ID/物品名称&gt; 命令直接打开远程交易板，无需跑到主城
/// 
/// 插件命令参考：
/// /pdr market &lt;物品ID/物品名称&gt; - 开关市场布告板（以指定物品打开市场布告板）
/// 
/// 注意：不依赖程序集名称检测，直接执行命令。
/// 如果命令无效（插件未安装），窗口不会弹出，调用方通过 IsMarketOpen 判断并回退。
/// </summary>
public static class PdrMarketHelper
{
    /// <summary>
    /// 可能的市场窗口名称
    /// PDR 的远程市场板可能复用 ItemSearch，也可能用自定义名称
    /// </summary>
    private static readonly string[] MarketAddonNames = new[]
    {
        "ItemSearch",       // 标准交易板窗口名
        "PDRMarket",        // 可能的 PDR 自定义名
        "BetterMarket",     // 可能的自定义名
        "PandaDutyMarket",  // 可能的自定义名
        "MarketBoard",      // 可能的自定义名
        "ShopExchangeItem", // 另一种交易相关窗口
    };

    /// <summary>
    /// 打开远程交易板并搜索指定物品（通过 /pdr market 命令）
    /// 返回 true 表示命令已执行（不代表一定成功打开了窗口）
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
    /// 打开远程交易板（不带物品参数，用于关闭）
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
    /// 检查市场窗口是否已打开（尝试多种可能的窗口名）
    /// </summary>
    public static bool IsMarketOpen()
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
                        if (atk->IsVisible)
                        {
                            Plugin.Log.Debug($"检测到市场窗口: {name}");
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
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
}
