using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace TreasureHunt.Helpers;

/// <summary>
/// 卡住检测和恢复（参考 GatherBuddy Reborn 的 AdvancedUnstuck）
/// 检测玩家是否在导航过程中卡住，并执行恢复动作（跳跃+随机移动）
/// </summary>
public class AdvancedUnstuck : IDisposable
{
    private const double UnstuckDuration = 3.0;
    private const double CheckExpiration = 1.0;
    private const float MinMovementDistance = 2.0f;

    private Vector3 _lastPosition = Vector3.Zero;
    private DateTime _lastCheckTime = DateTime.MinValue;
    private DateTime _lastUnstuckTime = DateTime.MinValue;
    private volatile bool _isUnstucking;
    private volatile bool _isDisposed;
    private readonly Random _random = new();

    public event Action<string>? OnLog;

    /// <summary>
    /// 检查是否卡住，如果卡住则执行恢复动作
    /// </summary>
    /// <param name="isPathing">是否正在寻路</param>
    public unsafe void Check(bool isPathing)
    {
        if (_isUnstucking || _isDisposed)
            return;

        var now = DateTime.Now;

        // 冷却期（上次恢复后 10 秒内不再检测）
        if ((now - _lastUnstuckTime).TotalSeconds < 10)
            return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            _lastPosition = Vector3.Zero;
            _lastCheckTime = now;
            return;
        }

        var currentPos = player.Position;

        if (!isPathing)
        {
            _lastPosition = Vector3.Zero;
            _lastCheckTime = now;
            return;
        }

        // 第一次记录位置
        if (_lastPosition == Vector3.Zero)
        {
            _lastPosition = currentPos;
            _lastCheckTime = now;
            return;
        }

        // 检查是否移动了足够距离
        var moved = Vector3.Distance(currentPos, _lastPosition);
        if (moved >= MinMovementDistance)
        {
            _lastPosition = currentPos;
            _lastCheckTime = now;
            return;
        }

        // 没有移动足够距离，检查持续时间
        var stuckDuration = (now - _lastCheckTime).TotalSeconds;
        if (stuckDuration < UnstuckDuration)
            return;

        // 确认卡住，执行恢复
        OnLog?.Invoke($"检测到卡住（{stuckDuration:F1}秒未移动），执行恢复...");
        _isUnstucking = true;
        _lastUnstuckTime = now;

        try
        {
            TryUnstuck();
        }
        finally
        {
            _isUnstucking = false;
            _lastPosition = currentPos;
            _lastCheckTime = now;
        }
    }

    /// <summary>
    /// 执行恢复动作：仅跳跃，不发起后台导航
    /// 注意：不要在这里发起 fire-and-forget 导航！
    /// 那会导致无限级联：unstuck导航 → 主循环检测到"正在导航"但没移动 → 再次触发unstuck → 无限循环
    /// 主循环的 MoveToAsync 调用会处理重新寻路
    /// </summary>
    private unsafe void TryUnstuck()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var condition = Plugin.Condition;
        var isFlying = condition[ConditionFlag.InFlight];
        var isDiving = condition[ConditionFlag.Diving];

        // 仅尝试跳跃（参考 GatherBuddy: GeneralAction(2) 跳跃）
        if (!isFlying && !isDiving)
        {
            OnLog?.Invoke("尝试跳跃恢复...");
            var actionManager = ActionManager.Instance();
            if (actionManager != null)
            {
                actionManager->UseAction(ActionType.GeneralAction, 2);
            }
        }
    }

    /// <summary>
    /// 重置跟踪状态
    /// </summary>
    public void Reset()
    {
        _lastPosition = Vector3.Zero;
        _lastCheckTime = DateTime.Now;
        _isUnstucking = false;
    }

    public void Dispose()
    {
        _isDisposed = true;
    }
}
