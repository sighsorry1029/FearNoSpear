using System.Collections.Generic;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearServerRegistry
{
    private const int MaxRecordsPerPlayer = 20;

    private static readonly Dictionary<long, SpearRecordStore> Records = new();

    internal static void Clear()
    {
        Records.Clear();
    }

    internal static void Record(long playerId, string key, Vector3 position, string source, bool fromTrackedProjectile)
    {
        Record(playerId, key, key, position, source, fromTrackedProjectile);
    }

    internal static void Record(long playerId, string key, string itemKey, Vector3 position, string source, bool fromTrackedProjectile)
    {
        if (playerId == 0L || string.IsNullOrEmpty(key)) return;
        if (string.IsNullOrEmpty(itemKey)) itemKey = key;

        SpearRecordStore records = GetOrCreateStore(playerId);
        records.Record(key, itemKey, position, source, fromTrackedProjectile, IsDropSource(source), mergeByItemNearPosition: IsDropSource(source));
    }

    internal static List<SpearLocationRecord> SelectBest(long playerId, int maxRecords)
    {
        if (playerId == 0L) return new List<SpearLocationRecord>();
        if (!Records.TryGetValue(playerId, out SpearRecordStore records)) return new List<SpearLocationRecord>();

        int limit = Mathf.Clamp(maxRecords, 1, MaxRecordsPerPlayer);
        return records.SelectBest(limit);
    }

    internal static bool RemovePickedUp(long playerId, string itemKey, Vector3 pickupPosition)
    {
        if (playerId == 0L || string.IsNullOrEmpty(itemKey)) return false;
        if (!Records.TryGetValue(playerId, out SpearRecordStore records)) return false;

        List<SpearLocationRecord> matches = records.RemoveByItem(itemKey);
        if (matches.Count == 0) return false;

        if (FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogDebug($"Removed picked spear from server locator registry: player={playerId}; itemKey={itemKey}; pos={pickupPosition}");
        }

        return true;
    }

    private static SpearRecordStore GetOrCreateStore(long playerId)
    {
        if (Records.TryGetValue(playerId, out SpearRecordStore records)) return records;

        records = new SpearRecordStore(MaxRecordsPerPlayer, SpearLocator.LoadedDropMergeRadius);
        Records[playerId] = records;
        return records;
    }

    private static bool IsDropSource(string source)
    {
        return source == SpearLocator.LoadedDropSource || source == "rescued spear" || source == "existing spear drop";
    }

}
