using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearServerRegistry
{
    private const int MaxRecordsPerPlayer = 20;

    private static List<string>? _spearItemDropPrefabNames;

    internal static void Clear()
    {
        _spearItemDropPrefabNames = null;
    }

    internal static List<SpearLocationRecord> SelectBest(long playerId, int maxRecords, Vector3 referencePosition)
    {
        if (playerId == 0L) return new List<SpearLocationRecord>();

        int limit = Mathf.Clamp(maxRecords, 1, MaxRecordsPerPlayer);
        return SelectWorldZdoSpearDrops(playerId, limit, referencePosition);
    }

    private static List<SpearLocationRecord> SelectWorldZdoSpearDrops(long playerId, int limit, Vector3 referencePosition)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return new List<SpearLocationRecord>();
        if (ZDOMan.instance == null || ZNetScene.instance == null) return new List<SpearLocationRecord>();

        List<SpearLocationRecord> records = new();
        foreach (string prefabName in GetSpearItemDropPrefabNames())
        {
            GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName);
            ItemDrop? prefabDrop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (prefabDrop == null || prefabDrop.m_itemData == null) continue;

            List<ZDO> zdos = new();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, zdos, ref index))
            {
            }

            foreach (ZDO zdo in zdos)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                if (SpearThrowerMetadata.ReadFromZdo(zdo) != playerId) continue;

                string itemKey = SpearItemIdentity.BuildLocatorKey(prefabName, prefabDrop.m_itemData, zdo);
                records.Add(new SpearLocationRecord
                {
                    Key = SpearItemIdentity.BuildWorldDropRecordKey(zdo.m_uid),
                    ItemKey = itemKey,
                    Position = zdo.GetPosition(),
                    LastUpdated = Time.time,
                    Source = SpearLocator.WorldZdoDropSource
                });
            }
        }

        return records
            .OrderBy(record => Vector3.SqrMagnitude(record.Position - referencePosition))
            .Take(limit)
            .ToList();
    }

    private static List<string> GetSpearItemDropPrefabNames()
    {
        if (_spearItemDropPrefabNames != null) return _spearItemDropPrefabNames;

        _spearItemDropPrefabNames = new List<string>();
        if (ZNetScene.instance == null) return _spearItemDropPrefabNames;

        foreach (string prefabName in ZNetScene.instance.GetPrefabNames())
        {
            GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName);
            ItemDrop? drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null || drop.m_itemData == null) continue;
            if (!SpearProjectileDetector.IsSpearItem(drop.m_itemData)) continue;

            _spearItemDropPrefabNames.Add(prefabName);
        }

        return _spearItemDropPrefabNames;
    }

}
