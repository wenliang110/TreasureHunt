using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using TreasureHunt.Helpers;

namespace TreasureHunt.Services;

public class MoneyBagResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int BagsCollected { get; set; }
    public int TargetCount { get; set; }
    public bool TimeExpired { get; set; }
}

/// <summary>
/// TP 钱袋子奖励房自动收集服务
/// 策略：使用 vnavmesh 快速导航到袋子位置并交互
/// 注意：直接修改玩家位置（瞬移）在大多数情况下无效，因此默认使用 vnavmesh 导航
/// </summary>
public class MoneyBagService : IDisposable
{
    private readonly Plugin _plugin;
    private CancellationTokenSource? _cts;
    private IFramework? _framework;
    private bool _isActive = false;

    public event Action<string>? OnLog;
    public event Action<int, int>? OnBagCollected; // (current, target)
    public event Action<int>? OnBagCountChanged;

    // 奖励房常量
    private const int TargetBagCount = 100;
    private const int BonusRoomTimeLimitSec = 90;
    private const float BagInteractRange = 3.0f;

    // 袋子名称
    private const string ShiningBagNameJP = "輝く袋";
    private const string GoldenShiningBagNameJP = "金の輝く袋";
    private const string ShiningBagNameCN = "闪亮的袋子";
    private const string GoldenShiningBagNameCN = "金色闪亮的袋子";

    // 当前收集状态
    private int _bagsCollected = 0;
    private DateTime? _roomStartTime;
    private readonly HashSet<ulong> _collectedBagIds = new();
    private Vector3 _lastTargetPos = Vector3.Zero;

    public bool IsActive => _isActive;
    public int BagsCollected => _bagsCollected;
    public int RemainingTime => _roomStartTime.HasValue
        ? Math.Max(0, BonusRoomTimeLimitSec - (int)(DateTime.Now - _roomStartTime.Value).TotalSeconds)
        : BonusRoomTimeLimitSec;

