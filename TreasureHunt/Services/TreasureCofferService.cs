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

    public TreasureCofferService(Plugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>
    /// 完整的宝箱交互流程：挖掘 → 等待宝箱 → 交互 → 等待战斗 → 开箱 → 检查传送门
    /// 全流程自动处理对话框（SelectYesno 确认等）
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

            // 步骤3: 交互宝箱（触发怪物）- 自动处理后续对话框
            State = CofferState.InteractingWithCoffer;
            if (!await InteractWithCoffer(coffer, token))
                return new CofferResult { Success = false, ErrorMessage = "交互宝箱失败" };

            // 步骤4: 等待战斗结束
            State = CofferState.WaitingForMonsterCombat;
            await WaitForCombatEnd(token);

            // 步骤5: 开箱 - 自动处理后续对话框
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
            unsafe
            {
                var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
                if (actionManager == null) return false;

                var status = actionManager->GetActionStatus(
                    FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 20);
                if (status != 0)
                {
                    OnLog?.Invoke($"挖掘动作不可用 (status={status})");
                    return false;
                }

                var result = actionManager->UseAction(
                    FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 20);
                if (!result)
                {
                    OnLog?.Invoke("挖掘 UseAction 返回 false");
                    return false;
                }
            }

            OnLog?.Invoke("执行挖掘 (GeneralAction ID=20)");
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

        // 10秒没找到，尝试随机移动搜索
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

    private async Task<Dalamud.Game.ClientState.Objects.Types.IGameObject?> SearchForCofferByRandomMovement(CancellationToken token)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        var basePos = player.Position;
        var random = new Random();

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            token.ThrowIfCancellationRequested();

            var coffer = GameObjectHelper.GetTreasureCoffer();
            if (coffer != null) return coffer;

            var offsetX = (float)(random.NextDouble() - 0.5) * 10f;
            var offsetZ = (float)(random.NextDouble() - 0.5) * 10f;
            var targetPos = new Vector3(basePos.X + offsetX, basePos.Y, basePos.Z + offsetZ);

            OnLog?.Invoke($"随机移动搜索宝箱 ({attempt}/5)...");

            if (VnavmeshHelper.IsAvailable())
            {
                await VnavmeshHelper.MoveToAsync(targetPos, tolerance: 1.5f, fly: false, timeoutMs: 8000, token: token);
            }
            else
            {
                await Task.Delay(1500, token);
            }

            await Task.Delay(500, token);

            coffer = GameObjectHelper.GetTreasureCoffer();
            if (coffer != null) return coffer;
        }

        return null;
    }

    /// <summary>
    /// 交互宝箱（触发怪物）- 使用异步对话框处理
    /// </summary>
    private async Task<bool> InteractWithCoffer(Dalamud.Game.ClientState.Objects.Types.IGameObject coffer, CancellationToken token)
    {
        try
        {
            int interactAttempts = 0;
            const int maxAttempts = 5;

            while (interactAttempts < maxAttempts)
            {
                token.ThrowIfCancellationRequested();
                interactAttempts++;

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
                    if (VnavmeshHelper.IsAvailable())
                    {
                        await VnavmeshHelper.MoveToAsync(currentCoffer.Position, tolerance: 2.5f, fly: false, timeoutMs: 10000, token: token);
                    }
                }

                // 使用异步交互，自动处理后续对话框（Talk/SelectString/SelectYesno）
                OnLog?.Invoke($"交互宝箱 ({interactAttempts}/{maxAttempts})");
                await GameObjectHelper.InteractWithObjectAsync(currentCoffer, token,
                    selectStringIndex: 0, selectIconStringIndex: 0, autoConfirmYesno: true,
                    totalTimeoutMs: 8000);

                await Task.Delay(_plugin.Configuration.InteractionDelay + 500, token);

                // 检查是否进入战斗
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

    /// <summary>
    /// 战斗后开箱 - 使用异步对话框处理
    /// </summary>
    private async Task<bool> OpenChestAfterCombat(Dalamud.Game.ClientState.Objects.Types.IGameObject coffer, CancellationToken token)
    {
        try
        {
            var currentCoffer = GameObjectHelper.GetTreasureCoffer();
            if (currentCoffer == null)
            {
                OnLog?.Invoke("战斗后未找到宝箱，尝试搜索...");
                currentCoffer = await SearchForCofferByRandomMovement(token);
                if (currentCoffer == null)
                {
                    OnLog?.Invoke("无法找到宝箱，可能已被开启或消失");
                    return false;
                }
            }

            // 移动到宝箱附近
            if (!GameObjectHelper.IsInInteractRange(currentCoffer, 3.0f))
            {
                if (VnavmeshHelper.IsAvailable())
                {
                    await VnavmeshHelper.MoveToAsync(currentCoffer.Position, tolerance: 2.5f, fly: false, timeoutMs: 15000, token: token);
                }
            }

            // 持续开箱直到宝箱消失 - 自动处理所有对话框
            OnLog?.Invoke("开始开启宝箱...");
            int openAttempts = 0;
            const int maxOpenAttempts = 10;

            while (openAttempts < maxOpenAttempts)
            {
                token.ThrowIfCancellationRequested();
                openAttempts++;

                var chest = GameObjectHelper.GetTreasureCoffer();
                if (chest == null)
                {
                    OnLog?.Invoke($"宝箱已开启成功！（尝试{openAttempts}次）");
                    return true;
                }

                if (!GameObjectHelper.IsInInteractRange(chest, 3.0f))
                {
                    if (VnavmeshHelper.IsAvailable())
                    {
                        await VnavmeshHelper.MoveToAsync(chest.Position, tolerance: 2.5f, fly: false, timeoutMs: 5000, token: token);
                    }
                }

                // 使用异步交互自动处理对话框
                OnLog?.Invoke($"开箱尝试 ({openAttempts}/{maxOpenAttempts})");
                await GameObjectHelper.InteractWithObjectAsync(chest, token,
                    selectStringIndex: 0, selectIconStringIndex: 0, autoConfirmYesno: true,
                    totalTimeoutMs: 5000);

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

    private async Task<bool> SearchForPortalByRandomMovement(CancellationToken token)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;

        var basePos = player.Position;
        var random = new Random();

        for (int attempt = 1; attempt <= 8; attempt++)
        {
            token.ThrowIfCancellationRequested();

            var portal = GameObjectHelper.GetPortalTransferCircle();
            if (portal != null) return true;

            var offsetX = (float)(random.NextDouble() - 0.5) * 10f;
            var offsetZ = (float)(random.NextDouble() - 0.5) * 10f;
            var targetPos = new Vector3(basePos.X + offsetX, basePos.Y, basePos.Z + offsetZ);

            OnLog?.Invoke($"随机移动搜索传送门 ({attempt}/8)...");

            if (VnavmeshHelper.IsAvailable())
            {
                await VnavmeshHelper.MoveToAsync(targetPos, tolerance: 1.0f, fly: false, timeoutMs: 8000, token: token);
            }
            else
            {
                await Task.Delay(1500, token);
            }

            await Task.Delay(500, token);

            portal = GameObjectHelper.GetPortalTransferCircle();
            if (portal != null) return true;
        }

        return false;
    }

    /// <summary>
    /// 交互洞内特定对象（兼容旧 API，用于 PortalDungeonService）
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
            if (VnavmeshHelper.IsAvailable())
            {
                await VnavmeshHelper.MoveToAsync(obj.Position, tolerance: 2.5f, fly: false, timeoutMs: 15000, token: token);
            }
        }

        await GameObjectHelper.InteractWithObjectAsync(obj, token,
            selectStringIndex: 0, selectIconStringIndex: 0, autoConfirmYesno: true,
            totalTimeoutMs: 10000);

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
