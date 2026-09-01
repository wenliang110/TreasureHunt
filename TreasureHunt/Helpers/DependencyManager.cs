using System;
using System.Collections.Generic;
using System.Linq;

namespace TreasureHunt.Helpers;

public enum DependencyType
{
    Vnavmesh,
    LazyLoot,
    BossMod,
    RotationSolver,
    Kapture,
    BetterMarketBoard,
}

public class DependencyStatus
{
    public DependencyType Type { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public bool IsRequired { get; set; }
    public string? Version { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// 依赖插件管理器
/// 使用 Assembly 加载检测 + IPC 双重方式判断插件是否可用
/// </summary>
public static class DependencyManager
{
    private static readonly Dictionary<DependencyType, DependencyStatus> _statuses = new();
    private static DateTime _lastCheckTime = DateTime.MinValue;
    private static readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(2);

    static DependencyManager()
    {
        _statuses[DependencyType.Vnavmesh] = new DependencyStatus
        {
            Type = DependencyType.Vnavmesh,
            DisplayName = "vnavmesh",
            IsRequired = true,
            Note = "自动导航"
        };
        _statuses[DependencyType.LazyLoot] = new DependencyStatus
        {
            Type = DependencyType.LazyLoot,
            DisplayName = "LazyLoot",
            IsRequired = false,
            Note = "自动Roll点"
        };
        _statuses[DependencyType.BossMod] = new DependencyStatus
        {
            Type = DependencyType.BossMod,
            DisplayName = "BossMod",
            IsRequired = false,
            Note = "战斗辅助"
        };
        _statuses[DependencyType.RotationSolver] = new DependencyStatus
        {
            Type = DependencyType.RotationSolver,
            DisplayName = "RSR",
            IsRequired = false,
            Note = "自动循环"
        };
        _statuses[DependencyType.Kapture] = new DependencyStatus
        {
            Type = DependencyType.Kapture,
            DisplayName = "Kapture",
            IsRequired = false,
            Note = "Roll点(备选)"
        };
        _statuses[DependencyType.BetterMarketBoard] = new DependencyStatus
        {
            Type = DependencyType.BetterMarketBoard,
            DisplayName = "更好的市场布告板",
            IsRequired = false,
            Note = "远程交易板买图 (/pdr market)"
        };
    }

    public static IReadOnlyDictionary<DependencyType, DependencyStatus> GetAllStatuses()
    {
        RefreshIfNeeded();
        return _statuses;
    }

    public static DependencyStatus GetStatus(DependencyType type)
    {
        RefreshIfNeeded();
        return _statuses[type];
    }

    public static bool IsAvailable(DependencyType type)
    {
        RefreshIfNeeded();
        return _statuses[type].IsAvailable;
    }

    private static void RefreshIfNeeded()
    {
        if ((DateTime.Now - _lastCheckTime) < _cacheDuration)
            return;

        _lastCheckTime = DateTime.Now;
        _statuses[DependencyType.Vnavmesh].IsAvailable = CheckVnavmesh();
        _statuses[DependencyType.LazyLoot].IsAvailable = CheckLazyLoot();
        _statuses[DependencyType.BossMod].IsAvailable = CheckBossMod();
        _statuses[DependencyType.RotationSolver].IsAvailable = CheckRotationSolver();
        _statuses[DependencyType.Kapture].IsAvailable = CheckKapture();
        _statuses[DependencyType.BetterMarketBoard].IsAvailable = CheckBetterMarketBoard();
    }

    /// <summary>
    /// 检查 Assembly 是否已加载（最可靠的插件检测方式）
    /// </summary>
    private static bool IsAssemblyLoaded(string assemblyName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.GetName().Name?.Equals(assemblyName, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool CheckVnavmesh()
    {
        if (IsAssemblyLoaded("vnavmesh"))
            return true;
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            return sub.InvokeFunc();
        }
        catch { return false; }
    }

    private static bool CheckLazyLoot()
    {
        // LazyLoot 不提供 IPC，用 Assembly 检测
        return IsAssemblyLoaded("LazyLoot");
    }

    private static bool CheckBossMod()
    {
        // BossMod Reborn CN / 原版 BossMod
        return IsAssemblyLoaded("BossMod") ||
               IsAssemblyLoaded("BossModReborn") ||
               IsAssemblyLoaded("BossModRebornCN");
    }

    private static bool CheckRotationSolver()
    {
        return IsAssemblyLoaded("RotationSolver") ||
               IsAssemblyLoaded("RotationSolverReborn") ||
               IsAssemblyLoaded("WrathCombo");
    }

    private static bool CheckKapture()
    {
        return IsAssemblyLoaded("Kapture");
    }

    private static bool CheckBetterMarketBoard()
    {
        // 更好的市场布告板 (PandaDutyReborn / BetterMarketBoard)
        // 插件名可能是 PandaDutyReborn 或 BetterMarketBoard
        return IsAssemblyLoaded("PandaDutyReborn") ||
               IsAssemblyLoaded("BetterMarketBoard") ||
               IsAssemblyLoaded("PandaDuty");
    }

    public static void ForceRefresh()
    {
        _lastCheckTime = DateTime.MinValue;
        RefreshIfNeeded();
    }
}
