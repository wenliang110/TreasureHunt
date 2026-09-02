using System;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using TreasureHunt.Helpers;

namespace TreasureHunt.Services;

public enum CofferState
{
    Idle,
    Digging,
    WaitingForCofferSpawn,
    CofferFound,
    InteractingWithCoffer,
    WaitingForMonsterCombat,
    OpeningChest,
    ChestOpened,
    CheckingPortal,
    Error
}

public class CofferResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool PortalSpawned { get; set; }
    public CofferState FinalState { get; set; }
}

public class TreasureCofferService : IDisposable
{
    private readonly Plugin _plugin;
    private CancellationTokenSource? _cts;

    public event Action<CofferState>? StateChanged;
    public event Action<string>? OnLog;

    private CofferState _state = CofferState.Idle;
    public CofferState State
    {
        get => _state;
        private set
        {
            _state = value;
            StateChanged?.Invoke(_state);
            OnLog?.Invoke($"宝箱状态: {value}");
        }
    }

    // 宝箱对象名
    private const string TreasureCofferNameCN = "宝箱";
    private const string TreasureCofferNameEN = "Treasure";
    private const string TreasureCofferNameJP = "宝箱";

    // 转送魔紋（传送门）
    private const string PortalNameCN = "传送魔纹";
    private const string PortalNameEN = "Transfer";
    private const string PortalNameJP = "転送魔紋";

