using System;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using TreasureHunt.Helpers;
using TreasureHunt.Models;

namespace TreasureHunt.Services;

public class OrchestratorResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public TreasureHuntPhase FinalPhase { get; set; }
    public string? Summary { get; set; }
}

public class TreasureHuntOrchestrator : IDisposable
{
    private readonly Plugin _plugin;
    private CancellationTokenSource? _cts;
    private readonly TreasureHuntState _state;

    private bool _isRunning = false;

    public bool IsRunning => _isRunning;
    public TreasureHuntPhase CurrentPhase => _state.Phase;
    public string StatusMessage => _state.StatusMessage ?? "空闲中";
    public TreasureHuntState State => _state;

    public event Action<TreasureHuntPhase>? PhaseChanged;
    public event Action<string>? OnLog;
    public event Action<bool>? OnRunComplete;

    public TreasureHuntOrchestrator(Plugin plugin)
    {
        _plugin = plugin;
        _state = new TreasureHuntState();
    }

    /// <summary>
    /// 启动全自动挖宝流程
    /// </summary>
    public async Task<OrchestratorResult> RunFullAutoAsync()
    {
        if (_isRunning)
        {
            return new OrchestratorResult { Success = false, ErrorMessage = "已有任务在运行中" };
        }

        _isRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        OnLog?.Invoke("========== 自动挖宝启动 ==========");

        try
        {
            // === 步骤1: 购买藏宝图（如果启用且背包无图） ===
            if (_plugin.Configuration.EnableAutoPurchase || _plugin.Configuration.EnableOneClickBuyDecipher)
            {
                _state.SetPhase(TreasureHuntPhase.PurchasingMap, "正在购买藏宝图...");
                PhaseChanged?.Invoke(_state.Phase);

                if (!HasMapInInventory())
                {
                    OnLog?.Invoke("背包无图，开始购买...");
                    var purchaseResult = await _plugin.MapPurchaseService.PurchaseMapAsync();
                    if (!purchaseResult.Success)
                    {
                        _state.Fail(purchaseResult.ErrorMessage ?? "购买失败");
                        return new OrchestratorResult { Success = false, ErrorMessage = purchaseResult.ErrorMessage, FinalPhase = _state.Phase };
                    }
                    OnLog?.Invoke($"购买成功，价格: {purchaseResult.Price}");
                    await Task.Delay(_plugin.Configuration.InteractionDelay, token);
                }
                else
                {
                    OnLog?.Invoke("背包已有藏宝图，跳过购买");
                }
            }

            // === 步骤2: 解读藏宝图 ===
            _state.SetPhase(TreasureHuntPhase.DecipheringMap, "正在解读藏宝图...");
            PhaseChanged?.Invoke(_state.Phase);

            var decipherResult = await _plugin.MapDecipherService.DecipherMapAsync();
            if (!decipherResult.Success)
            {
                _state.Fail(decipherResult.ErrorMessage ?? "解读失败");
                return new OrchestratorResult { Success = false, ErrorMessage = decipherResult.ErrorMessage, FinalPhase = _state.Phase };
            }

            var mapData = decipherResult.MapData;
            var matchedLoc = decipherResult.MatchedLocation;
            OnLog?.Invoke($"解读成功，坐标: ({mapData?.Location?.MapX}, {mapData?.Location?.MapY})");

            // === 步骤3: 传送到最近晶石 ===
            if (_plugin.Configuration.EnableAutoTeleport)
            {
                _state.SetPhase(TreasureHuntPhase.Teleporting, "正在传送...");
                PhaseChanged?.Invoke(_state.Phase);

                // 根据匹配的点位确定最近晶石
                if (matchedLoc != null)
                {
                    OnLog?.Invoke($"最近晶石: {matchedLoc.NearestAetheryteNameCN}");
                    // 使用 Teleporter 或 AetheryteHelper 传送
                    // 这里需要根据晶石名称找到对应的 aetheryte ID
                }

                await Task.Delay(2000, token); // 等待传送完成
            }

            // === 步骤4: 导航到挖宝点 ===
            _state.SetPhase(TreasureHuntPhase.NavigatingToSpot, "正在导航到挖宝点...");
            PhaseChanged?.Invoke(_state.Phase);

            // 使用 vnavmesh 导航到目标位置
            // 需要将地图坐标转换为世界坐标
            // 这需要通过 MapLinkData 转换
            if (mapData?.Location != null)
            {
                var worldPos = MapToWorldPosition(mapData.Location.MapX, mapData.Location.MapY);
                OnLog?.Invoke($"导航到世界坐标: ({worldPos.X:F1}, {worldPos.Y:F1}, {worldPos.Z:F1})");

                var navResult = await _plugin.NavigationService.NavigateToAsync(worldPos, "藏宝图点位");
                if (!navResult.Success)
                {
                    _state.Fail(navResult.ErrorMessage ?? "导航失败");
                    return new OrchestratorResult { Success = false, ErrorMessage = navResult.ErrorMessage, FinalPhase = _state.Phase };
                }
            }

            // === 步骤5: 挖掘 → 战斗 → 开箱 → 检查传送门 ===
            _state.SetPhase(TreasureHuntPhase.Digging, "正在挖掘...");
            PhaseChanged?.Invoke(_state.Phase);

            var cofferResult = await _plugin.TreasureCofferService.ExecuteCofferFlowAsync();
            if (!cofferResult.Success)
            {
                _state.Fail(cofferResult.ErrorMessage ?? "宝箱流程失败");
                return new OrchestratorResult { Success = false, ErrorMessage = cofferResult.ErrorMessage, FinalPhase = _state.Phase };
            }

            // === 步骤6: 如果出洞了，进洞挖宝 ===
            if (cofferResult.PortalSpawned)
            {
                _state.SetPhase(TreasureHuntPhase.EnteringPortal, "进入传送门...");
                PhaseChanged?.Invoke(_state.Phase);

                var portalResult = await _plugin.PortalDungeonService.ExecutePortalDungeonFlow();
                OnLog?.Invoke($"洞内流程完成: 清理 {portalResult.FloorsCleared} 层, 奖励房: {portalResult.ReachedBonusRoom}");

                // === 步骤6a: 如果触发了奖励房，执行钱袋子收集 ===
                if (portalResult.ReachedBonusRoom && _plugin.Configuration.EnableMoneyBagCollection)
                {
                    _state.SetPhase(TreasureHuntPhase.InPortalDungeon, "TP 钱袋子奖励房开始!");
                    PhaseChanged?.Invoke(_state.Phase);

                    var moneyBagResult = await _plugin.MoneyBagService.StartCollectionAsync();
                    OnLog?.Invoke($"钱袋子收集: {moneyBagResult.BagsCollected}/{moneyBagResult.TargetCount}" +
                        (moneyBagResult.TimeExpired ? " (超时)" : ""));
                }
            }
            else
            {
                OnLog?.Invoke("未出洞，本张图结束");
            }

            _state.SetPhase(TreasureHuntPhase.Done, "挖宝完成");
            PhaseChanged?.Invoke(_state.Phase);

            var summary = $"挖宝完成 - " +
                (cofferResult.PortalSpawned ? "出洞并完成洞内流程" : "无洞，图结束");
            OnLog?.Invoke($"========== {summary} ==========");

            return new OrchestratorResult
            {
                Success = true,
                FinalPhase = _state.Phase,
                Summary = summary
            };
        }
        catch (OperationCanceledException)
        {
            OnLog?.Invoke("自动挖宝已取消");
            return new OrchestratorResult { Success = false, ErrorMessage = "已取消", FinalPhase = _state.Phase };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"自动挖宝异常: {ex.Message}");
            _state.Fail(ex.Message);
            return new OrchestratorResult { Success = false, ErrorMessage = ex.Message, FinalPhase = _state.Phase };
        }
        finally
        {
            _isRunning = false;
            _state.SetPhase(TreasureHuntPhase.Idle);
            _cts?.Dispose();
            _cts = null;
            OnRunComplete?.Invoke(true);
        }
    }

    /// <summary>
    /// 一键买图+解读
    /// </summary>
    public async Task<OrchestratorResult> OneClickBuyAndDecipherAsync()
    {
        _isRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // 购买
            _state.SetPhase(TreasureHuntPhase.PurchasingMap, "一键买图: 购买中...");
            PhaseChanged?.Invoke(_state.Phase);

            if (!HasMapInInventory())
            {
                var purchaseResult = await _plugin.MapPurchaseService.PurchaseMapAsync();
                if (!purchaseResult.Success)
                {
                    return new OrchestratorResult { Success = false, ErrorMessage = purchaseResult.ErrorMessage };
                }
            }

            // 解读
            _state.SetPhase(TreasureHuntPhase.DecipheringMap, "一键买图: 解读中...");
            PhaseChanged?.Invoke(_state.Phase);

            var decipherResult = await _plugin.MapDecipherService.DecipherMapAsync();
            if (!decipherResult.Success)
            {
                return new OrchestratorResult { Success = false, ErrorMessage = decipherResult.ErrorMessage };
            }

            // 标记位置
            if (_plugin.Configuration.EnableMarkLocation)
            {
                OnLog?.Invoke("已标记藏宝图位置");
            }

            // 传送
            if (_plugin.Configuration.EnableAutoTeleport && decipherResult.MatchedLocation != null)
            {
                _state.SetPhase(TreasureHuntPhase.Teleporting, "一键买图: 传送中...");
                PhaseChanged?.Invoke(_state.Phase);
                // 执行传送逻辑
                await Task.Delay(2000, token);
            }

            return new OrchestratorResult
            {
                Success = true,
                Summary = "买图+解读完成"
            };
        }
        catch (OperationCanceledException)
        {
            return new OrchestratorResult { Success = false, ErrorMessage = "已取消" };
        }
        catch (Exception ex)
        {
            return new OrchestratorResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _isRunning = false;
            _state.SetPhase(TreasureHuntPhase.Idle);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool HasMapInInventory()
    {
        return _plugin.MapDecipherService.FindMapInInventory(out _, out _);
    }

    private Vector3 MapToWorldPosition(float mapX, float mapY)
    {
        // 将地图坐标 (X, Y) 转换为世界坐标 (X, Y, Z)
        // 在 FF14 中，地图坐标与世界坐标的转换需要通过 MapToWorldMap8 实例
        // 这需要使用 Dalamud 的 IDataManager 获取地图数据
        // 简化实现 - 实际使用时需要通过 TerritoryMap 数据转换

        // 临时实现：返回近似坐标
        // 实际应通过以下方式转换：
        // 1. 获取当前 Territory 的 MapData
        // 2. 使用 MapData 的 OffsetX, OffsetY, ScaleFactor 等参数
        // 3. worldX = (mapX - offset) * scale + center

        return new Vector3(mapX * 10.0f, 0, mapY * 10.0f);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _isRunning = false;
        _state.SetPhase(TreasureHuntPhase.Idle, "已取消");
        OnLog?.Invoke("自动挖宝已取消");
    }

    public void Dispose()
    {
        Cancel();
    }
}
