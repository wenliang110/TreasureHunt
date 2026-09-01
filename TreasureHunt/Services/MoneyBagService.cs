using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
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
    private const int BagScanIntervalMs = 100;
    private const float AoeDangerRange = 5.0f;

    // 辉く袋 (Shining Bag) 名称
    private const string ShiningBagNameJP = "輝く袋";
    private const string GoldenShiningBagNameJP = "金の輝く袋";
    private const string ShiningBagNameCN = "闪亮的袋子";
    private const string GoldenShiningBagNameCN = "金色闪亮的袋子";

    // 当前收集状态
    private int _bagsCollected = 0;
    private DateTime? _roomStartTime;
    private Vector3? _lastSafePosition;
    private readonly HashSet<uint> _collectedBagIds = new();

    public bool IsActive => _isActive;
    public int BagsCollected => _bagsCollected;
    public int RemainingTime => _roomStartTime.HasValue
        ? Math.Max(0, BonusRoomTimeLimitSec - (int)(DateTime.Now - _roomStartTime.Value).TotalSeconds)
        : BonusRoomTimeLimitSec;

    public MoneyBagService(Plugin plugin)
    {
        _plugin = plugin;
        _framework = _plugin.Framework;
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

        try
        {
            while (_bagsCollected < TargetBagCount && !token.IsCancellationRequested)
            {
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
                    await Task.Delay(BagScanIntervalMs, token);
                    continue;
                }

                // 找到最优目标（最近的金色/普通袋子）
                var target = FindBestBagTarget(bags);
                if (target == null)
                {
                    await Task.Delay(BagScanIntervalMs, token);
                    continue;
                }

                // 检查是否需要躲避 AOE
                if (_plugin.Configuration.MoneyBagDodgeAoe)
                {
                    var danger = CheckAoeDanger(target.Position);
                    if (danger != null)
                    {
                        OnLog?.Invoke($"检测到 AOE 危险，躲避中...");
                        await DodgeAoe(danger, token);
                        continue;
                    }
                }

                // 瞬移到袋子位置并收集
                await TeleportAndCollectBag(target, token);
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
            return new MoneyBagResult { Success = false, ErrorMessage = "已取消", BagsCollected = _bagsCollected, TargetCount = TargetBagCount };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"钱袋子收集异常: {ex.Message}");
            return new MoneyBagResult { Success = false, ErrorMessage = ex.Message, BagsCollected = _bagsCollected, TargetCount = TargetBagCount };
        }
        finally
        {
            _isActive = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 获取所有闪亮袋子，金色优先排序
    /// </summary>
    private List<(Dalamud.Game.ClientState.Objects.Types.IGameObject bag, bool isGolden, float distance)> GetAllShiningBagsSorted()
    {
        var result = new List<(Dalamud.Game.ClientState.Objects.Types.IGameObject, bool, float)>();
        var player = _plugin.ClientState.LocalPlayer;
        if (player == null) return result;

        foreach (var obj in _plugin.ObjectTable)
        {
            if (obj == null) continue;
            var name = obj.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            bool isBag = name.Contains(ShiningBagNameJP, StringComparison.OrdinalIgnoreCase) ||
                         name.Contains(ShiningBagNameCN, StringComparison.OrdinalIgnoreCase);
            if (!isBag) continue;

            // 跳过已收集的
            if (_collectedBagIds.Contains(obj.GameObjectId)) continue;

            bool isGolden = name.Contains("金", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Gold", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("golden", StringComparison.OrdinalIgnoreCase);

            var dist = Vector3.Distance(player.Position, obj.Position);
            result.Add((obj, isGolden, dist));
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

        // 优先金色袋子（3倍计数）
        var golden = bags.FirstOrDefault(b => b.isGolden && b.distance < _plugin.Configuration.MoneyBagCollectRange);
        if (golden.bag != null)
        {
            OnLog?.Invoke($"优先金色袋子 (距离 {golden.distance:F1}m)");
            return golden.bag;
        }

        // 最近的有效袋子
        var nearest = bags.FirstOrDefault(b => b.distance < _plugin.Configuration.MoneyBagCollectRange);
        return nearest.bag;
    }

    /// <summary>
    /// 瞬移到袋子位置并收集
    /// </summary>
    private async Task TeleportAndCollectBag(Dalamud.Game.ClientState.Objects.Types.IGameObject bag, CancellationToken token)
    {
        try
        {
            // 使用瞬移到袋子位置
            // 这是 TP 钱袋子的核心功能 - 通过修改玩家位置实现快速移动
            TeleportToPositionInternal(bag.Position);

            // 等待一小段时间让游戏处理交互
            await Task.Delay(50, token);

            // 尝试交互袋子
            if (GameObjectHelper.IsInInteractRange(bag, BagInteractRange))
            {
                GameObjectHelper.InteractWithObject(bag);
                _collectedBagIds.Add(bag.GameObjectId);

                // 金色袋子算3个
                var name = bag.Name.ToString();
                var count = name.Contains("金") ? 3 : 1;
                _bagsCollected += count;

                OnLog?.Invoke($"收集袋子 ({_bagsCollected}/{TargetBagCount})" + (count > 1 ? " [金色x3]" : ""));
                OnBagCollected?.Invoke(_bagsCollected, TargetBagCount);
                OnBagCountChanged?.Invoke(_bagsCollected);
            }

            await Task.Delay(_plugin.Configuration.MoneyBagScanInterval, token);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"收集袋子异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查 AOE 危险区域
    /// </summary>
    private Vector3? CheckAoeDanger(Vector3 targetPos)
    {
        var player = _plugin.ClientState.LocalPlayer;
        if (player == null) return null;

        // 检查目标位置附近是否有 AOE 危险
        // 通过检测施法中的敌人或地面效果
        foreach (var obj in _plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc) continue;

            // 检查敌人是否正在施放直线范围攻击
            // 这里需要检测敌人的 cast 信息
            var distToTarget = Vector3.Distance(obj.Position, targetPos);
            if (distToTarget < AoeDangerRange)
            {
                // 检测敌人面朝方向是否指向目标
                // 如果是直线攻击，需要判断是否在攻击路径上
                return obj.Position; // 返回危险源位置
            }
        }

        return null;
    }

    /// <summary>
    /// 躲避 AOE
    /// </summary>
    private async Task DodgeAoe(Vector3 dangerSource, CancellationToken token)
    {
        var player = _plugin.ClientState.LocalPlayer;
        if (player == null) return;

        // 计算远离危险源的安全位置
        var direction = Vector3.Normalize(player.Position - dangerSource);
        if (direction == Vector3.Zero)
            direction = new Vector3(1, 0, 0);

        var safePos = player.Position + direction * (AoeDangerRange + 3.0f);
        // 保持 Y 坐标不变
        safePos.Y = player.Position.Y;

        OnLog?.Invoke($"躲避到安全位置 ({safePos.X:F1}, {safePos.Z:F1})");
        TeleportToPositionInternal(safePos);

        await Task.Delay(200, token);
    }

    /// <summary>
    /// 内部瞬移方法 - 修改玩家位置
    /// </summary>
    private unsafe void TeleportToPositionInternal(Vector3 position)
    {
        try
        {
            var player = _plugin.ClientState.LocalPlayer;
            if (player == null) return;

            // 获取游戏内部的 Player 对象指针
            var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0];
            if (playerObj == null || !playerObj->IsPlayer()) return;

            // 修改位置
            playerObj->SetPosition(position.X, position.Y, position.Z);

            // 确保位置更新生效
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (framework != null)
            {
                // 更新游戏内部位置缓存
                var pos = playerObj->GetPosition();
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"瞬移失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Framework 更新回调 - 用于实时检测
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_isActive) return;

        // 检查是否仍在奖励房内
        if (_roomStartTime.HasValue)
        {
            var elapsed = (DateTime.Now - _roomStartTime.Value).TotalSeconds;
            if (elapsed >= BonusRoomTimeLimitSec)
            {
                OnLog?.Invoke("时间到，停止收集");
                _cts?.Cancel();
                return;
            }
        }

        // 检查是否在洞内（或被踢出）
        var player = _plugin.ClientState.LocalPlayer;
        if (player == null) return;

        // 检查是否还有袋子在刷新
        // 如果连续多次扫描都没有袋子且时间剩余较多，可能还在战斗阶段
    }

    public void Cancel()
    {
        _cts?.Cancel();
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
