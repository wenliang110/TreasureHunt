using System;
using Dalamud.Configuration;

namespace TreasureHunt;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // 核心功能开关
    public bool EnableAutoPurchase { get; set; } = false;
    public bool EnableMarkLocation { get; set; } = true;
    public bool EnableOneClickBuyDecipher { get; set; } = false;
    public bool EnableAutoTeleport { get; set; } = true;
    public bool EnableMoneyBagCollection { get; set; } = true;
    public bool AvoidOthersTreasureMonsters { get; set; } = true;

    // 全流程自动化
    public bool EnableFullAutoMode { get; set; } = false;

    // 交易板购买设置
    public int MaxPurchasePrice { get; set; } = 50000;
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
