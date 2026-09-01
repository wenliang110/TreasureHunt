using System;
using System.Threading;
using System.Threading.Tasks;
using ECommons.Automation;

namespace TreasureHunt.Helpers;

/// <summary>
/// LazyLoot 辅助类
/// 用于与 LazyLoot 插件交互，自动 Roll 点
/// 
/// LazyLoot 命令参考：
/// /lazy need   - 对所有物品 Need（不能 Need 则 Greed，不能 Greed 则 Pass）
/// /lazy greed  - 对所有物品 Greed（不能 Greed 则 Pass）
/// /lazy pass   - 对所有物品 Pass
/// /fulf on/off - 启用/禁用 FULF 全自动模式
/// 
/// 如果 LazyLoot 提供了 IPC，优先使用 IPC；否则回退到命令方式
/// </summary>
public static class LazyLootHelper
{
    private const string PluginLabel = "LazyLoot";

    /// <summary>
    /// 检查 LazyLoot 是否可用
    /// </summary>
    public static bool IsAvailable()
    {
        return DependencyManager.IsAvailable(DependencyType.LazyLoot);
    }

    /// <summary>
    /// 执行 Need Roll（优先 Need，降级 Greed/Pass）
    /// </summary>
    public static bool RollNeed()
    {
        return ExecuteRollCommand("need");
    }

    /// <summary>
    /// 执行 Greed Roll
    /// </summary>
    public static bool RollGreed()
    {
        return ExecuteRollCommand("greed");
    }

    /// <summary>
    /// 执行 Pass
    /// </summary>
    public static bool RollPass()
    {
        return ExecuteRollCommand("pass");
    }

    /// <summary>
    /// 启用 FULF 全自动 Roll 模式
    /// </summary>
    public static bool EnableFulf()
    {
        try
        {
            // 尝试通过 IPC 启用
            try
            {
                var sub = Plugin.PluginInterface.GetIpcSubscriber<bool, object>("LazyLoot.SetFulfEnabled");
                sub.InvokeAction(true);
                return true;
            }
            catch
            {
                // 回退到命令方式
                Chat.ExecuteCommand("/fulf on");
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"启用 FULF 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 禁用 FULF 全自动 Roll 模式
    /// </summary>
    public static bool DisableFulf()
    {
        try
        {
            try
            {
                var sub = Plugin.PluginInterface.GetIpcSubscriber<bool, object>("LazyLoot.SetFulfEnabled");
                sub.InvokeAction(false);
                return true;
            }
            catch
            {
                Chat.ExecuteCommand("/fulf off");
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"禁用 FULF 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 执行 Roll 命令
    /// 优先尝试 IPC，失败则回退到聊天命令
    /// </summary>
    private static bool ExecuteRollCommand(string rollType)
    {
        try
        {
            if (!IsAvailable())
            {
                Plugin.Log.Warning("LazyLoot 不可用");
                return false;
            }

            // 方式1：尝试通过 IPC 调用（如果 LazyLoot 提供了）
            try
            {
                var ipcName = $"LazyLoot.Roll.{rollType}";
                var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>(ipcName);
                var result = sub.InvokeFunc();
                if (result)
                {
                    Plugin.Log.Debug($"LazyLoot IPC Roll {rollType} 成功");
                    return true;
                }
            }
            catch
            {
                // IPC 不存在，继续尝试命令方式
            }

            // 方式2：通过聊天命令调用（最兼容的方式）
            // /lazy need / /lazy greed / /lazy pass
            var command = $"/lazy {rollType}";
            Plugin.Log.Debug($"执行 LazyLoot 命令: {command}");
            Chat.ExecuteCommand(command);

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"LazyLoot Roll {rollType} 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 等待 Roll 完成（检测 NeedGreed / Loot 窗口是否消失）
    /// </summary>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <param name="token">取消令牌</param>
    /// <returns>是否成功完成 Roll</returns>
    public static async Task<bool> WaitForRollComplete(int timeoutMs = 60000, CancellationToken token = default)
    {
        try
        {
            var startTime = DateTime.Now;
            var hasRollWindowAtStart = false;
            var initialWait = 0;

            // 先等待最多 2 秒，看 roll 窗口是否出现
            while (initialWait < 2000)
            {
                if (token.IsCancellationRequested) return false;

                if (HasRollWindow())
                {
                    hasRollWindowAtStart = true;
                    break;
                }

                await Task.Delay(100, token);
                initialWait += 100;
            }

            // 如果一开始就没有 roll 窗口，可能已经 roll 完了，或者根本没有可 roll 的东西
            if (!hasRollWindowAtStart)
            {
                Plugin.Log.Debug("未检测到 Roll 窗口，可能无需 Roll");
                return true;
            }

            // 等待 roll 窗口消失
            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (token.IsCancellationRequested) return false;

                if (!HasRollWindow())
                {
                    // 窗口消失，再等 1 秒确认（防止中间短暂消失）
                    await Task.Delay(1000, token);
                    if (!HasRollWindow())
                    {
                        Plugin.Log.Debug("Roll 窗口已关闭，Roll 完成");
                        return true;
                    }
                }

                await Task.Delay(500, token);
            }

            Plugin.Log.Warning("等待 Roll 完成超时");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"等待 Roll 完成异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查是否有 Roll 窗口（NeedGreed 或 Loot）
    /// </summary>
    public static bool HasRollWindow()
    {
        try
        {
            var needGreed = Plugin.GameGui.GetAddonByName("NeedGreed");
            if (needGreed.Address != IntPtr.Zero)
            {
                unsafe
                {
                    var atk = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)needGreed.Address;
                    if (atk->IsVisible) return true;
                }
            }

            var loot = Plugin.GameGui.GetAddonByName("Loot");
            if (loot.Address != IntPtr.Zero)
            {
                unsafe
                {
                    var atk = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)loot.Address;
                    if (atk->IsVisible) return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
