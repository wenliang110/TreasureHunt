using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace TreasureHunt.Helpers;

public static unsafe class AetheryteHelper
{
    public static List<(uint aetheryteId, string name, Vector3 position)> GetNearbyAetherytes(Vector3 targetPos)
    {
        var result = new List<(uint aetheryteId, string name, Vector3 position)>();

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte) continue;

            var aetheryte = obj as IGameObject;
            if (aetheryte == null) continue;

            var name = aetheryte.Name.ToString();
            var pos = aetheryte.Position;
            result.Add((aetheryte.BaseId, name, pos));
        }

        result.Sort((a, b) =>
            Vector3.Distance(targetPos, a.position).CompareTo(Vector3.Distance(targetPos, b.position)));

        return result;
    }

    public static (uint aetheryteId, string name, Vector3 position)? GetNearestAetheryte(Vector3 targetPos)
    {
        var aetherytes = GetNearbyAetherytes(targetPos);
        return aetherytes.Count > 0 ? aetherytes[0] : null;
    }

    public static unsafe bool TeleportToAetheryte(uint aetheryteId)
    {
        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return false;

            telepo->Teleport(aetheryteId, 0);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"传送失败: {ex.Message}");
            return false;
        }
    }

    public static bool TeleportToNearestAetheryte(Vector3 targetPos)
    {
        var nearest = GetNearestAetheryte(targetPos);
        if (nearest == null)
        {
            Plugin.Log.Warning("未找到附近的水晶");
            return false;
        }

        Plugin.Log.Information($"传送到最近的水晶: {nearest.Value.name}");
        return TeleportToAetheryte(nearest.Value.aetheryteId);
    }

    public static bool IsTeleporting()
    {
        return Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
               Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51];
    }

    public static bool HasAetheryteTicket()
    {
        // 检查是否有传送网使用券
        var inventory = InventoryManager.Instance();
        if (inventory == null) return false;

        // 传送网使用券的 ItemId
        const uint aetheryteTicketId = 21073;
        var count = inventory->GetInventoryItemCount(aetheryteTicketId);
        return count > 0;
    }
}
