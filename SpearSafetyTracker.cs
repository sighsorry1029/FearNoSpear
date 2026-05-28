using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FearNoSpear;

internal sealed class SpearSafetyTracker : MonoBehaviour
{
    private const string ZdoRescueClaimKey = "FearNoSpear.Rescued";
    private const float NearbyDuplicateCheckRadius = 4f;
    private const float PendingDropTagSeconds = 2f;

    private static readonly List<PendingDropTag> PendingDropTags = new();

    private Projectile? _projectile;
    private bool _armed;
    private bool _normalHit;
    private bool _rescueAttempted;
    private bool _lastKnownOwner;
    private float _lastOwnerStateTime = float.NegativeInfinity;
    private float _lastTtl;
    private Vector3 _lastPosition;
    private Vector3 _lastVelocity;
    private long _throwerPlayerId;

    internal static SpearSafetyTracker ArmOrRefresh(Projectile projectile)
    {
        SpearSafetyTracker tracker = projectile.GetComponent<SpearSafetyTracker>();
        if (tracker == null) tracker = projectile.gameObject.AddComponent<SpearSafetyTracker>();
        tracker.Arm(projectile);
        return tracker;
    }

    internal static SpearSafetyTracker? GetOrArmIfTracked(Projectile projectile, string source)
    {
        if (!SpearProjectileDetector.IsTrackedSpearProjectile(projectile)) return null;

        SpearSafetyTracker tracker = projectile.GetComponent<SpearSafetyTracker>();
        if (tracker != null) return tracker;

        tracker = projectile.gameObject.AddComponent<SpearSafetyTracker>();
        tracker.Arm(projectile);

        if (FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogDebug($"Lazy-armed thrown spear tracker from {source}: {SpearProjectileDetector.DescribeProjectile(projectile)}");
        }

        return tracker;
    }

    internal static void ClearPendingDropTags()
    {
        PendingDropTags.Clear();
    }

    internal static void UpdatePendingDropTags()
    {
        if (PendingDropTags.Count == 0) return;

        for (int i = PendingDropTags.Count - 1; i >= 0; --i)
        {
            PendingDropTag pending = PendingDropTags[i];
            if (Time.time > pending.ExpiresAt)
            {
                PendingDropTags.RemoveAt(i);
                continue;
            }

            ItemDrop? drop = FindBestMatchingNearbyDrop(pending.SpawnItem, pending.Position);
            if (drop == null) continue;

            if (SpearThrowerMetadata.TryWriteToDrop(drop, pending.ThrowerPlayerId) && FearNoSpearConfig.Verbose)
            {
                FearNoSpearPlugin.Log.LogDebug($"Tagged delayed spear drop with thrower metadata after {pending.Reason}: playerId={pending.ThrowerPlayerId}; drop={drop.name}; pos={drop.transform.position}");
            }

            PendingDropTags.RemoveAt(i);
        }
    }

    internal void Arm(Projectile projectile)
    {
        _projectile = projectile;
        _armed = true;
        _normalHit = ReflectionCache.Get(ReflectionCache.F_didHit, projectile, false);
        _rescueAttempted = false;
        _throwerPlayerId = 0L;
        RefreshState();

        EnsureThrowerMetadata();
        ExtendInitialTtlIfNeeded();

        if (FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogInfo($"Tracked thrown spear: {SpearProjectileDetector.DescribeProjectile(projectile)} ttl={_lastTtl:0.00} owner={_lastKnownOwner}");
        }
    }

    internal void MarkNormalHit(string source)
    {
        if (_projectile != null)
        {
            RefreshState();
            TryCopyThrowerMetadataToNearbyDrop("normal hit");
        }

        _normalHit = true;
        if (FearNoSpearConfig.Verbose && _projectile != null)
        {
            FearNoSpearPlugin.Log.LogInfo($"Spear projectile normal hit observed ({source}): {_projectile.name}");
        }
    }

