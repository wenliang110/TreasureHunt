using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TreasureHunt.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private int _selectedTab = 0;

    public ConfigWindow(Plugin plugin) : base("TreasureHunt 设置###TreasureHuntConfig")
    {
        Size = new Vector2(480, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
        _plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("ConfigTabs"))
        {
            DrawGeneralTab();
            DrawPurchaseTab();
            DrawNavigationTab();
            DrawMoneyBagTab();
            DrawAdvancedTab();

            ImGui.EndTabBar();
        }

        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("保存设置", new Vector2(-1, 30)))
        {
            _plugin.Configuration.ValidateAndFix();
            _plugin.Configuration.Save();
        }
    }

    private void DrawGeneralTab()
    {
        if (!ImGui.BeginTabItem("通用")) return;

        var config = _plugin.Configuration;
        ImGui.Text("核心功能开关");
        ImGui.Spacing();

        ImGui.Checkbox("自动购买藏宝图", ref config.EnableAutoPurchase);
        ImGui.Checkbox("解读后标记位置", ref config.EnableMarkLocation);
        ImGui.Checkbox("一键买图解读", ref config.EnableOneClickBuyDecipher);
        ImGui.Checkbox("自动传送", ref config.EnableAutoTeleport);
        ImGui.Checkbox("TP 钱袋子自动收集", ref config.EnableMoneyBagCollection);
        ImGui.Checkbox("不选中他人宝箱怪", ref config.AvoidOthersTreasureMonsters);
        ImGui.Checkbox("全自动模式", ref config.EnableFullAutoMode);

        ImGui.Separator();
        ImGui.Text("交互设置");
        ImGui.Spacing();

        var delay = config.InteractionDelay;
        if (ImGui.DragInt("交互延迟 (ms)", ref delay, 10, 100, 5000))
        {
            config.InteractionDelay = delay;
        }

        var combatDelay = config.CombatWaitDelay;
        if (ImGui.DragInt("战斗等待延迟 (ms)", ref combatDelay, 10, 500, 10000))
        {
            config.CombatWaitDelay = combatDelay;
        }

        ImGui.EndTabItem();
    }

    private void DrawPurchaseTab()
    {
        if (!ImGui.BeginTabItem("购买设置")) return;

        var config = _plugin.Configuration;
        ImGui.Text("交易板购买设置");
        ImGui.Spacing();

        var maxPrice = (int)config.MaxPurchasePrice;
        if (ImGui.DragInt("最高购买价格 (Gil)", ref maxPrice, 100, 0, 10000000))
        {
            config.MaxPurchasePrice = maxPrice;
        }

        var maxQty = config.MaxPurchaseQuantity;
        if (ImGui.DragInt("最大购买数量", ref maxQty, 1, 1, 8))
        {
            config.MaxPurchaseQuantity = maxQty;
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "购买流程: 打开交易板 → 搜索 → 选最低价 → 确认购买");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "注意: 需要在交易板附近使用");

        ImGui.EndTabItem();
    }

    private void DrawNavigationTab()
    {
        if (!ImGui.BeginTabItem("导航设置")) return;

        var config = _plugin.Configuration;
        ImGui.Text("传送设置");
        ImGui.Spacing();

        ImGui.Checkbox("使用传送网使用券", ref config.UseTeleportTicket);

        var gilThreshold = config.TeleportGilThreshold;
        if (ImGui.DragInt("传送保留 Gil 下限", ref gilThreshold, 100, 0, 10000000))
        {
            config.TeleportGilThreshold = gilThreshold;
        }

        ImGui.Separator();
        ImGui.Text("vnavmesh 导航设置");
        ImGui.Spacing();

        var stopDist = config.NavigationStopDistance;
        if (ImGui.DragFloat("到达判定距离", ref stopDist, 0.1f, 0.5f, 20.0f))
        {
            config.NavigationStopDistance = stopDist;
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "导航依赖: vnavmesh 插件");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "vnavmesh 国服仓库: AtmoOmen/DalamudPlugins");

        ImGui.EndTabItem();
    }

    private void DrawMoneyBagTab()
    {
        if (!ImGui.BeginTabItem("钱袋子设置")) return;

        var config = _plugin.Configuration;
        ImGui.Text("TP 钱袋子奖励房设置");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.85f, 0.65f, 0.0f, 1.0f), "目标: 90秒内收集 100 个闪亮袋子");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "金色袋子 = 3倍计数，优先收集");
        ImGui.Spacing();

        ImGui.Checkbox("自动躲避 AOE", ref config.MoneyBagDodgeAoe);

        var scanInterval = config.MoneyBagScanInterval;
        if (ImGui.DragInt("扫描间隔 (ms)", ref scanInterval, 10, 50, 1000))
        {
            config.MoneyBagScanInterval = scanInterval;
        }

        var collectRange = config.MoneyBagCollectRange;
        if (ImGui.DragFloat("收集范围 (m)", ref collectRange, 1.0f, 5.0f, 100.0f))
        {
            config.MoneyBagCollectRange = collectRange;
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "收集策略: 金色优先 → 距离最近 → 瞬移收集");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "AOE 躲避: 检测敌人位置 → 计算安全方向 → 瞬移脱离");

        ImGui.EndTabItem();
    }

    private void DrawAdvancedTab()
    {
        if (!ImGui.BeginTabItem("高级")) return;

        var config = _plugin.Configuration;
        ImGui.Text("高级设置");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.9f, 0.2f, 0.2f, 1.0f), "警告: 以下设置可能影响游戏稳定性");

        ImGui.Separator();
        ImGui.Text("Gargantuaskin 藏宝图点位");
        ImGui.Spacing();

        var locations = TreasureHunt.Helpers.MapLocationDatabase.GetLocations();
        if (ImGui.BeginTable("LocationTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("#");
            ImGui.TableSetupColumn("坐标 X");
            ImGui.TableSetupColumn("坐标 Y");
            ImGui.TableSetupColumn("最近水晶");
            ImGui.TableHeadersRow();

            foreach (var loc in locations)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(loc.Id.ToString());
                ImGui.TableSetColumnIndex(1);
                ImGui.Text(loc.MapX.ToString("F1"));
                ImGui.TableSetColumnIndex(2);
                ImGui.Text(loc.MapY.ToString("F1"));
                ImGui.TableSetColumnIndex(3);
                ImGui.Text(loc.NearestAetheryteNameCN);
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"配置版本: {config.Version}");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"插件版本: 0.1.0.0");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Dalamud API Level: 13");

        ImGui.EndTabItem();
    }
}
