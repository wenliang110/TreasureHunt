using System;
using System.Threading;
using System.Threading.Tasks;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TreasureHunt.Helpers;

/// <summary>
/// 更好的市场布告板 (PDR / BetterMarketBoard) 辅助类
/// 通过 /pdr market &lt;物品ID&gt; 命令直接打开远程交易板，无需跑到主城
/// 
/// 命令参考：
/// /pdr market &lt;物品ID/物品名称&gt; - 开关市场布告板（以指定物品打开）
/// </summary>
public static class PdrMarketHelper
{
    /// <summary>
    /// 检查更好的市场布告板是否可用
    /// </summary>
    public static bool IsAvailable()
    {
        return DependencyManager.IsAvailable(DependencyType.BetterMarketBoard);
    }

    /// <summary>
    /// 打开远程交易板并搜索指定物品
    /// </summary>
    public static bool OpenMarket(uint itemId)
    {
        try
        {
            if (!IsAvailable())
            {
                Plugin.Log.Warning("更好的市场布告板不可用");
                return false;
            }

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
    /// 关闭远程交易板
    /// </summary>
    public static bool CloseMarket()
    {
        try
        {
            // 再次执行 /pdr market 可关闭
            Chat.ExecuteCommand("/pdr market");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"PDR 关闭交易板失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查 PDR 市场窗口是否已打开
    /// </summary>
    public static bool IsMarketOpen()
    {
        try
        {
            // PDR 的交易板窗口名可能是 ItemSearch 或自定义名称
            // 先尝试标准名称
            var addonNames = new[] { "ItemSearch", "PDRMarket", "BetterMarket" };
            foreach (var name in addonNames)
            {
                var addon = Plugin.GameGui.GetAddonByName(name);
                if (addon.Address != IntPtr.Zero)
                {
                    unsafe
                    {
                        var atk = (AtkUnitBase*)addon.Address;
                        if (atk->IsVisible) return true;
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
    /// 等待 PDR 市场窗口出现
    /// </summary>
    public static async Task<bool> WaitForMarketOpen(int timeoutMs = 5000, CancellationToken token = default)
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