    internal bool TryRescue(string reason)
    {
        if (FearNoSpearPlugin.IsShuttingDown) return false;
        if (!FearNoSpearPlugin.Cfg.Enabled.Value) return false;
        if (!_armed || _projectile == null || _rescueAttempted) return false;
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
            if (TryUseExistingNearbyDrop(spawnItem, _lastPosition, EnsureThrowerMetadata(), "pre-rescue", out _))
            {
                rescueSatisfied = true;
                _normalHit = true;
                ReflectionCache.Set(ReflectionCache.F_didHit, _projectile, true);
                return true;
            }

            Vector3 normal = _lastVelocity.sqrMagnitude > 0.001f ? -_lastVelocity.normalized : Vector3.up;
            if (!TrySpawnOriginalItem(_projectile, spawnItem, normal, reason, out Vector3 rescuedDropPosition))
            {
                ReleaseNetworkRescueClaim(claim, "spawn failed");
                FearNoSpearPlugin.Log.LogWarning($"Could not rescue spear; Valheim SpawnOnHit and ItemDrop fallback were unavailable or failed. reason={reason}; {SpearProjectileDetector.DescribeProjectile(_projectile)}");
                return false;
            }
            rescueSatisfied = true;

            _normalHit = true;
            ReflectionCache.Set(ReflectionCache.F_didHit, _projectile, true);
            TryCopyThrowerMetadataToNearbyDrop(spawnItem, rescuedDropPosition, "rescue");
            RemoveNearbyDuplicateDrops(spawnItem, rescuedDropPosition);

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
        if (!FearNoSpearConfig.RescueBeforeTtlExpiry) return false;
        if (!_armed || _projectile == null) return false;

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
        if (!FearNoSpearConfig.RescueOnUnexpectedDestroy) return;
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
        if (!FearNoSpearConfig.ExtendInitialTtl) return;

        float min = Mathf.Max(0f, FearNoSpearConfig.MinimumInitialTtlSeconds);
        if (min <= 0f) return;

        float ttl = ReflectionCache.Get(ReflectionCache.F_ttl, _projectile, 0f);
        if (ttl > 0f && ttl < min)
        {
            ReflectionCache.Set(ReflectionCache.F_ttl, _projectile, min);
            _lastTtl = min;
            if (FearNoSpearConfig.Verbose)
            {
                FearNoSpearPlugin.Log.LogInfo($"Extended thrown spear TTL {ttl:0.00}s -> {min:0.00}s: {SpearProjectileDetector.DescribeProjectile(_projectile)}");
            }
        }
    }

    private bool MayThisClientRescue()
    {
        if (_projectile == null) return false;
        if (!FearNoSpearConfig.OnlyOwnerMayRescue) return true;

        ZNetView? nview = ReflectionCache.GetNView(_projectile);
        if (nview != null && nview.IsValid())
        {
            bool isOwner = nview.IsOwner();
            if (!isOwner && FearNoSpearConfig.Verbose)
            {
                FearNoSpearPlugin.Log.LogDebug($"Skipped spear rescue because this client is not current owner: {_projectile.name}");
            }
            return isOwner;
        }

        float maxAge = Mathf.Max(0f, FearNoSpearPlugin.Cfg.LastKnownOwnerGraceSeconds.Value);
        float age = Time.time - _lastOwnerStateTime;
        bool allowLastKnown = FearNoSpearPlugin.Cfg.AllowLastKnownOwnerIfZNetViewInvalid.Value &&
                              _lastKnownOwner &&
                              age <= maxAge;
        if (!allowLastKnown && FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogDebug($"Skipped spear rescue because ZNetView invalid and this client is not a recent last-known owner: {_projectile.name}; lastOwner={_lastKnownOwner}; age={age:0.00}s; maxAge={maxAge:0.00}s");
        }
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

    private void TryCopyThrowerMetadataToNearbyDrop(string reason)
    {
        if (_projectile == null) return;

        ItemDrop.ItemData? spawnItem = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_spawnItem, _projectile, null);
        if (spawnItem == null) return;

        TryCopyThrowerMetadataToNearbyDrop(spawnItem, _lastPosition, reason);
    }

    private void TryCopyThrowerMetadataToNearbyDrop(ItemDrop.ItemData spawnItem, Vector3 position, string reason)
    {
        long throwerPlayerId = EnsureThrowerMetadata();
        if (throwerPlayerId == 0L) return;

        ItemDrop? drop = FindBestMatchingNearbyDrop(spawnItem, position);
        if (drop == null)
        {
            QueuePendingDropTag(spawnItem, position, throwerPlayerId, reason);
            return;
        }

        if (SpearThrowerMetadata.TryWriteToDrop(drop, throwerPlayerId) && FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogDebug($"Tagged spear drop with thrower metadata after {reason}: playerId={throwerPlayerId}; drop={drop.name}; pos={drop.transform.position}");
        }
    }

    private static void QueuePendingDropTag(ItemDrop.ItemData spawnItem, Vector3 position, long throwerPlayerId, string reason)
    {
        if (throwerPlayerId == 0L) return;

        PendingDropTags.Add(new PendingDropTag
        {
            SpawnItem = spawnItem,
            Position = position,
            ThrowerPlayerId = throwerPlayerId,
            Reason = reason,
            ExpiresAt = Time.time + PendingDropTagSeconds
        });
    }

