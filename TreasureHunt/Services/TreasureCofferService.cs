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
        var timeout = TimeSpan.FromSeconds(10);
        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime) < timeout)
        {
            token.ThrowIfCancellationRequested();

            var coffer = GameObjectHelper.GetTreasureCoffer();
            if (coffer != null)
            {
                OnLog?.Invoke("宝箱已出现");
                return coffer;
            }

            await Task.Delay(200, token);
        }

        OnLog?.Invoke("等待宝箱超时");
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
                // 使用 vnavmesh 导航到宝箱位置
                VnavmeshHelper.PathfindAndMoveTo(coffer.Position);

                var moveTimeout = TimeSpan.FromSeconds(30);
                var moveStart = DateTime.Now;
                while (!GameObjectHelper.IsInInteractRange(coffer, 3.0f) && (DateTime.Now - moveStart) < moveTimeout)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(200, token);
                }

                VnavmeshHelper.StopAutoRunning();

                if (!GameObjectHelper.IsInInteractRange(coffer, 3.0f))
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

            // 交互宝箱
            GameObjectHelper.InteractWithObject(coffer);
            OnLog?.Invoke("已交互宝箱");
            await Task.Delay(_plugin.Configuration.InteractionDelay, token);
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
        var timeout = TimeSpan.FromMinutes(5);
        var startTime = DateTime.Now;
        var inCombat = false;

        while ((DateTime.Now - startTime) < timeout)
        {
            token.ThrowIfCancellationRequested();

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                await Task.Delay(500, token);
                continue;
            }

            // 检查战斗状态
            var currentInCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
            if (currentInCombat)
            {
                if (!inCombat)
                {
                    inCombat = true;
                    OnLog?.Invoke("战斗开始");
                }
            }
            else if (inCombat)
            {
                // 战斗刚结束
                OnLog?.Invoke("战斗结束");
                await Task.Delay(_plugin.Configuration.CombatWaitDelay, token);
                return;
            }

            await Task.Delay(500, token);
        }

        OnLog?.Invoke("等待战斗超时");
    }

    private async Task<bool> OpenChestAfterCombat(Dalamud.Game.ClientState.Objects.Types.IGameObject coffer, CancellationToken token)
    {
        try
        {
            // 再次交互宝箱开箱
            if (!GameObjectHelper.IsInInteractRange(coffer, 3.0f))
            {
                VnavmeshHelper.PathfindAndMoveTo(coffer.Position);
                var moveTimeout = TimeSpan.FromSeconds(15);
                var moveStart = DateTime.Now;
                while (!GameObjectHelper.IsInInteractRange(coffer, 3.0f) && (DateTime.Now - moveStart) < moveTimeout)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(200, token);
                }
                VnavmeshHelper.StopAutoRunning();
            }

            GameObjectHelper.InteractWithObject(coffer);
            OnLog?.Invoke("已开箱");
            await Task.Delay(1000, token);
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
        await Task.Delay(1000, token);

        var portal = GameObjectHelper.GetPortalTransferCircle();
        if (portal != null)
        {
            OnLog?.Invoke("传送门已出现!");
            return true;
        }

        OnLog?.Invoke("无传送门，本张图结束");
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
            var timeout = TimeSpan.FromSeconds(30);
            var start = DateTime.Now;
            while (!GameObjectHelper.IsInInteractRange(obj, 3.0f) && (DateTime.Now - start) < timeout)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(200, token);
            }
            VnavmeshHelper.StopAutoRunning();
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
