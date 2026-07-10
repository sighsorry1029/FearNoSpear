using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearServerRegistry
{
    private static List<string>? _spearItemDropPrefabNames;

    internal static void Clear()
    {
        _spearItemDropPrefabNames = null;
    }

    internal static List<SpearLocationRecord> SelectBest(long playerId, Vector3 referencePosition)
    {
        if (playerId == 0L) return new List<SpearLocationRecord>();
        return SelectWorldZdoSpearDrops(playerId, referencePosition);
    }

    private static List<SpearLocationRecord> SelectWorldZdoSpearDrops(long playerId, Vector3 referencePosition)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return new List<SpearLocationRecord>();
        if (ZDOMan.instance == null || ZNetScene.instance == null) return new List<SpearLocationRecord>();

        List<SpearLocationRecord> records = new();
        foreach (string prefabName in GetSpearItemDropPrefabNames())
        {
            List<ZDO> zdos = new();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, zdos, ref index))
            {
            }

            foreach (ZDO zdo in zdos)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                if (SpearThrowerMetadata.ReadFromZdo(zdo) != playerId) continue;

                records.Add(new SpearLocationRecord
                {
                    Key = SpearItemIdentity.BuildWorldDropRecordKey(zdo.m_uid),
                    Position = zdo.GetPosition()
                });
            }
        }

        return records
            .OrderBy(record => Vector3.SqrMagnitude(record.Position - referencePosition))
            .Take(FearNoSpearConfig.MaxPinsPerCommand)
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
