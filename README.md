# TreasureHunt - FF14 自动挖宝插件

Dalamud 插件，为 FF14 国服 (CN) 提供藏宝图全流程自动化。

## 功能概览

### 核心功能 (可勾选开关)

1. **不选中他人宝箱怪** - 自动跳过其他玩家的宝箱怪物
2. **解读后标记位置** - 解读藏宝图后在大地图标记位置
3. **一键买图解读** - 交易板购买 + 解读 + 标记 + 传送 一键完成
4. **自动传送** - 解读后自动传送到最近的晶石
5. **TP 钱袋子** - 奖励房内自动瞬移收集闪亮袋子 (90秒/100个)

### 全自动流程

```
购买藏宝图 → 解读 → 传送 → 导航到点位 → 挖掘
→ 触发怪物 → (战斗插件处理) → 开箱
→ 检查传送门
  ├─ 出洞 → 进洞 → 交互机关 → (战斗) → 开箱 → roll点 → 下一层
  │         └─ 触发奖励房 → TP 钱袋子自动收集
  └─ 无洞 → 本张图结束
```

### 支持的藏宝图

- **Timeworn Gargantuaskin Map (G18)** → Vault Oneiron (7.3 新洞)
- 所有 8 个固定点位 (Living Memory 区域)

## 依赖插件

| 插件 | 用途 | 仓库 |
|------|------|------|
| vnavmesh | 自动寻路和避障 | 国服: AtmoOmen/DalamudPlugins |
| Rotation Solver Reborn / BossMod | 自动战斗 | 手动安装 |
| Kapture | roll 点监控 | DalamudPlugins |

## 开发环境搭建

### 1. 安装 .NET SDK

1. 下载 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. 运行安装程序
3. 验证: `dotnet --version`

### 2. 安装 Visual Studio

1. 下载 [Visual Studio Community](https://visualstudio.microsoft.com/downloads/)
2. 安装时选择 ".NET 桌面开发" 工作负载
3. 或使用 [JetBrains Rider](https://www.jetbrains.com/rider/) (推荐)

### 3. 安装 XIVLauncher (国服)

1. 下载 [XIVLauncher CN](https://github.com/AtmoOmen/FFXIVQuickLauncherCN)
2. 安装并登录
3. 在设置中启用 Dalamud

### 4. 配置 Dalamud 开发环境

1. 在游戏内输入 `/xldev` 打开开发菜单
2. 前往 Experimental → Dev Plugin Locations
3. 添加你的插件输出路径:
   ```
   C:\Path\To\TreasureHunt\TreasureHunt\bin\Debug\
   ```
4. 在 DevPlugins 列表中点击 Load 加载插件

### 5. 构建插件

```bash
cd TreasureHunt
dotnet build -c Release
```

输出 DLL 位于 `TreasureHunt/bin/Release/` 目录。

## 项目结构

```
TreasureHunt/
├── TreasureHunt.sln
└── TreasureHunt/
    ├── TreasureHunt.csproj          # 项目文件 (Dalamud.NET.Sdk)
    ├── TreasureHunt.json            # 插件元数据
    ├── Plugin.cs                     # 入口点
    ├── Configuration.cs              # 配置 (所有开关项)
    ├── Models/
    │   ├── TreasureMapData.cs        # 藏宝图数据模型
    │   ├── TreasureMapLocation.cs    # 点位位置模型
    │   └── PortalDungeonState.cs    # 洞内状态追踪
    ├── Services/
    │   ├── MapPurchaseService.cs     # 交易板自动购买
    │   ├── MapDecipherService.cs     # 自动解读+标记
    │   ├── NavigationService.cs      # 传送+vnavmesh导航
    │   ├── TreasureCofferService.cs  # 挖掘+宝箱交互
    │   ├── PortalDungeonService.cs   # 进洞+下一层
    │   ├── MoneyBagService.cs        # TP钱袋子奖励房
    │   └── TreasureHuntOrchestrator.cs # 全流程编排器
    ├── Helpers/
    │   ├── MapLocationDatabase.cs    # G18 固定点位数据库
    │   ├── GameObjectHelper.cs       # 游戏对象查找/交互
    │   ├── AetheryteHelper.cs        # 晶石查找/传送
    │   └── VnavmeshHelper.cs         # vnavmesh IPC集成
    ├── Windows/
    │   ├── MainWindow.cs             # 主控制面板
    │   └── ConfigWindow.cs           # 设置窗口
    └── Data/
        └── gargantuaskin_locations.json # 8个点位数据
```

## 使用方法

1. 在游戏内输入 `/thunt` 打开主面板
2. 勾选需要的功能开关
3. 点击"启动全自动挖宝"或单独功能按钮
4. 确保依赖插件 (vnavmesh 等) 已安装并运行

## Gargantuaskin 藏宝图点位

| # | 坐标 (X, Y) | 最近水晶 |
|---|-------------|----------|
| 1 | (9.3, 10.5) | 记忆节点·风 |
| 2 | (10.3, 35.9) | 记忆节点·记忆 |
| 3 | (18.6, 15.8) | 记忆节点·风 |
| 4 | (20.2, 23.0) | 记忆节点·风 |
| 5 | (25.0, 6.9) | 记忆节点·风 |
| 6 | (31.0, 18.1) | 记忆节点·火 |
| 7 | (38.5, 28.1) | 记忆节点·火 |
| 8 | (35.0, 35.3) | 记忆节点·记忆 |

## 注意事项

- 需要配合战斗插件 (RSR/BossMod) 使用
- 交易板购买需要在市场区域附近
- TP 钱袋子功能需要洞内触发特殊梦境 (宝箱图案3连)
- 被踢出 (强制退出) 会自动检测并停止