    private bool TryClaimNetworkRescue(out NetworkRescueClaim claim)
    {
        claim = default;
        if (_projectile == null) return false;
        if (!FearNoSpearConfig.UseZdoClaimFlag) return true;

        ZNetView? nview = ReflectionCache.GetNView(_projectile);
        if (nview == null || !nview.IsValid()) return true;
        if (ReflectionCache.M_zNetViewGetZdo == null || ReflectionCache.M_zdoGetBool == null || ReflectionCache.M_zdoSetBool == null) return true;

        try
        {
            object? zdo = ReflectionCache.M_zNetViewGetZdo.Invoke(nview, Array.Empty<object>());
            if (zdo == null) return true;

            bool alreadyClaimed = (bool)ReflectionCache.M_zdoGetBool.Invoke(zdo, new object[] { ZdoRescueClaimKey, false });
            if (alreadyClaimed)
            {
                if (FearNoSpearConfig.Verbose)
                {
                    FearNoSpearPlugin.Log.LogDebug($"Skipped spear rescue because ZDO claim flag is already set: {_projectile.name}");
                }
                return false;
            }

            ReflectionCache.M_zdoSetBool.Invoke(zdo, new object[] { ZdoRescueClaimKey, true });
            claim = new NetworkRescueClaim(zdo);
            return true;
        }
        catch (Exception ex)
        {
            if (FearNoSpearConfig.Verbose)
            {
                FearNoSpearPlugin.Log.LogDebug($"Could not use ZDO rescue claim flag; continuing without it. ex={ex.GetType().Name}: {ex.Message}");
            }
            return true;
        }
    }

