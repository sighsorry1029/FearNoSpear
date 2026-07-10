using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FearNoSpear;

internal sealed class SpearSafetyTracker : MonoBehaviour
{
    private const string ZdoRescueClaimKey = "FearNoSpear.Rescued";
    private const float NearbyDropMatchRadius = 4f;
    private const float PendingDropTagSeconds = 2f;
    private const float PendingDropTagRetrySeconds = 0.1f;

    private static readonly List<PendingDropTag> PendingDropTags = new();
    private static float _nextPendingDropTagScanAt;

    private Projectile? _projectile;
    private bool _normalHit;
    private bool _rescueAttempted;
    private bool _lastKnownOwner;
    private float _lastOwnerStateTime = float.NegativeInfinity;
    private float _lastTtl;
    private Vector3 _lastPosition;
    private Vector3 _lastVelocity;
    private long _throwerPlayerId;

    internal static SpearSafetyTracker? GetOrArmIfTracked(Projectile projectile)
    {
        if (!SpearProjectileDetector.IsTrackedSpearProjectile(projectile)) return null;

        SpearSafetyTracker tracker = projectile.GetComponent<SpearSafetyTracker>();
        if (tracker != null) return tracker;

        tracker = projectile.gameObject.AddComponent<SpearSafetyTracker>();
        tracker.Arm(projectile);
        return tracker;
    }

    internal static void ClearPendingDropTags()
    {
        PendingDropTags.Clear();
        _nextPendingDropTagScanAt = 0f;
    }

    internal static void UpdatePendingDropTags()
    {
        if (PendingDropTags.Count == 0) return;
        if (Time.time < _nextPendingDropTagScanAt) return;
        _nextPendingDropTagScanAt = Time.time + PendingDropTagRetrySeconds;
        ItemDrop[] loadedDrops = FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);

