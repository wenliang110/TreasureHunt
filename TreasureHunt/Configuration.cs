using System;
using Dalamud.Configuration;

namespace TreasureHunt;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // 核心功能开关 (与参考图一致)
    public bool AvoidOthersTreasureMonsters { get; set; } = true;   // 1. 不选中他人宝箱怪
    public bool EnableMarkLocation { get; set; } = true;             // 2. 解读后标记位置
    public bool EnableOneClickBuyDecipher { get; set; } = false;     // 3. 一键买图解读
    public bool EnableUnlimitedDigging { get; set; } = false;        // 4. 无限挖掘
    public bool EnableAutoTeleport { get; set; } = true;             // 5. 自动传送
    public bool EnableMoneyBagCollection { get; set; } = false;      // 6. TP 钱袋 (默认关)

    // 全流程自动化
    public bool EnableFullAutoMode { get; set; } = false;
    public bool EnableAutoPurchase { get; set; } = false;

    // 交易板购买设置
    public bool UsePdrMarket { get; set; } = true;           // 使用 PDR 远程交易板 (无需跑主城)
    public int MaxPurchasePrice { get; set; } = 50000;       // 价格限制
    public uint TreasureMapItemId { get; set; } = 46185; // 藏宝图ID (国服陈旧的卡冈图亚革地图)
    public int MaxPurchaseQuantity { get; set; } = 1;

    // 传送设置
    public bool UseTeleportTicket { get; set; } = false;
    public int TeleportGilThreshold { get; set; } = 999;

    // TP 钱袋子设置
    public int MoneyBagScanInterval { get; set; } = 100;
    public float MoneyBagCollectRange { get; set; } = 30.0f;
    public bool MoneyBagDodgeAoe { get; set; } = true;

    // 导航设置
    public string VnavmeshPath { get; set; } = string.Empty;
    public float NavigationStopDistance { get; set; } = 3.0f;

    // 交互延迟（毫秒）
    public int InteractionDelay { get; set; } = 500;
    public int CombatWaitDelay { get; set; } = 1000;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    public void ValidateAndFix()
    {
        if (MaxPurchasePrice < 0) MaxPurchasePrice = 50000;
        if (MaxPurchaseQuantity < 1) MaxPurchaseQuantity = 1;
        if (MaxPurchaseQuantity > 8) MaxPurchaseQuantity = 8;
        if (MoneyBagScanInterval < 50) MoneyBagScanInterval = 50;
        if (MoneyBagCollectRange < 5.0f) MoneyBagCollectRange = 30.0f;
        if (NavigationStopDistance < 0.5f) NavigationStopDistance = 3.0f;
        if (InteractionDelay < 100) InteractionDelay = 500;
        if (CombatWaitDelay < 500) CombatWaitDelay = 1000;
    }
}