    public MoneyBagService(Plugin plugin)
    {
        _plugin = plugin;
        _framework = Plugin.Framework;
        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// 启动 TP 钱袋子奖励房自动收集
    /// </summary>
    public async Task<MoneyBagResult> StartCollectionAsync()
    {
        if (!_plugin.Configuration.EnableMoneyBagCollection)
        {
            return new MoneyBagResult { Success = false, ErrorMessage = "TP 钱袋子功能未启用" };
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _isActive = true;
        _bagsCollected = 0;
        _roomStartTime = DateTime.Now;
        _collectedBagIds.Clear();

        OnLog?.Invoke("=== TP 钱袋子奖励房开始 ===");
        OnLog?.Invoke($"目标: {TargetBagCount} 个袋子，限时: {BonusRoomTimeLimitSec} 秒");
        OnLog?.Invoke($"使用 vnavmesh 导航模式");

        try
        {
            while (_bagsCollected < TargetBagCount && !token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();

                // 检查时间
                var elapsed = (DateTime.Now - _roomStartTime.Value).TotalSeconds;
                if (elapsed >= BonusRoomTimeLimitSec)
                {
                    OnLog?.Invoke($"时间到! 已收集 {_bagsCollected}/{TargetBagCount}");
                    return new MoneyBagResult
                    {
                        Success = _bagsCollected >= TargetBagCount,
                        BagsCollected = _bagsCollected,
                        TargetCount = TargetBagCount,
                        TimeExpired = true
                    };
                }

                // 获取所有袋子并优先排序
                var bags = GetAllShiningBagsSorted();
                if (bags.Count == 0)
                {
                    // 没有袋子，等待刷新
                    await Task.Delay(_plugin.Configuration.MoneyBagScanInterval, token);
                    continue;
                }

                // 找到最优目标（最近的金色/普通袋子）
                var target = FindBestBagTarget(bags);
                if (target == null)
                {
                    await Task.Delay(_plugin.Configuration.MoneyBagScanInterval, token);
                    continue;
                }

                // 移动到袋子位置并收集
                await MoveToAndCollectBag(target, token);
            }

            var success = _bagsCollected >= TargetBagCount;
            OnLog?.Invoke(success
                ? $"=== 奖励房完成! 收集 {_bagsCollected} 个袋子 ==="
                : $"=== 奖励房结束，收集 {_bagsCollected}/{TargetBagCount} ===");

            return new MoneyBagResult
            {
                Success = success,
                BagsCollected = _bagsCollected,
                TargetCount = TargetBagCount,
                TimeExpired = false
            };
        }
        catch (OperationCanceledException)
        {
            OnLog?.Invoke("钱袋子收集已取消");
            VnavmeshHelper.Stop();
            return new MoneyBagResult { Success = false, ErrorMessage = "已取消", BagsCollected = _bagsCollected, TargetCount = TargetBagCount };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"钱袋子收集异常: {ex.Message}");
            VnavmeshHelper.Stop();
            return new MoneyBagResult { Success = false, ErrorMessage = ex.Message, BagsCollected = _bagsCollected, TargetCount = TargetBagCount };
        }
        finally
        {
            _isActive = false;
            VnavmeshHelper.Stop();
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 获取所有闪亮袋子，金色优先，按距离排序
    /// </summary>
    private List<(Dalamud.Game.ClientState.Objects.Types.IGameObject bag, bool isGolden, float distance)> GetAllShiningBagsSorted()
    {
        var result = new List<(Dalamud.Game.ClientState.Objects.Types.IGameObject bag, bool isGolden, float distance)>();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return result;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            var name = obj.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            bool isBag = name.Contains(ShiningBagNameJP, StringComparison.OrdinalIgnoreCase) ||
                         name.Contains(ShiningBagNameCN, StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("bag", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("geld", StringComparison.OrdinalIgnoreCase);
            if (!isBag) continue;

            // 跳过已收集的
            if (_collectedBagIds.Contains(obj.GameObjectId)) continue;

            bool isGolden = name.Contains("金", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Gold", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("golden", StringComparison.OrdinalIgnoreCase);

            var dist = Vector3.Distance(player.Position, obj.Position);

            // 只考虑收集范围内的袋子
            if (dist < _plugin.Configuration.MoneyBagCollectRange)
            {
                result.Add((obj, isGolden, dist));
            }
        }

        // 金色袋子优先，然后按距离排序
        result.Sort((a, b) =>
        {
            if (a.isGolden && !b.isGolden) return -1;
            if (!a.isGolden && b.isGolden) return 1;
            return a.distance.CompareTo(b.distance);
        });

        return result;
    }

    /// <summary>
    /// 找到最优的袋子目标
    /// </summary>
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindBestBagTarget(
        List<(Dalamud.Game.ClientState.Objects.Types.IGameObject bag, bool isGolden, float distance)> bags)
    {
        if (bags.Count == 0) return null;

        // 优先最近的金色袋子
        var golden = bags.FirstOrDefault(b => b.isGolden);
        if (golden.bag != null)
        {
            if (_lastTargetPos != golden.bag.Position)
            {
                OnLog?.Invoke($"目标金色袋子 (距离 {golden.distance:F1}m)");
                _lastTargetPos = golden.bag.Position;
            }
            return golden.bag;
        }

        // 最近的普通袋子
        var nearest = bags[0];
        if (_lastTargetPos != nearest.bag.Position)
        {
            _lastTargetPos = nearest.bag.Position;
        }
        return nearest.bag;
    }

    /// <summary>
    /// 移动到袋子位置并收集 - 使用 MoveToAsync 异步导航
    /// </summary>
    private async Task MoveToAndCollectBag(Dalamud.Game.ClientState.Objects.Types.IGameObject bag, CancellationToken token)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return;

            var distance = Vector3.Distance(player.Position, bag.Position);

            // 如果已经在交互范围内，直接交互
            if (distance <= BagInteractRange + 1.0f)
            {
                TryCollectBag(bag);
                await Task.Delay(_plugin.Configuration.MoneyBagScanInterval, token);
                return;
            }

            // 使用 vnavmesh 导航到袋子位置（使用短超时的异步导航）
            if (VnavmeshHelper.IsAvailable())
            {
                // 使用快速短超时导航（奖励房时间紧迫）
                await VnavmeshHelper.MoveToAsync(bag.Position, tolerance: BagInteractRange,
                    fly: false, timeoutMs: 2500, token: token);

                // 导航结束后尝试交互（无论是否完全到达）
                TryCollectBag(bag);
            }
            else
            {
                // vnavmesh 不可用，尝试直接交互（可能在范围内）
                TryCollectBag(bag);
            }

            await Task.Delay(_plugin.Configuration.MoneyBagScanInterval, token);
        }
        catch (OperationCanceledException)
        {
            VnavmeshHelper.Stop();
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"移动收集袋子异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 尝试收集袋子 - 使用同步交互（奖励房时间紧迫，不需要等待对话框）
    /// </summary>
    private void TryCollectBag(Dalamud.Game.ClientState.Objects.Types.IGameObject bag)
    {
        try
        {
            if (_collectedBagIds.Contains(bag.GameObjectId)) return;

            if (GameObjectHelper.IsInInteractRange(bag, BagInteractRange + 1.0f))
            {
                GameObjectHelper.InteractWithObject(bag);
                _collectedBagIds.Add(bag.GameObjectId);

                var name = bag.Name.ToString();
                var count = name.Contains("金") || name.Contains("Gold") || name.Contains("golden") ? 3 : 1;
                _bagsCollected += count;

                OnLog?.Invoke($"收集袋子 ({_bagsCollected}/{TargetBagCount})" + (count > 1 ? " [金色x3]" : ""));
                OnBagCollected?.Invoke(_bagsCollected, TargetBagCount);
                OnBagCountChanged?.Invoke(_bagsCollected);

                // 收集后关闭可能弹出的对话框
                GameHelper.CloseAllDialogs();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"收集袋子失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Framework 更新回调 - 用于实时检测和超时
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_isActive) return;

        // 检查时间
        if (_roomStartTime.HasValue)
        {
            var elapsed = (DateTime.Now - _roomStartTime.Value).TotalSeconds;
            if (elapsed >= BonusRoomTimeLimitSec)
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    OnLog?.Invoke("时间到，停止收集");
                    _cts.Cancel();
                }
            }
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        VnavmeshHelper.Stop();
        _isActive = false;
    }

    public void Dispose()
    {
        if (_framework != null)
        {
            _framework.Update -= OnFrameworkUpdate;
            _framework = null;
        }
        Cancel();
    }
}
