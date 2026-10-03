using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearServerRegistry
{
    private const float CandidateCacheSeconds = 5f;
    private static readonly Dictionary<long, List<ZDOID>> WeaponIds = new();
    private static readonly Dictionary<long, List<ZDOID>> TombstoneIds = new();
    private static float _nextScanAt;
    private static float _nextTombstoneScanAt;

    internal static void Clear()
    {
        WeaponIds.Clear();
        _nextScanAt = 0f;
        TombstoneIds.Clear();
        _nextTombstoneScanAt = 0f;
    }

    internal static List<SpearLocationRecord> SelectBest(long playerId, Vector3 referencePosition, bool weapons, bool tombstones)
    {
        List<SpearLocationRecord> records = new();
        if (playerId == 0L || ZNet.instance == null || !ZNet.instance.IsServer()) return records;
        if (ZDOMan.instance == null || ZNetScene.instance == null) return records;

        // Native tag lookup scans world data. Group once per cache period, not once per requester.
        if (weapons && Time.unscaledTime >= _nextScanAt)
        {
            RefreshCandidates(WeaponIds,
                ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Long, SpearThrowerMetadata.ThrowerPlayerIdHash),
                SpearThrowerMetadata.ThrowerPlayerIdHash);
            _nextScanAt = Time.unscaledTime + CandidateCacheSeconds;
        }

        if (weapons && WeaponIds.TryGetValue(playerId, out List<ZDOID> weaponIds))
        foreach (ZDOID id in weaponIds)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (SpearThrowerMetadata.ReadFromZdo(zdo) != playerId) continue;
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo!.GetPrefab());
            ItemDrop? drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) continue;

            records.Add(new SpearLocationRecord
            {
                Key = SpearItemIdentity.BuildWorldDropRecordKey(id),
                Position = zdo.GetPosition(),
                PrefabHash = zdo.GetPrefab(),
                Variant = zdo.GetInt(ZDOVars.s_variant, 0)
            });
        }

        records = records.OrderBy(record => (record.Position - referencePosition).sqrMagnitude)
            .ThenBy(record => record.Key, System.StringComparer.Ordinal)
            .Take(FearNoSpearConfig.MaxLocationResults).ToList();

        if (!tombstones) return records;
        if (Time.unscaledTime >= _nextTombstoneScanAt)
        {
            RefreshCandidates(TombstoneIds,
                ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Long, ZDOVars.s_timeOfDeath), ZDOVars.s_owner);
            _nextTombstoneScanAt = Time.unscaledTime + CandidateCacheSeconds;
        }
        if (!TombstoneIds.TryGetValue(playerId, out List<ZDOID> tombstoneIds)) return records;
        List<SpearLocationRecord> graves = new();
        foreach (ZDOID id in tombstoneIds)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !zdo.IsValid() || zdo.GetLong(ZDOVars.s_owner, 0L) != playerId) continue;
            long createdTicks = zdo.GetLong(ZDOVars.s_timeOfDeath, 0L);
            if (createdTicks <= 0L || createdTicks > System.DateTime.MaxValue.Ticks) continue;
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab == null || prefab.GetComponent<TombStone>() == null) continue;
            graves.Add(new SpearLocationRecord
            {
                Key = SpearItemIdentity.BuildTombstoneRecordKey(id),
                Position = zdo.GetPosition(),
                PrefabHash = zdo.GetPrefab(),
                Kind = LocationKind.Tombstone,
                CreatedTicks = createdTicks
            });
        }
        records.AddRange(graves.OrderByDescending(record => record.CreatedTicks)
            .ThenBy(record => record.Key, System.StringComparer.Ordinal).Take(FearNoSpearConfig.MaxTombstoneResults));
        return records;
    }

    private static void RefreshCandidates(Dictionary<long, List<ZDOID>> owners, List<ZDOID> candidates, int ownerHash)
    {
        owners.Clear();
        foreach (ZDOID id in candidates)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !zdo.IsValid()) continue;
            long playerId = zdo.GetLong(ownerHash, 0L);
            if (playerId == 0L) continue;
            if (!owners.TryGetValue(playerId, out List<ZDOID> ids)) owners[playerId] = ids = new List<ZDOID>();
            ids.Add(id);
        }
    }
}