    public TreasureCofferService(Plugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>
    /// 完整的宝箱交互流程：挖掘 → 等待宝箱 → 交互 → 等待战斗 → 开箱 → 检查传送门
    /// </summary>
    public async Task<CofferResult> ExecuteCofferFlowAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // 步骤1: 挖掘
            State = CofferState.Digging;
            if (!await DigForTreasure(token))
                return new CofferResult { Success = false, ErrorMessage = "挖掘失败" };

            // 步骤2: 等待宝箱出现
            State = CofferState.WaitingForCofferSpawn;
            var coffer = await WaitForCofferSpawn(token);
            if (coffer == null)
                return new CofferResult { Success = false, ErrorMessage = "宝箱未出现" };

            State = CofferState.CofferFound;
            OnLog?.Invoke($"找到宝箱: {coffer.Name} at ({coffer.Position.X:F1}, {coffer.Position.Z:F1})");

            // 步骤3: 交互宝箱（触发怪物）
            State = CofferState.InteractingWithCoffer;
            if (!await InteractWithCoffer(coffer, token))
                return new CofferResult { Success = false, ErrorMessage = "交互宝箱失败" };

            // 步骤4: 等待战斗结束（由其他插件处理战斗）
            State = CofferState.WaitingForMonsterCombat;
            await WaitForCombatEnd(token);

            // 步骤5: 开箱
            State = CofferState.OpeningChest;
            if (!await OpenChestAfterCombat(coffer, token))
                return new CofferResult { Success = false, ErrorMessage = "开箱失败" };

            // 步骤6: 检查传送门
            State = CofferState.CheckingPortal;
            var portalSpawned = await CheckPortalSpawn(token);

            State = CofferState.ChestOpened;
            return new CofferResult
            {
                Success = true,
                PortalSpawned = portalSpawned,
                FinalState = State
            };
        }
        catch (OperationCanceledException)
        {
            return new CofferResult { Success = false, ErrorMessage = "已取消" };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"宝箱流程异常: {ex.Message}");
            return new CofferResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            State = CofferState.Idle;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<bool> DigForTreasure(CancellationToken token)
    {
        try
        {
            // 使用挖掘技能
            // 需要通过 MapDecipherService 执行
            unsafe
            {
                var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
                if (actionManager == null) return false;

                actionManager->UseAction(
                    FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 12898); // Dig
            }

            OnLog?.Invoke("执行挖掘");
            await Task.Delay(500, token);
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"挖掘失败: {ex.Message}");
            return false;
        }
    }

    private async Task<Dalamud.Game.ClientState.Objects.Types.IGameObject?> WaitForCofferSpawn(CancellationToken token)
    {
        OnLog?.Invoke("等待宝箱出现...");

        // 使用 AsyncHelper 等待宝箱出现（参考 Untarnished Heart 的 WaitUntilAsync 模式）
        var found = await AsyncHelper.WaitUntilAsync(
            () => GameObjectHelper.GetTreasureCoffer() != null,
            "宝箱出现",
            token,
            10000,
            200);

        if (found)
        {
            OnLog?.Invoke("宝箱已出现");
            return GameObjectHelper.GetTreasureCoffer();
        }

        // 10秒没找到，尝试随机移动搜索（参考 SND 脚本）
        OnLog?.Invoke("未找到宝箱，尝试随机移动搜索...");
        var foundCoffer = await SearchForCofferByRandomMovement(token);
        if (foundCoffer != null)
        {
            OnLog?.Invoke("随机移动后找到宝箱");
            return foundCoffer;
        }

        OnLog?.Invoke("等待宝箱超时");
        return null;
    }

    /// <summary>
    /// 随机移动搜索宝箱（参考 SND 脚本 3开启宝箱.lua 的 FindTreasureChest）
    /// 在当前位置附近随机移动，每次移动后尝试寻找宝箱
    /// </summary>
    private async Task<Dalamud.Game.ClientState.Objects.Types.IGameObject?> SearchForCofferByRandomMovement(CancellationToken token)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        var basePos = player.Position;
        var random = new Random();

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            token.ThrowIfCancellationRequested();

            // 检查当前是否已经有宝箱
            var coffer = GameObjectHelper.GetTreasureCoffer();
            if (coffer != null) return coffer;

            // 生成随机偏移（±5米）
            var offsetX = (float)(random.NextDouble() - 0.5) * 10f;
            var offsetZ = (float)(random.NextDouble() - 0.5) * 10f;
            var targetPos = new Vector3(basePos.X + offsetX, basePos.Y, basePos.Z + offsetZ);

            OnLog?.Invoke($"随机移动搜索宝箱 ({attempt}/5)...");

            // 移动到随机位置
            if (VnavmeshHelper.IsAvailable())
            {
                await VnavmeshHelper.MoveToAsync(targetPos, tolerance: 1.5f, fly: false, timeoutMs: 8000, token: token);
                VnavmeshHelper.Stop();
            }
            else
            {
                await Task.Delay(1500, token);
            }

            await Task.Delay(500, token);

            // 再次检查宝箱
            coffer = GameObjectHelper.GetTreasureCoffer();
            if (coffer != null) return coffer;
        }

        return null;
    }

    private async Task<bool> InteractWithCoffer(Dalamud.Game.ClientState.Objects.Types.IGameObject coffer, CancellationToken token)
    {
        try
        {
            // 如果不在交互范围内，先移动到附近
            if (!GameObjectHelper.IsInInteractRange(coffer, 3.0f))
            {
                OnLog?.Invoke("移动到宝箱附近");
                VnavmeshHelper.PathfindAndMoveTo(coffer.Position);

                // 使用 AsyncHelper 等待到达交互范围
                var reached = await AsyncHelper.WaitUntilAsync(
                    () => GameObjectHelper.IsInInteractRange(coffer, 3.0f),
                    "到达宝箱位置",
                    token,
                    30000,
                    200);

                VnavmeshHelper.Stop();

                if (!reached)
                {
                    OnLog?.Invoke("无法到达宝箱位置");
                    return false;
                }
            }

            // 如果配置了不选中他人宝箱怪，检查宝箱是否属于自己
            if (_plugin.Configuration.AvoidOthersTreasureMonsters)
            {
                // 检查宝箱是否是自己的
                // 这需要读取宝箱的所有者信息
                // 如果不是自己的，跳过
            }

            // 持续交互宝箱直到目标消失（参考 SND 脚本的 ContinuousOpenChest）
            // 宝箱触发后会召唤怪物，目标可能暂时不会消失，
            // 所以这里我们只保证至少交互成功一次
            OnLog?.Invoke("与宝箱交互（触发怪物）...");
            int interactAttempts = 0;
            const int maxAttempts = 5;

            while (interactAttempts < maxAttempts)
            {
                token.ThrowIfCancellationRequested();
                interactAttempts++;

                // 重新定位宝箱（可能位置有变化）
                var currentCoffer = GameObjectHelper.GetTreasureCoffer();
                if (currentCoffer == null)
                {
                    OnLog?.Invoke($"宝箱已消失（第{interactAttempts}次交互后）");
                    return true;
                }

                // 确保在交互范围内
                if (!GameObjectHelper.IsInInteractRange(currentCoffer, 3.0f))
                {
                    OnLog?.Invoke($"距离宝箱较远，重新靠近...");
                    VnavmeshHelper.PathfindAndMoveTo(currentCoffer.Position);
                    await AsyncHelper.WaitUntilAsync(
                        () => GameObjectHelper.IsInInteractRange(currentCoffer, 3.0f),
                        "靠近宝箱",
                        token,
                        5000,
                        200);
                    VnavmeshHelper.Stop();
                }

                // 交互
                GameObjectHelper.InteractWithObject(currentCoffer);
                OnLog?.Invoke($"交互宝箱 ({interactAttempts}/{maxAttempts})");
                await Task.Delay(_plugin.Configuration.InteractionDelay + 500, token);

                // 检查是否进入战斗（说明交互成功触发了怪物）
                if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
                {
                    OnLog?.Invoke("已进入战斗，宝箱触发成功");
                    return true;
                }
            }

            OnLog?.Invoke($"已尝试 {maxAttempts} 次交互，继续下一步");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"交互宝箱失败: {ex.Message}");
            return false;
        }
    }

    private async Task WaitForCombatEnd(CancellationToken token)
    {
        OnLog?.Invoke("等待战斗结束...");

        // 先等待进入战斗（参考 Untarnished Heart: 等待条件满足模式）
        var combatStarted = await AsyncHelper.WaitUntilAsync(
            () => Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat],
            "战斗开始",
            token,
            30000,
            500);

        if (combatStarted)
        {
            OnLog?.Invoke("战斗开始");
        }
        else
        {
            OnLog?.Invoke("未检测到战斗开始，可能已秒杀或无怪物");
            return;
        }

        // 等待战斗结束（5分钟超时）
        var combatEnded = await AsyncHelper.WaitUntilAsync(
            () => !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat],
            "战斗结束",
            token,
            300000,
            500);

        if (combatEnded)
        {
            OnLog?.Invoke("战斗结束");
            await Task.Delay(_plugin.Configuration.CombatWaitDelay, token);
        }
        else
        {
            OnLog?.Invoke("等待战斗超时");
        }
    }

    private async Task<bool> OpenChestAfterCombat(Dalamud.Game.ClientState.Objects.Types.IGameObject coffer, CancellationToken token)
    {
        try
        {
            // 战斗结束后，宝箱可能还在原地，也可能需要重新寻找
            var currentCoffer = GameObjectHelper.GetTreasureCoffer();
            if (currentCoffer == null)
            {
                OnLog?.Invoke("战斗后未找到宝箱，尝试搜索...");
                // 尝试在附近搜索
                currentCoffer = await SearchForCofferByRandomMovement(token);
                if (currentCoffer == null)
                {
                    OnLog?.Invoke("无法找到宝箱，可能已被开启或消失");
                    return false;
                }
            }

            // 移动到宝箱附近（使用 AsyncHelper 等待到达交互范围）
            if (!GameObjectHelper.IsInInteractRange(currentCoffer, 3.0f))
            {
                VnavmeshHelper.PathfindAndMoveTo(currentCoffer.Position);
                await AsyncHelper.WaitUntilAsync(
                    () => GameObjectHelper.IsInInteractRange(currentCoffer, 3.0f),
                    "到达宝箱位置(战斗后)",
                    token,
                    15000,
                    200);
                VnavmeshHelper.Stop();
            }

            // 持续开箱直到宝箱消失（参考 SND 脚本 ContinuousOpenChest）
            OnLog?.Invoke("开始开启宝箱...");
            int openAttempts = 0;
            const int maxOpenAttempts = 10;

            while (openAttempts < maxOpenAttempts)
            {
                token.ThrowIfCancellationRequested();
                openAttempts++;

                // 重新获取宝箱对象
                var chest = GameObjectHelper.GetTreasureCoffer();
                if (chest == null)
                {
                    OnLog?.Invoke($"宝箱已开启成功！（尝试{openAttempts}次）");
                    return true;
                }

                // 确保在范围内
                if (!GameObjectHelper.IsInInteractRange(chest, 3.0f))
                {
                    VnavmeshHelper.PathfindAndMoveTo(chest.Position);
                    await AsyncHelper.WaitUntilAsync(
                        () => GameObjectHelper.IsInInteractRange(chest, 3.0f),
                        "靠近宝箱(开箱)",
                        token,
                        5000,
                        200);
                    VnavmeshHelper.Stop();
                }

                // 交互开箱
                GameObjectHelper.InteractWithObject(chest);
                OnLog?.Invoke($"开箱尝试 ({openAttempts}/{maxOpenAttempts})");
                await Task.Delay(1500, token);
            }

            OnLog?.Invoke($"开箱尝试达 {maxOpenAttempts} 次，假设已成功");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"开箱失败: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CheckPortalSpawn(CancellationToken token)
    {
        OnLog?.Invoke("检查传送门...");
        await Task.Delay(2000, token);

        var portal = GameObjectHelper.GetPortalTransferCircle();
        if (portal != null)
        {
            OnLog?.Invoke("传送门已出现!");
            return true;
        }

        // 没找到的话，尝试随机移动搜索（参考 SND 脚本 4开启传送魔纹.lua）
        OnLog?.Invoke("未检测到传送门，尝试随机移动搜索...");
        var foundPortal = await SearchForPortalByRandomMovement(token);
        if (foundPortal)
        {
            OnLog?.Invoke("随机移动后找到传送门！");
            return true;
        }

        OnLog?.Invoke("无传送门，本张图结束");
        return false;
    }

    /// <summary>
    /// 随机移动搜索传送门（参考 SND 脚本 4开启传送魔纹.lua 的 FindPortal）
    /// </summary>
    private async Task<bool> SearchForPortalByRandomMovement(CancellationToken token)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;

        var basePos = player.Position;
        var random = new Random();

        for (int attempt = 1; attempt <= 8; attempt++)
        {
            token.ThrowIfCancellationRequested();

            // 先检查传送门
            var portal = GameObjectHelper.GetPortalTransferCircle();
            if (portal != null) return true;

            // 生成随机偏移（±5米）
            var offsetX = (float)(random.NextDouble() - 0.5) * 10f;
            var offsetZ = (float)(random.NextDouble() - 0.5) * 10f;
            var targetPos = new Vector3(basePos.X + offsetX, basePos.Y, basePos.Z + offsetZ);

            OnLog?.Invoke($"随机移动搜索传送门 ({attempt}/8)...");

            // 移动
            if (VnavmeshHelper.IsAvailable())
            {
                await VnavmeshHelper.MoveToAsync(targetPos, tolerance: 1.0f, fly: false, timeoutMs: 8000, token: token);
                VnavmeshHelper.Stop();
            }
            else
            {
                await Task.Delay(1500, token);
            }

            await Task.Delay(500, token);

            // 再次检查
            portal = GameObjectHelper.GetPortalTransferCircle();
            if (portal != null) return true;
        }

        return false;
    }

    /// <summary>
    /// 交互洞内特定对象（如洞内的机关、下一层按钮等）
    /// </summary>
    public async Task<bool> InteractWithDungeonObject(uint dataId, CancellationToken token)
    {
        var obj = GameObjectHelper.FindNearestObjectByDataId(dataId);
        if (obj == null)
        {
            OnLog?.Invoke($"未找到目标对象 (DataId={dataId})");
            return false;
        }

        if (!GameObjectHelper.IsInInteractRange(obj, 3.0f))
        {
            VnavmeshHelper.PathfindAndMoveTo(obj.Position);
            await AsyncHelper.WaitUntilAsync(
                () => GameObjectHelper.IsInInteractRange(obj, 3.0f),
                "到达交互对象位置",
                token,
                30000,
                200);
            VnavmeshHelper.Stop();
        }

        GameObjectHelper.InteractWithObject(obj);
        OnLog?.Invoke($"已交互对象: {obj.Name}");
        await Task.Delay(_plugin.Configuration.InteractionDelay, token);
        return true;
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Cancel();
    }
}
