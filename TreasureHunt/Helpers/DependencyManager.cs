using System;
using System.Collections.Generic;

namespace TreasureHunt.Helpers;

/// <summary>
/// 依赖插件类型
/// </summary>
public enum DependencyType
{
    /// <summary>vnavmesh - 自动导航（必需）</summary>
    Vnavmesh,
    /// <summary>LazyLoot - 自动 Roll 点（可选）</summary>
    LazyLoot,
    /// <summary>BossMod / BossModReborn - 战斗辅助（可选）</summary>
    BossMod,
    /// <summary>RotationSolver / WrathCombo - 自动循环（可选）</summary>
    RotationSolver,
    /// <summary>Kapture - Roll 点（旧版/备选）</summary>
    Kapture,
}

/// <summary>
/// 依赖插件状态
/// </summary>
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
/// 参考 AutoDuty 的 IPC 订阅模式，统一管理所有外部插件依赖
/// </summary>
public static class DependencyManager
{
    private static readonly Dictionary<DependencyType, DependencyStatus> _statuses = new();
    private static DateTime _lastCheckTime = DateTime.MinValue;
    private static readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 静态构造函数：初始化所有依赖状态
    /// </summary>
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
            Note = "自动 Roll 点"
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
            Note = "Roll 点（备选）"
        };
    }

    /// <summary>
    /// 获取所有依赖状态（带缓存）
    /// </summary>
    public static IReadOnlyDictionary<DependencyType, DependencyStatus> GetAllStatuses()
    {
        RefreshIfNeeded();
        return _statuses;
    }

    /// <summary>
    /// 获取单个依赖状态
    /// </summary>
    public static DependencyStatus GetStatus(DependencyType type)
    {
        RefreshIfNeeded();
        return _statuses[type];
    }

    /// <summary>
    /// 检查依赖是否可用
    /// </summary>
    public static bool IsAvailable(DependencyType type)
    {
        RefreshIfNeeded();
        return _statuses[type].IsAvailable;
    }

    /// <summary>
    /// 刷新所有依赖状态
    /// </summary>
    private static void RefreshIfNeeded()
    {
        if ((DateTime.Now - _lastCheckTime) < _cacheDuration)
            return;

        _lastCheckTime = DateTime.Now;

        // 检查 vnavmesh
        _statuses[DependencyType.Vnavmesh].IsAvailable = CheckVnavmesh();

        // 检查 LazyLoot
        _statuses[DependencyType.LazyLoot].IsAvailable = CheckLazyLoot();

        // 检查 BossMod
        _statuses[DependencyType.BossMod].IsAvailable = CheckBossMod();

        // 检查 RotationSolver
        _statuses[DependencyType.RotationSolver].IsAvailable = CheckRotationSolver();

        // 检查 Kapture
        _statuses[DependencyType.Kapture].IsAvailable = CheckKapture();
    }

    #region 各依赖检查方法

    /// <summary>
    /// 检查 vnavmesh 是否可用
    /// </summary>
    private static bool CheckVnavmesh()
    {
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            return sub.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查 LazyLoot 是否可用
    /// LazyLoot 使用 InternalName "LazyLoot"
    /// 通过检查 IPC 或插件列表来判断
    /// </summary>
    private static bool CheckLazyLoot()
    {
        try
        {
            // 方式1：检查 LazyLoot 是否安装并启用（通过 Dalamud 插件管理器）
            // 方式2：尝试调用 LazyLoot 的 IPC（如果有的话）
            // 方式3：通过检查插件内部名

            // 优先尝试 IPC 方式（如果 LazyLoot 提供了版本查询）
            try
            {
                var versionSub = Plugin.PluginInterface.GetIpcSubscriber<string>("LazyLoot.Version");
                var version = versionSub.InvokeFunc();
                if (!string.IsNullOrEmpty(version))
                {
                    _statuses[DependencyType.LazyLoot].Version = version;
                    return true;
                }
            }
            catch { /* IPC 不存在，继续尝试其他方式 */ }

            // 备用：检查插件是否在已安装列表中
            // 通过 Dalamud 的 PluginInstaller 来检查
            try
            {
                var installerType = Plugin.PluginInterface.GetType().Assembly.GetType("Dalamud.Plugin.Internal.PluginManager")
                    ?? Plugin.PluginInterface.GetType().Assembly.GetType("Dalamud.PluginService")
                    ?? Plugin.PluginInterface.GetType().Assembly.GetType("Dalamud.DalamudPluginManager");

                if (installerType != null)
                {
                    // 尝试通过反射获取已安装插件列表
                    // 这是一个较复杂的操作，作为备选方案
                }
            }
            catch { }

            // 最简化的方式：尝试执行 /lazyloot 命令看看插件是否响应
            // 但这样会产生副作用，不推荐
            // 所以我们用另一种方式：检查插件的 IPC 订阅者是否存在

            // 通过 PluginInterface 的 IPC 机制间接检测
            // 如果插件已加载，它会注册一些 IPC provider
            // 我们可以尝试探测一个常见的 IPC

            // 暂时使用一个简单的检测：检查是否有 LazyLoot 相关的 IPC
            // 如果 GetIpcSubscriber 调用不抛异常且返回合理值，说明插件可能存在
            try
            {
                // 尝试获取 FULF 状态（LazyLoot 的核心功能）
                var fulfSub = Plugin.PluginInterface.GetIpcSubscriber<bool>("LazyLoot.FulfEnabled");
                var _ = fulfSub.InvokeFunc();
                return true;
            }
            catch
            {
                // IPC 调用失败，可能插件不存在或版本不同
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查 BossMod / BossModReborn 是否可用
    /// </summary>
    private static bool CheckBossMod()
    {
        try
        {
            // 尝试 BossModReborn
            try
            {
                var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>("BossModReborn.Enabled");
                return sub.InvokeFunc();
            }
            catch { }

            // 尝试 Veyn's Boss Mod
            try
            {
                var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.Enabled");
                return sub.InvokeFunc();
            }
            catch { }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查 RotationSolver / WrathCombo 是否可用
    /// </summary>
    private static bool CheckRotationSolver()
    {
        try
        {
            // 尝试 RotationSolver Reborn
            try
            {
                var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>("RotationSolver.Enabled");
                return sub.InvokeFunc();
            }
            catch { }

            // 尝试 Wrath Combo / RSR
            try
            {
                var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>("RSR.Enabled");
                return sub.InvokeFunc();
            }
            catch { }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查 Kapture 是否可用（备选 roll 点插件）
    /// </summary>
    private static bool CheckKapture()
    {
        try
        {
            var sub = Plugin.PluginInterface.GetIpcSubscriber<bool>("Kapture.Enabled");
            return sub.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    #endregion

    /// <summary>
    /// 强制刷新所有依赖状态
    /// </summary>
    public static void ForceRefresh()
    {
        _lastCheckTime = DateTime.MinValue;
        RefreshIfNeeded();
    }
}
