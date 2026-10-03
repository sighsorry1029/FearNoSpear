using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearServerRegistry
{
    private static List<ZDOID> _taggedIds = new();
    private static float _nextScanAt;

    internal static void Clear()
    {
        _taggedIds.Clear();
        _nextScanAt = 0f;
    }

    internal static List<SpearLocationRecord> SelectBest(long playerId, Vector3 referencePosition)
    {
        List<SpearLocationRecord> records = new();
        if (playerId == 0L || ZNet.instance == null || !ZNet.instance.IsServer()) return records;
        if (ZDOMan.instance == null || ZNetScene.instance == null) return records;

        // Share one bounded-rate tag scan across clients, not a world scan per spear prefab.
        if (Time.unscaledTime >= _nextScanAt)
        {
            _taggedIds = ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Long, SpearThrowerMetadata.ThrowerPlayerIdHash);
            _nextScanAt = Time.unscaledTime + 2f;
        }

        foreach (ZDOID id in _taggedIds)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(id);
            if (SpearThrowerMetadata.ReadFromZdo(zdo) != playerId) continue;
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo!.GetPrefab());
            ItemDrop? drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null || !SpearProjectileDetector.IsSpearItem(drop.m_itemData)) continue;

            records.Add(new SpearLocationRecord
            {
                Key = SpearItemIdentity.BuildWorldDropRecordKey(id),
                Position = zdo.GetPosition(),
                PrefabHash = zdo.GetPrefab(),
                Variant = zdo.GetInt(ZDOVars.s_variant, 0)
            });
        }

        return records.OrderBy(record => (record.Position - referencePosition).sqrMagnitude)
            .ThenBy(record => record.Key, System.StringComparer.Ordinal)
            .Take(FearNoSpearConfig.MaxLocationResults).ToList();
    }

}