        for (int i = PendingDropTags.Count - 1; i >= 0; --i)
        {
            PendingDropTag pending = PendingDropTags[i];
            if (Time.time > pending.ExpiresAt)
            {
                PendingDropTags.RemoveAt(i);
                continue;
            }

            ItemDrop? drop = FindBestMatchingNearbyDrop(pending.SpawnItem, pending.Position, loadedDrops);
            if (drop == null) continue;

            SpearThrowerMetadata.TryWriteToDrop(drop, pending.ThrowerPlayerId);
            PendingDropTags.RemoveAt(i);
        }
    }

    internal void Arm(Projectile projectile)
    {
        _projectile = projectile;
        _normalHit = ReflectionCache.Get(ReflectionCache.F_didHit, projectile, false);
        _rescueAttempted = false;
        _throwerPlayerId = 0L;
        RefreshState();

        EnsureThrowerMetadata();
        ExtendInitialTtlIfNeeded();
    }

    internal void MarkNormalHit()
    {
        if (_projectile != null)
        {
            RefreshState();
            TryCopyThrowerMetadataToNearbyDrop();
        }

        _normalHit = true;
    }

    internal bool TryRescue(string reason)
    {
        if (FearNoSpearPlugin.IsShuttingDown) return false;
        if (!FearNoSpearPlugin.Cfg.Enabled.Value) return false;
        if (_projectile == null || _rescueAttempted) return false;
        if (_normalHit || ReflectionCache.Get(ReflectionCache.F_didHit, _projectile, false)) return false;
        if (!SpearProjectileDetector.IsTrackedSpearProjectile(_projectile)) return false;
        if (!MayThisClientRescue()) return false;
        ItemDrop.ItemData? spawnItem = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_spawnItem, _projectile, null);
        if (spawnItem == null) return false;
        if (!TryClaimNetworkRescue(out NetworkRescueClaim claim)) return false;

        _rescueAttempted = true;
        bool rescueSatisfied = false;

        try
        {
            Vector3 normal = _lastVelocity.sqrMagnitude > 0.001f ? -_lastVelocity.normalized : Vector3.up;
            if (!TrySpawnOriginalItem(_projectile, spawnItem, normal, out ItemDrop rescuedDrop))
            {
                ReleaseNetworkRescueClaim(claim, "spawn failed");
                FearNoSpearPlugin.Log.LogWarning($"Could not rescue spear; Valheim SpawnOnHit and ItemDrop fallback were unavailable or failed. reason={reason}; {SpearProjectileDetector.DescribeProjectile(_projectile)}");
                return false;
            }
            rescueSatisfied = true;

            _normalHit = true;
            ReflectionCache.Set(ReflectionCache.F_didHit, _projectile, true);
            long throwerPlayerId = EnsureThrowerMetadata();
            if (throwerPlayerId != 0L)
            {
                SpearThrowerMetadata.TryWriteToDrop(rescuedDrop, throwerPlayerId);
            }

            Vector3 rescuedDropPosition = rescuedDrop.transform.position;
            FearNoSpearPlugin.Log.LogInfo($"Rescued thrown spear before projectile loss: reason={reason}; pos={rescuedDropPosition}; ttl={_lastTtl:0.00}; {SpearProjectileDetector.DescribeProjectile(_projectile)}");
            return true;
        }
        catch (Exception ex)
        {
            if (!rescueSatisfied)
            {
                ReleaseNetworkRescueClaim(claim, "exception before spawn completed");
            }

            FearNoSpearPlugin.Log.LogWarning($"Exception while rescuing thrown spear: reason={reason}; ex={ex}");
            return false;
        }
    }

    internal bool TryTtlRescueAndDestroyIfNeeded()
    {
        if (!FearNoSpearPlugin.Cfg.Enabled.Value) return false;
        if (_projectile == null) return false;

        RefreshState();

        float ttl = _lastTtl;
        float window = Mathf.Max(Time.fixedDeltaTime * 1.5f, FearNoSpearPlugin.Cfg.TtlRescueWindowSeconds.Value);
        if (ttl <= 0f || ttl > window) return false;

        if (!TryRescue("TTL expiry")) return false;

        if (ZNetScene.instance != null)
        {
            ZNetScene.instance.Destroy(_projectile.gameObject);
        }
        else
        {
            Destroy(_projectile.gameObject);
        }

        return true;
    }

    private void OnDestroy()
    {
        if (FearNoSpearPlugin.IsShuttingDown) return;
        TryRescue("OnDestroy fallback");
    }

    private void RefreshState()
    {
        if (_projectile == null) return;

        _lastPosition = _projectile.transform.position;
        _lastVelocity = ReflectionCache.Get(ReflectionCache.F_vel, _projectile, Vector3.zero);
        _lastTtl = ReflectionCache.Get(ReflectionCache.F_ttl, _projectile, 0f);

        ZNetView? nview = ReflectionCache.GetNView(_projectile);
        if (nview != null && nview.IsValid())
        {
            _lastKnownOwner = nview.IsOwner();
            _lastOwnerStateTime = Time.time;
        }

        EnsureThrowerMetadata();
    }

    private void ExtendInitialTtlIfNeeded()
    {
        if (_projectile == null) return;

        float ttl = ReflectionCache.Get(ReflectionCache.F_ttl, _projectile, 0f);
        if (ttl > 0f && ttl < FearNoSpearConfig.MinimumInitialTtlSeconds)
        {
            ReflectionCache.Set(ReflectionCache.F_ttl, _projectile, FearNoSpearConfig.MinimumInitialTtlSeconds);
            _lastTtl = FearNoSpearConfig.MinimumInitialTtlSeconds;
        }
    }

    private bool MayThisClientRescue()
    {
        if (_projectile == null) return false;

        ZNetView? nview = ReflectionCache.GetNView(_projectile);
        if (nview != null && nview.IsValid())
        {
            return nview.IsOwner();
        }

        float maxAge = Mathf.Max(0f, FearNoSpearPlugin.Cfg.LastKnownOwnerGraceSeconds.Value);
        float age = Time.time - _lastOwnerStateTime;
        bool allowLastKnown = FearNoSpearPlugin.Cfg.AllowLastKnownOwnerIfZNetViewInvalid.Value &&
                              _lastKnownOwner &&
                              age <= maxAge;
        return allowLastKnown;
    }

    private long EnsureThrowerMetadata()
    {
        if (_throwerPlayerId != 0L) return _throwerPlayerId;
        if (_projectile == null) return 0L;

        _throwerPlayerId = SpearThrowerMetadata.ReadFromProjectile(_projectile);
        if (_throwerPlayerId != 0L) return _throwerPlayerId;

        if (SpearThrowerMetadata.TryWriteToProjectile(_projectile, out long resolvedPlayerId))
        {
            _throwerPlayerId = resolvedPlayerId;
        }
        else if (SpearThrowerMetadata.TryResolveThrowerPlayerId(_projectile, out resolvedPlayerId))
        {
            _throwerPlayerId = resolvedPlayerId;
        }

        return _throwerPlayerId;
    }

    private void TryCopyThrowerMetadataToNearbyDrop()
    {
        if (_projectile == null) return;

        ItemDrop.ItemData? spawnItem = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_spawnItem, _projectile, null);
        if (spawnItem == null) return;

        TryCopyThrowerMetadataToNearbyDrop(spawnItem, _lastPosition);
    }

    private void TryCopyThrowerMetadataToNearbyDrop(ItemDrop.ItemData spawnItem, Vector3 position)
    {
        long throwerPlayerId = EnsureThrowerMetadata();
        if (throwerPlayerId == 0L) return;

        ItemDrop? drop = FindBestMatchingNearbyDrop(spawnItem, position);
        if (drop == null)
        {
            QueuePendingDropTag(spawnItem, position, throwerPlayerId);
            return;
        }

        SpearThrowerMetadata.TryWriteToDrop(drop, throwerPlayerId);
    }

    private static void QueuePendingDropTag(ItemDrop.ItemData spawnItem, Vector3 position, long throwerPlayerId)
    {
        if (throwerPlayerId == 0L) return;

        PendingDropTags.Add(new PendingDropTag
        {
            SpawnItem = spawnItem,
            Position = position,
            ThrowerPlayerId = throwerPlayerId,
            ExpiresAt = Time.time + PendingDropTagSeconds
        });
    }

    private bool TryClaimNetworkRescue(out NetworkRescueClaim claim)
    {
        claim = default;
        if (_projectile == null) return false;

        ZNetView? nview = ReflectionCache.GetNView(_projectile);
        if (nview == null || !nview.IsValid()) return true;

        try
        {
            ZDO zdo = nview.GetZDO();
            if (zdo == null || !zdo.IsValid()) return true;

            if (zdo.GetBool(ZdoRescueClaimKey, false)) return false;

            zdo.Set(ZdoRescueClaimKey, true);
            claim = new NetworkRescueClaim(zdo);
            return true;
        }
        catch
        {
            return true;
        }
    }

    private void ReleaseNetworkRescueClaim(NetworkRescueClaim claim, string reason)
    {
        if (claim.Zdo == null) return;

        try
        {
            claim.Zdo.Set(ZdoRescueClaimKey, false);
        }
        catch
        {
            FearNoSpearPlugin.Log.LogWarning($"Could not release failed spear rescue claim: reason={reason}");
        }
    }

    private static ItemDrop? FindBestMatchingNearbyDrop(ItemDrop.ItemData spawnItem, Vector3 position, ItemDrop[]? loadedDrops = null)
    {
        ItemDrop? best = null;
        float bestDistanceSqr = float.PositiveInfinity;
        float radiusSqr = NearbyDropMatchRadius * NearbyDropMatchRadius;

        foreach (ItemDrop drop in loadedDrops ?? FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
        {
            if (drop == null || drop.m_itemData == null) continue;
            float distanceSqr = Vector3.SqrMagnitude(drop.transform.position - position);
            if (distanceSqr > radiusSqr || distanceSqr >= bestDistanceSqr) continue;
            if (!SpearItemIdentity.IsEquivalent(spawnItem, drop.m_itemData)) continue;
            best = drop;
            bestDistanceSqr = distanceSqr;
        }

        return best;
    }

    private bool TrySpawnOriginalItem(Projectile projectile, ItemDrop.ItemData spawnItem, Vector3 normal, out ItemDrop drop)
    {
        drop = null!;
        bool spawnOnHitWouldNoOp = ReflectionCache.Get(ReflectionCache.F_groundHitOnly, projectile, false);
        if (!spawnOnHitWouldNoOp && TrySpawnOriginalItemThroughValheimPath(projectile, normal))
        {
            ItemDrop? spawnedDrop = FindBestMatchingNearbyDrop(spawnItem, _lastPosition);
            if (spawnedDrop != null)
            {
                drop = spawnedDrop;
                return true;
            }
        }

        return TryDropStoredItem(projectile, spawnItem, _lastPosition, out drop);
    }

    private static bool TrySpawnOriginalItemThroughValheimPath(Projectile projectile, Vector3 normal)
    {
        MethodInfo? method = ReflectionCache.M_spawnOnHit;
        if (method == null) return false;

        try
        {
            object?[] args = BuildSpawnOnHitArguments(method, normal);
            method.Invoke(projectile, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDropStoredItem(Projectile projectile, ItemDrop.ItemData spawnItem, Vector3 position, out ItemDrop drop)
    {
        drop = null!;
        MethodInfo? method = ReflectionCache.M_itemDropDropItem;
        if (method == null) return false;

        try
        {
            ItemDrop? spawnedDrop = method.Invoke(
                null,
                new object[] { spawnItem, 1, position, projectile.transform.rotation }) as ItemDrop;
            if (spawnedDrop == null) return false;

            drop = spawnedDrop;
            return true;
        }
        catch (TargetInvocationException ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"ItemDrop fallback failed while rescuing spear: {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"ItemDrop fallback failed while rescuing spear: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static object?[] BuildSpawnOnHitArguments(MethodInfo method, Vector3 normal)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object?[] args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; ++i)
        {
            Type type = parameters[i].ParameterType;
            if (type == typeof(Vector3)) args[i] = normal;
            else if (!type.IsValueType) args[i] = null;
            else if (type == typeof(bool)) args[i] = false;
            else if (type == typeof(int)) args[i] = 0;
            else if (type == typeof(float)) args[i] = 0f;
            else args[i] = Activator.CreateInstance(type);
        }

        return args;
    }

    private readonly struct NetworkRescueClaim
    {
        internal readonly ZDO? Zdo;

        internal NetworkRescueClaim(ZDO zdo)
        {
            Zdo = zdo;
        }
    }

    private sealed class PendingDropTag
    {
        internal ItemDrop.ItemData SpawnItem = null!;
        internal Vector3 Position;
        internal long ThrowerPlayerId;
        internal float ExpiresAt;
    }
}
