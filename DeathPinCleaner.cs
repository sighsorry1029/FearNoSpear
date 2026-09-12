using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
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

        long ownerId = ReflectionCache.GetTombstoneOwner(tombstone);
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

        long ownerId = ReflectionCache.GetTombstoneOwner(tombstone);
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
        Container? container = ReflectionCache.GetTombstoneContainer(tombstone);
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

[HarmonyPatch(typeof(TombStone), nameof(TombStone.Interact),
    new[] { typeof(Humanoid), typeof(bool), typeof(bool) })]
internal static class TombStoneOwnerOnlyInteractPatch
{
    private static bool Prefix(TombStone __instance, Humanoid __0, bool __1, ref bool __result)
    {
        if (!FearNoSpearPlugin.Cfg.Enabled.Value ||
            !FearNoSpearPlugin.Cfg.OwnerOnlyTombstones.Value)
        {
            return true;
        }

        long ownerId = ReflectionCache.GetTombstoneOwner(__instance);
        if (ownerId == 0L)
        {
            return true;
        }

        Player? player = __0 as Player;
        if (player != null &&
            (player.GetPlayerID() == ownerId ||
             HasAdminDebugBypass(player) ||
             ClanTombstoneCompatibility.IsMemberOfLocalClan(player, ownerId)))
        {
            return true;
        }

        if (!__1 && __0 != null)
        {
            __0.Message(MessageHud.MessageType.Center, "$msg_cantpickup");
        }

        __result = false;
        return false;
    }

    private static bool HasAdminDebugBypass(Player player)
    {
        return player == Player.m_localPlayer &&
               Player.m_debugMode &&
               Console.instance != null &&
               Console.instance.IsCheatsEnabled() &&
               ZNet.instance != null &&
               ZNet.instance.LocalPlayerIsAdminOrHost();
    }
}

internal static class ClanTombstoneCompatibility
{
    internal const string PluginGuid = "sighsorry.Clan";
    private const int MinimumApiVersion = 5;

    private static bool _initialized;
    private static bool _available;
    private static PropertyInfo? _currentProperty;
    private static PropertyInfo? _isReadyProperty;
    private static PropertyInfo? _membersProperty;
    private static PropertyInfo? _memberPlayerIdProperty;

    internal static bool IsMemberOfLocalClan(Player player, long ownerId)
    {
        if (player == null || player != Player.m_localPlayer || ownerId == 0L)
        {
            return false;
        }

        EnsureInitialized();
        if (!_available)
        {
            return false;
        }

        try
        {
            object? snapshot = _currentProperty!.GetValue(null, null);
            if (snapshot == null ||
                _isReadyProperty!.GetValue(snapshot, null) is not bool isReady ||
                !isReady ||
                _membersProperty!.GetValue(snapshot, null) is not IEnumerable members)
            {
                return false;
            }

            foreach (object? member in members)
            {
                if (member != null &&
                    _memberPlayerIdProperty!.GetValue(member, null) is long memberPlayerId &&
                    memberPlayerId == ownerId)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _available = false;
            FearNoSpearPlugin.Log.LogWarning(
                $"Clan tombstone compatibility was disabled after an API read failed: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out BepInEx.PluginInfo pluginInfo) ||
            pluginInfo.Instance == null)
        {
            return;
        }

        try
        {
            Assembly assembly = pluginInfo.Instance.GetType().Assembly;
            Type? apiType = assembly.GetType("Clan.ClanApi", throwOnError: false);
            FieldInfo? apiVersionField = apiType?.GetField(
                "ApiVersion",
                BindingFlags.Public | BindingFlags.Static);
            int apiVersion = apiVersionField?.GetValue(null) is int value ? value : 0;
            if (apiVersion < MinimumApiVersion)
            {
                FearNoSpearPlugin.Log.LogWarning(
                    $"Clan tombstone compatibility requires Clan API v{MinimumApiVersion} or newer; found v{apiVersion}.");
                return;
            }

            _currentProperty = apiType!.GetProperty(
                "Current",
                BindingFlags.Public | BindingFlags.Static);
            Type? snapshotType = _currentProperty?.PropertyType;
            _isReadyProperty = snapshotType?.GetProperty(
                "IsReady",
                BindingFlags.Public | BindingFlags.Instance);
            _membersProperty = snapshotType?.GetProperty(
                "Members",
                BindingFlags.Public | BindingFlags.Instance);
            Type? memberType = _membersProperty?.PropertyType.IsGenericType == true
                ? _membersProperty.PropertyType.GetGenericArguments().FirstOrDefault()
                : assembly.GetType("Clan.ClanMemberInfo", throwOnError: false);
            _memberPlayerIdProperty = memberType?.GetProperty(
                "PlayerId",
                BindingFlags.Public | BindingFlags.Instance);

            _available = _currentProperty != null &&
                         _isReadyProperty?.PropertyType == typeof(bool) &&
                         _membersProperty != null &&
                         _memberPlayerIdProperty?.PropertyType == typeof(long);
            if (!_available)
            {
                FearNoSpearPlugin.Log.LogWarning(
                    "Clan tombstone compatibility could not bind the required Clan API members.");
            }
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning(
                $"Clan tombstone compatibility could not initialize: {ex.GetType().Name}: {ex.Message}");
        }
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
