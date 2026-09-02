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
    /// 参考 vsatisfy: GameMain.Instance()->TerritoryLoadState == 2（比 BetweenAreas 更精确）
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
                       GameHelper.IsTerritoryLoaded() &&
                       GameHelper.IsInteractable();
            },
            "等待区域加载",
            token,
            timeoutMs,
            500);
    }

    /// <summary>
    /// 等待传送完成
    /// 参考 GatherBuddy: Enqueue(BetweenAreas) → Enqueue(!BetweenAreas) → Delay(1500)
    /// 参考 vsatisfy: 使用 IsTerritoryLoaded + IsInteractable 精确检测
    /// </summary>
    public static async Task<bool> WaitForTeleportCompleteAsync(CancellationToken token, int timeoutMs = 30000)
    {
        // 先等待进入传送状态（BetweenAreas 或 正在施放传送）
        var entered = await WaitUntilAsync(
            () => Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
                  GameHelper.IsCastingTeleport(),
            "等待传送开始",
            token,
            10000,
            200);

        if (!entered)
            return false;

        // 等待传送完成：不在区域切换中 + 区域已加载 + 玩家可交互
        var exited = await WaitUntilAsync(
            () => !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] &&
                  GameHelper.IsTerritoryLoaded() &&
                  GameHelper.IsInteractable(),
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

    /// <summary>
    /// 等待过场动画完成
    /// 参考 TextAdvance: 检测过场动画开始→结束
    /// 用于交互宝箱/传送门后可能触发的过场动画
    /// </summary>
    public static async Task<bool> WaitForCutsceneAsync(CancellationToken token, int timeoutMs = 30000)
    {
        // 等待过场动画开始（短暂等待，可能没有过场动画）
        var started = await WaitUntilAsync(
            () => GameHelper.IsCutsceneActive(),
            "等待过场动画",
            token,
            5000,
            200);

        if (!started)
            return true; // 没有过场动画，继续执行

        // 等待过场动画结束
        var ended = await WaitUntilAsync(
            () => !GameHelper.IsCutsceneActive(),
            "等待过场动画结束",
            token,
            timeoutMs,
            500);

        if (ended)
        {
            await Task.Delay(1000, token);
            return true;
        }

        return false;
    }
}
