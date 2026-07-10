using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FearNoSpear;

internal static class DeathPinCleaner
{
    private const float RecentDeathSeconds = 8f;
    private const float DeathPinRemoveRadius = 32f;

    private static readonly Dictionary<int, Vector3> TombstoneDeathPositions = new();
    private static readonly HashSet<int> CleanedTombstones = new();
    private static PendingDeath? _pendingDeath;

    internal static void Clear()
    {
        TombstoneDeathPositions.Clear();
        CleanedTombstones.Clear();
        _pendingDeath = null;
    }

    internal static void BeginLocalDeath(Player player)
    {
        if (!IsEnabled()) return;
        if (player == null || player != Player.m_localPlayer) return;

        long playerId = GetLocalPlayerId();
        if (playerId == 0L) return;

        _pendingDeath = new PendingDeath
        {
            PlayerId = playerId,
            Position = player.transform.position,
            StartedAt = Time.time
        };
    }

    internal static void UpdatePendingDeath()
    {
        PendingDeath? pendingDeath = _pendingDeath;
        if (pendingDeath == null) return;
        if (!IsEnabled())
        {
            _pendingDeath = null;
            return;
        }

        if (Time.time - pendingDeath.StartedAt < RecentDeathSeconds) return;

        _pendingDeath = null;
        RemoveNearestDeathPin(pendingDeath.Position);
    }

    internal static void RegisterTombstone(TombStone tombstone)
    {
        if (!IsEnabled()) return;
        if (tombstone == null) return;

        long ownerId = tombstone.GetOwner();
        if (ownerId == 0L || ownerId != GetLocalPlayerId()) return;

        int instanceId = tombstone.GetInstanceID();
        Vector3 pinPosition = GetTombstonePinPosition(tombstone);

        PendingDeath? pendingDeath = _pendingDeath;
        if (pendingDeath != null &&
            pendingDeath.PlayerId == ownerId &&
            Time.time - pendingDeath.StartedAt <= RecentDeathSeconds &&
            Vector3.SqrMagnitude(pinPosition - pendingDeath.Position) <= DeathPinRemoveRadius * DeathPinRemoveRadius)
        {
            pinPosition = pendingDeath.Position;
            _pendingDeath = null;
        }

        TombstoneDeathPositions[instanceId] = pinPosition;
    }

    internal static void CleanForRecoveredTombstone(TombStone tombstone)
    {
        if (!IsEnabled()) return;
        if (!IsLocalTombstone(tombstone)) return;

        int instanceId = tombstone.GetInstanceID();
        if (!CleanedTombstones.Add(instanceId)) return;

        Vector3 pinPosition = TombstoneDeathPositions.TryGetValue(instanceId, out Vector3 knownPosition)
            ? knownPosition
            : GetTombstonePinPosition(tombstone);

        TombstoneDeathPositions.Remove(instanceId);
        RemoveNearestDeathPin(pinPosition);
    }

    internal static void CleanIfTombstoneIsEmpty(TombStone tombstone)
    {
        if (!IsEnabled()) return;
        if (!IsLocalTombstone(tombstone)) return;
        if (!IsTombstoneEmpty(tombstone)) return;

        CleanForRecoveredTombstone(tombstone);
    }

    private static bool IsEnabled()
    {
        return FearNoSpearPlugin.Cfg.Enabled.Value &&
               FearNoSpearPlugin.Cfg.CleanDeathPins.Value;
    }

    private static bool IsLocalTombstone(TombStone tombstone)
    {
        if (tombstone == null) return false;

        long ownerId = tombstone.GetOwner();
        long localPlayerId = GetLocalPlayerId();
        return ownerId != 0L && ownerId == localPlayerId;
    }

    private static long GetLocalPlayerId()
    {
        Player? localPlayer = Player.m_localPlayer;
        if (localPlayer != null) return localPlayer.GetPlayerID();

        Game? game = Game.instance;
        PlayerProfile? profile = game != null ? game.GetPlayerProfile() : null;
        return profile != null ? profile.GetPlayerID() : 0L;
    }

    private static bool IsTombstoneEmpty(TombStone tombstone)
    {
        Container? container = tombstone.m_container;
        Inventory? inventory = container != null ? container.GetInventory() : null;
        return inventory != null && inventory.NrOfItems() <= 0;
    }

    private static Vector3 GetTombstonePinPosition(TombStone tombstone)
    {
        ZNetView nview = tombstone.GetComponent<ZNetView>();
        if (nview != null && nview.IsValid())
        {
            ZDO zdo = nview.GetZDO();
            if (zdo != null && zdo.IsValid())
            {
                return zdo.GetVec3(ZDOVars.s_spawnPoint, tombstone.transform.position);
            }
        }

        return tombstone.transform.position;
    }

    private static bool RemoveNearestDeathPin(Vector3 position)
    {
        Minimap minimap = Minimap.instance;
        if (minimap == null) return false;

        List<Minimap.PinData>? pins = ReflectionCache.F_minimapPins?.GetValue(minimap) as List<Minimap.PinData>;
        if (pins == null) return false;

        Minimap.PinData? deathPin = pins
            .Where(pin => pin != null && pin.m_save && pin.m_type == Minimap.PinType.Death)
            .Where(pin => Vector3.Distance(pin.m_pos, position) <= DeathPinRemoveRadius)
            .OrderBy(pin => Vector3.SqrMagnitude(pin.m_pos - position))
            .FirstOrDefault();
        if (deathPin == null) return false;

        minimap.RemovePin(deathPin);
        return true;
    }

    private sealed class PendingDeath
    {
        internal long PlayerId;
        internal Vector3 Position;
        internal float StartedAt;
    }
}

[HarmonyPatch(typeof(Player), "OnDeath")]
internal static class PlayerOnDeathDeathPinPatch
{
    private static void Prefix(Player __instance)
    {
        DeathPinCleaner.BeginLocalDeath(__instance);
    }

}

[HarmonyPatch(typeof(TombStone), "Setup", new[] { typeof(string), typeof(long) })]
internal static class TombStoneSetupDeathPinPatch
{
    private static void Postfix(TombStone __instance)
    {
        DeathPinCleaner.RegisterTombstone(__instance);
    }
}

[HarmonyPatch(typeof(TombStone), "OnTakeAllSuccess")]
internal static class TombStoneTakeAllDeathPinPatch
{
    private static void Postfix(TombStone __instance)
    {
        DeathPinCleaner.CleanForRecoveredTombstone(__instance);
    }
}

[HarmonyPatch(typeof(TombStone), "UpdateDespawn")]
internal static class TombStoneUpdateDespawnDeathPinPatch
{
    private static void Prefix(TombStone __instance)
    {
        DeathPinCleaner.CleanIfTombstoneIsEmpty(__instance);
    }
}
