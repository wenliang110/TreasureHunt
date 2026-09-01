using System;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using Dalamud.Utility;
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
    /// 新流程：先传送到挖宝地图 → PDR 远程买图 → 解读 → 导航 → 挖掘 → 进洞
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
            // === 步骤1: 购买藏宝图（如果启用且背包无图）===
            // 优先 PDR 远程购买，失败则自动传送到主城购买
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

            // === 步骤3: 传送到最近晶石（仅当不在挖宝地图时）===
            if (_plugin.Configuration.EnableAutoTeleport && matchedLoc != null)
            {
                var currentTerritory = Plugin.ClientState.TerritoryType;
                // 用名称判断是否在G18地图：查找附近是否有地场节点水晶
                // （领土ID可能不准确，用名称匹配更可靠）
                bool isInG18 = false;
                var unlocked = AetheryteHelper.GetUnlockedAetherytesWithNames();
                foreach (var a in unlocked)
                {
                    if (a.name.Contains("地场节点", StringComparison.OrdinalIgnoreCase) && 
                        a.territoryId == currentTerritory)
                    {
                        isInG18 = true;
                        break;
                    }
                }

                if (!isInG18)
                {
                    _state.SetPhase(TreasureHuntPhase.Teleporting, "正在传送...");
                    PhaseChanged?.Invoke(_state.Phase);

                    // 通过名称匹配找水晶ID（不依赖领土ID）
                    var aetheryteId = FindG18AetheryteByName(matchedLoc.NearestAetheryteNameCN);
                    if (aetheryteId != 0)
                    {
                        OnLog?.Invoke($"传送到晶石: {matchedLoc.NearestAetheryteNameCN} (ID={aetheryteId})");
                        var teleResult = await _plugin.NavigationService.TeleportOnlyAsync(aetheryteId);
                        if (!teleResult.Success)
                        {
                            OnLog?.Invoke($"传送失败: {teleResult.ErrorMessage}");
                        }
                    }
                    else
                    {
                        OnLog?.Invoke($"无法找到晶石: {matchedLoc.NearestAetheryteNameCN}");
                        // 回退：找任意一个地场节点水晶传送
                        var fallbackId = FindAnyG18Aetheryte();
                        if (fallbackId != 0)
                        {
                            OnLog?.Invoke($"回退传送到任意地场节点 (ID={fallbackId})");
                            await _plugin.NavigationService.TeleportOnlyAsync(fallbackId);
                        }
                    }
                }
                else
                {
                    OnLog?.Invoke("已在挖宝地图，直接导航到点位");
                }
            }

            // === 步骤4: 导航到挖宝点 ===
            _state.SetPhase(TreasureHuntPhase.NavigatingToSpot, "正在导航到挖宝点...");
            PhaseChanged?.Invoke(_state.Phase);

            if (mapData?.Location != null)
            {
                // 使用解读地图时从 AgentMap.FlagMapMarkers 读取的世界坐标
                var worldPos = mapData.Location.WorldPosition;
                if (worldPos == Vector3.Zero)
                {
                    // 回退：通过 Map Excel 表转换地图坐标到世界坐标
                    worldPos = MapToWorldPosition(mapData.Location.MapX, mapData.Location.MapY, mapData.Location.TerritoryId);
                }

                OnLog?.Invoke($"解读坐标: ({worldPos.X:F1}, {worldPos.Y:F1}, {worldPos.Z:F1})");

                // 参考 SND 脚本：用 8 个 G18 宝箱预设位置修正导航目标
                // 藏宝图解读后的 flag 坐标可能有偏差，取最近的预设宝箱位置更准确
                var currentTerritory = Plugin.ClientState.TerritoryType;
                if (currentTerritory == TreasureMapConstants.GargantuaskinTerritoryId)
                {
                    var nearestChest = TreasureMapConstants.GetNearestG18ChestPosition(worldPos);
                    var flagToChestDist = Vector3.Distance(new Vector3(worldPos.X, 0, worldPos.Z), 
                                                           new Vector3(nearestChest.X, 0, nearestChest.Z));
                    
                    if (flagToChestDist < 100f) // 距离在合理范围内才修正（避免误判）
                    {
                        OnLog?.Invoke($"修正到最近宝箱预设位置: ({nearestChest.X:F1}, {nearestChest.Y:F1}, {nearestChest.Z:F1}) (偏差 {flagToChestDist:F1}m)");
                        worldPos = nearestChest;
                    }
                    else
                    {
                        OnLog?.Invoke($"最近预设位置距离 {flagToChestDist:F1}m，超出合理范围，使用原始解读坐标");
                    }
                }

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
    /// 新流程：先传送到挖宝地图 → PDR 远程买图 → 解读
    /// </summary>
    public async Task<OrchestratorResult> OneClickBuyAndDecipherAsync()
    {
        _isRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // 先传送到挖宝地图
            if (_plugin.Configuration.EnableAutoTeleport)
            {
                var currentTerritory = Plugin.ClientState.TerritoryType;
                if (currentTerritory != TreasureMapConstants.GargantuaskinTerritoryId)
                {
                    _state.SetPhase(TreasureHuntPhase.Teleporting, "传送到挖宝地图...");
                    PhaseChanged?.Invoke(_state.Phase);

                    // 从已解锁水晶列表中找目标地图的水晶（优先记忆>火>风）
                    var unlocked = AetheryteHelper.GetUnlockedAetherytesWithNames();
                    var g18Aetherytes = unlocked.FindAll(a => a.territoryId == TreasureMapConstants.GargantuaskinTerritoryId);

                    uint aetheryteId = 0;
                    string aetheryteName = "";
                    var memKeywords = new[] { "记忆", "忆", "Memoris", "Memory" };
                    var fireKeywords = new[] { "火", "Fire" };
                    var windKeywords = new[] { "风", "Wind" };

                    foreach (var kw in memKeywords)
                    {
                        var found = g18Aetherytes.Find(a => a.name.Contains(kw, StringComparison.OrdinalIgnoreCase));
                        if (found.aetheryteId != 0) { aetheryteId = found.aetheryteId; aetheryteName = found.name; break; }
                    }
                    if (aetheryteId == 0) foreach (var kw in fireKeywords)
                    {
                        var found = g18Aetherytes.Find(a => a.name.Contains(kw, StringComparison.OrdinalIgnoreCase));
                        if (found.aetheryteId != 0) { aetheryteId = found.aetheryteId; aetheryteName = found.name; break; }
                    }
                    if (aetheryteId == 0) foreach (var kw in windKeywords)
                    {
                        var found = g18Aetherytes.Find(a => a.name.Contains(kw, StringComparison.OrdinalIgnoreCase));
                        if (found.aetheryteId != 0) { aetheryteId = found.aetheryteId; aetheryteName = found.name; break; }
                    }
                    if (aetheryteId == 0 && g18Aetherytes.Count > 0)
                    {
                        aetheryteId = g18Aetherytes[0].aetheryteId;
                        aetheryteName = g18Aetherytes[0].name;
                    }

                    if (aetheryteId != 0)
                    {
                        OnLog?.Invoke($"传送到挖宝地图: {aetheryteName} (ID={aetheryteId})");
                        await _plugin.NavigationService.TeleportOnlyAsync(aetheryteId);
                    }
                }
            }

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

                var aetheryteId = MapLocationDatabase.ResolveAetheryteId(decipherResult.MatchedLocation);
                if (aetheryteId != 0)
                {
                    OnLog?.Invoke($"传送到晶石: {decipherResult.MatchedLocation.NearestAetheryteNameCN} (ID={aetheryteId})");
                    await _plugin.NavigationService.TeleportOnlyAsync(aetheryteId);
                }
                else
                {
                    OnLog?.Invoke($"无法解析晶石 ID: {decipherResult.MatchedLocation.NearestAetheryteNameCN}");
                }
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

    /// <summary>
    /// 通过名称查找 G18 地场节点水晶的 ID
    /// 不依赖领土ID，直接按名称匹配（更准确）
    /// </summary>
    private uint FindG18AetheryteByName(string aetheryteName)
    {
        try
        {
            var unlocked = AetheryteHelper.GetUnlockedAetherytesWithNames();
            foreach (var a in unlocked)
            {
                if (a.name.Contains(aetheryteName, StringComparison.OrdinalIgnoreCase))
                {
                    return a.aetheryteId;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"查找G18水晶失败: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// 查找任意一个 G18 地场节点水晶（作为回退）
    /// 优先级：忆 > 火 > 风
    /// </summary>
    private uint FindAnyG18Aetheryte()
    {
        try
        {
            var unlocked = AetheryteHelper.GetUnlockedAetherytesWithNames();
            var g18Aetherytes = unlocked.FindAll(a => 
                a.name.Contains("地场节点", StringComparison.OrdinalIgnoreCase));

            // 优先级：忆 > 火 > 风
            var priority = new[] { "忆", "火", "风" };
            foreach (var kw in priority)
            {
                foreach (var a in g18Aetherytes)
                {
                    if (a.name.Contains(kw))
                    {
                        return a.aetheryteId;
                    }
                }
            }

            if (g18Aetherytes.Count > 0)
            {
                return g18Aetherytes[0].aetheryteId;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"查找G18回退水晶失败: {ex.Message}");
        }
        return 0;
    }

    private bool HasMapInInventory()
    {
        return _plugin.MapDecipherService.FindMapInInventory(out _, out _);
    }

    /// <summary>
    /// 通过 Map Excel 表将地图显示坐标 (如 9.3, 10.5) 转换为世界坐标。
    /// 公式: worldX = ((mapX - 1) / 0.02 - offsetX) * 100 / sizeFactor
    /// </summary>
    private Vector3 MapToWorldPosition(float mapX, float mapY, uint territoryId = 0)
    {
        try
        {
            // 获取当前领土的 Map 行
            uint mapId = 0;
            if (territoryId == 0)
                territoryId = Plugin.ClientState.TerritoryType;

            var ttSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (ttSheet != null)
            {
                var ttRow = ttSheet.GetRow(territoryId);
                mapId = ttRow.Map.RowId;
            }

            if (mapId == 0) return Vector3.Zero;

            var mapSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            if (mapSheet == null) return Vector3.Zero;

            var mapRow = mapSheet.GetRow(mapId);

            var offsetX = mapRow.OffsetX;
            var offsetY = mapRow.OffsetY;
            var sizeFactor = mapRow.SizeFactor;

            if (sizeFactor == 0) return Vector3.Zero;

            // 逆公式: MapUtil.WorldToMap 的逆运算
            // WorldToMap: mapCoord = (worldCoord * sizeFactor / 100 + offset) * 0.02 + 1
            // 逆: worldCoord = ((mapCoord - 1) / 0.02 - offset) * 100 / sizeFactor
            var worldX = ((mapX - 1.0f) / 0.02f - offsetX) * 100.0f / sizeFactor;
            var worldZ = ((mapY - 1.0f) / 0.02f - offsetY) * 100.0f / sizeFactor;

            return new Vector3(worldX, 0, worldZ);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"坐标转换失败: {ex.Message}");
            return new Vector3(mapX * 10.0f, 0, mapY * 10.0f);
        }
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
