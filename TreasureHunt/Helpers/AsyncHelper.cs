using System;
using System.Threading;
using System.Threading.Tasks;

namespace TreasureHunt.Helpers;

/// <summary>
/// 异步辅助方法（参考 Untarnished Heart 的 WaitUntilAsync 模式）
/// 提供通用的异步等待功能，简化状态等待代码
/// </summary>
public static class AsyncHelper
{
    /// <summary>
    /// 等待条件满足，带超时和描述
    /// 参考 Untarnished Heart: WaitUntilAsync(Func&lt;bool&gt;, string, CancellationToken)
    /// </summary>
    /// <param name="condition">等待条件（返回 true 表示满足）</param>
    /// <param name="description">等待描述（用于日志）</param>
    /// <param name="token">取消令牌</param>
    /// <param name="timeoutMs">超时毫秒（默认 10 秒）</param>
    /// <param name="checkIntervalMs">检查间隔毫秒（默认 500）</param>
    /// <returns>条件是否在超时前满足</returns>
    public static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        string description,
        CancellationToken token,
        int timeoutMs = 10000,
        int checkIntervalMs = 500)
    {
        var startTime = DateTime.Now;
        while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            if (condition())
                return true;
            await Task.Delay(checkIntervalMs, token);
        }
        Plugin.Log.Verbose($"等待超时: {description} ({timeoutMs}ms)");
        return false;
    }

    /// <summary>
    /// 等待条件满足并执行动作，带超时
    /// </summary>
    public static async Task<bool> WaitAndExecuteAsync(
        Func<bool> condition,
        string description,
        Action action,
        CancellationToken token,
        int timeoutMs = 10000,
        int checkIntervalMs = 500)
    {
        var success = await WaitUntilAsync(condition, description, token, timeoutMs, checkIntervalMs);
        if (success)
            action();
        return success;
    }

    /// <summary>
    /// 等待区域加载完成
    /// 参考 Untarnished Heart: WaitForAreaReadyAsync
    /// </summary>
    public static async Task<bool> WaitForAreaReadyAsync(CancellationToken token, int timeoutMs = 30000)
    {
        return await WaitUntilAsync(
            () =>
            {
                var cond = Plugin.Condition;
                return !cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] &&
                       !cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] &&
                       Plugin.ObjectTable.LocalPlayer != null;
            },
            "等待区域加载",
            token,
            timeoutMs,
            500);
    }

    /// <summary>
    /// 等待传送完成
    /// 参考 GatherBuddy: Enqueue(BetweenAreas) → Enqueue(!BetweenAreas) → Delay(1500)
    /// </summary>
    public static async Task<bool> WaitForTeleportCompleteAsync(CancellationToken token, int timeoutMs = 30000)
    {
        // 先等待进入传送状态
        var entered = await WaitUntilAsync(
            () => Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas],
            "等待传送开始",
            token,
            10000,
            200);

        if (!entered)
            return false;

        // 等待传送完成
        var exited = await WaitUntilAsync(
            () => !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] &&
                  Plugin.ObjectTable.LocalPlayer != null,
            "等待传送完成",
            token,
            timeoutMs,
            500);

        if (!exited)
            return false;

        // 额外等待 1.5 秒让画面稳定（参考 GatherBuddy: DelayNext(1500)）
        await Task.Delay(1500, token);
        return true;
    }
}