    private void ReleaseNetworkRescueClaim(NetworkRescueClaim claim, string reason)
    {
        if (!claim.Claimed || claim.Zdo == null || ReflectionCache.M_zdoSetBool == null) return;

        try
        {
            ReflectionCache.M_zdoSetBool.Invoke(claim.Zdo, new object[] { ZdoRescueClaimKey, false });
            if (FearNoSpearConfig.Verbose && _projectile != null)
            {
                FearNoSpearPlugin.Log.LogDebug($"Released spear rescue ZDO claim after failed rescue: reason={reason}; {_projectile.name}");
            }
        }
        catch (Exception ex)
        {
            if (FearNoSpearConfig.Verbose)
            {
                FearNoSpearPlugin.Log.LogDebug($"Could not release failed spear rescue ZDO claim: reason={reason}; ex={ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static bool TryUseExistingNearbyDrop(ItemDrop.ItemData spawnItem, Vector3 position, long throwerPlayerId, string phase, out Vector3 dropPosition)
    {
        ItemDrop? existing = FindBestMatchingNearbyDrop(spawnItem, position);
        if (existing == null)
        {
            dropPosition = position;
            return false;
        }

        dropPosition = existing.transform.position;
        if (throwerPlayerId != 0L)
        {
            SpearThrowerMetadata.TryWriteToDrop(existing, throwerPlayerId);
        }

        if (FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogInfo($"Skipped spear rescue because an equivalent item drop already exists nearby during {phase}: drop={existing.name}; pos={existing.transform.position}; radius={NearbyDuplicateCheckRadius:0.0}m");
        }

        return true;
    }

    private static void RemoveNearbyDuplicateDrops(ItemDrop.ItemData? spawnItem, Vector3 position)
    {
        if (spawnItem == null) return;

        List<ItemDrop> matches = FindMatchingNearbyDrops(spawnItem, position);
        if (matches.Count <= 1) return;

        matches.Sort((a, b) =>
            Vector3.SqrMagnitude(a.transform.position - position)
                .CompareTo(Vector3.SqrMagnitude(b.transform.position - position)));

        for (int i = 1; i < matches.Count; ++i)
        {
            ItemDrop duplicate = matches[i];
            if (DestroyDuplicateDrop(duplicate) && FearNoSpearConfig.Verbose)
            {
                FearNoSpearPlugin.Log.LogInfo($"Removed duplicate rescued spear drop near rescue point: drop={duplicate.name}; pos={duplicate.transform.position}; kept={matches[0].name}; radius={NearbyDuplicateCheckRadius:0.0}m");
            }
        }
    }

    private static ItemDrop? FindBestMatchingNearbyDrop(ItemDrop.ItemData spawnItem, Vector3 position)
    {
        List<ItemDrop> matches = FindMatchingNearbyDrops(spawnItem, position);
        if (matches.Count == 0) return null;

        matches.Sort((a, b) =>
            Vector3.SqrMagnitude(a.transform.position - position)
                .CompareTo(Vector3.SqrMagnitude(b.transform.position - position)));

        return matches[0];
    }

    private static List<ItemDrop> FindMatchingNearbyDrops(ItemDrop.ItemData spawnItem, Vector3 position)
    {
        List<ItemDrop> matches = new();
        float radiusSqr = NearbyDuplicateCheckRadius * NearbyDuplicateCheckRadius;

        foreach (ItemDrop drop in FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
        {
            if (drop == null || drop.m_itemData == null) continue;
            if (Vector3.SqrMagnitude(drop.transform.position - position) > radiusSqr) continue;
            if (!SpearItemIdentity.IsEquivalent(spawnItem, drop.m_itemData)) continue;
            matches.Add(drop);
        }

        return matches;
    }

    private static bool DestroyDuplicateDrop(ItemDrop duplicate)
    {
        try
        {
            ZNetView nview = duplicate.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                if (!nview.IsOwner())
                {
                    nview.ClaimOwnership();
                }

                nview.Destroy();
                return true;
            }

            Destroy(duplicate.gameObject);
            return true;
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"Failed to remove duplicate rescued spear drop: drop={duplicate.name}; ex={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TrySpawnOriginalItem(Projectile projectile, ItemDrop.ItemData spawnItem, Vector3 normal, string reason, out Vector3 dropPosition)
    {
        dropPosition = _lastPosition;
        bool spawnOnHitWouldNoOp = ReflectionCache.Get(ReflectionCache.F_groundHitOnly, projectile, false);
        string? spawnOnHitFailure = spawnOnHitWouldNoOp ? "ground-hit-only projectile without a ground hit object" : null;
        if (!spawnOnHitWouldNoOp && TrySpawnOriginalItemThroughValheimPath(projectile, normal, out spawnOnHitFailure))
        {
            if (TryFindSpawnedDrop(spawnItem, _lastPosition, out dropPosition))
            {
                return true;
            }

            spawnOnHitFailure = "SpawnOnHit completed but no equivalent ItemDrop appeared nearby";
        }

        if (!FearNoSpearConfig.UseItemDropFallback) return false;

        if (spawnOnHitWouldNoOp && FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogDebug($"Using ItemDrop fallback because SpawnOnHit requires a ground hit object. reason={reason}; {SpearProjectileDetector.DescribeProjectile(projectile)}");
        }
        else if (spawnOnHitFailure != null && FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogDebug($"Using ItemDrop fallback after SpawnOnHit failed: {spawnOnHitFailure}. reason={reason}; {SpearProjectileDetector.DescribeProjectile(projectile)}");
        }

        if (!TryDropStoredItem(projectile, spawnItem, _lastPosition)) return false;

        if (TryFindSpawnedDrop(spawnItem, _lastPosition, out dropPosition))
        {
            return true;
        }

        if (FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogDebug($"ItemDrop fallback returned without a matching spear drop near the rescue point. reason={reason}; {SpearProjectileDetector.DescribeProjectile(projectile)}");
        }

        return false;
    }

    private static bool TrySpawnOriginalItemThroughValheimPath(Projectile projectile, Vector3 normal, out string? failure)
    {
        MethodInfo? method = ReflectionCache.M_spawnOnHit;
        if (method == null)
        {
            failure = "method missing";
            return false;
        }

        try
        {
            object?[] args = BuildSpawnOnHitArguments(method, normal);
            method.Invoke(projectile, args);
            failure = null;
            return true;
        }
        catch (TargetInvocationException ex)
        {
            failure = ex.InnerException == null
                ? ex.Message
                : $"{ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            return false;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryDropStoredItem(Projectile projectile, ItemDrop.ItemData spawnItem, Vector3 position)
    {
        MethodInfo? method = ReflectionCache.M_itemDropDropItem;
        if (method == null) return false;

        try
        {
            method.Invoke(null, new object[] { spawnItem, 1, position, projectile.transform.rotation });
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

    private static bool TryFindSpawnedDrop(ItemDrop.ItemData spawnItem, Vector3 position, out Vector3 dropPosition)
    {
        ItemDrop? drop = FindBestMatchingNearbyDrop(spawnItem, position);
        if (drop == null)
        {
            dropPosition = position;
            return false;
        }

        dropPosition = drop.transform.position;
        return true;
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
        internal readonly object? Zdo;
        internal readonly bool Claimed;

        internal NetworkRescueClaim(object zdo)
        {
            Zdo = zdo;
            Claimed = true;
        }
    }

    private sealed class PendingDropTag
    {
        internal ItemDrop.ItemData SpawnItem = null!;
        internal Vector3 Position;
        internal long ThrowerPlayerId;
        internal string Reason = string.Empty;
        internal float ExpiresAt;
    }
}
