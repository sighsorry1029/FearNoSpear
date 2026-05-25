using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FearNoSpear;

internal sealed class SpearLocationRecord
{
    internal string Key = string.Empty;
    internal string ItemKey = string.Empty;
    internal Vector3 Position;
    internal float LastUpdated;
    internal string Source = string.Empty;
    internal bool FromTrackedProjectile;
    internal bool FromLoadedDrop;
}

internal sealed class SpearRecordStore
{
    private readonly int _maxRecords;
    private readonly float _mergeRadius;
    private readonly List<SpearLocationRecord> _records = new();

    internal SpearRecordStore(int maxRecords, float mergeRadius)
    {
        _maxRecords = maxRecords;
        _mergeRadius = mergeRadius;
    }

    internal void Clear()
    {
        _records.Clear();
    }

    internal bool HasItem(string itemKey)
    {
        return _records.Any(record => record.ItemKey == itemKey || record.Key == itemKey);
    }

    internal List<SpearLocationRecord> FindByItem(string itemKey)
    {
        return _records
            .Where(record => record.ItemKey == itemKey || record.Key == itemKey)
            .ToList();
    }

    internal List<SpearLocationRecord> SelectBest(int maxRecords)
    {
        if (_records.Count == 0) return new List<SpearLocationRecord>();

        return _records
            .OrderByDescending(record => record.LastUpdated)
            .ThenByDescending(record => record.FromLoadedDrop)
            .ThenByDescending(record => record.FromTrackedProjectile)
            .Take(Mathf.Clamp(maxRecords, 1, _maxRecords))
            .ToList();
    }

    internal SpearLocationRecord Record(
        string key,
        string itemKey,
        Vector3 position,
        string source,
        bool fromTrackedProjectile,
        bool fromLoadedDrop,
        bool mergeByItemNearPosition,
        float lastUpdated = -1f)
    {
        float timestamp = lastUpdated >= 0f ? lastUpdated : Time.time;
        SpearLocationRecord? existing = FindRecord(key, itemKey, position, mergeByItemNearPosition);
        if (existing != null)
        {
            existing.FromTrackedProjectile |= fromTrackedProjectile;
            existing.FromLoadedDrop |= fromLoadedDrop;
            if (timestamp >= existing.LastUpdated)
            {
                existing.Key = key;
                existing.ItemKey = itemKey;
                existing.Position = position;
                existing.LastUpdated = timestamp;
                existing.Source = source;
            }
            return existing;
        }

        SpearLocationRecord record = new()
        {
            Key = key,
            ItemKey = itemKey,
            Position = position,
            LastUpdated = timestamp,
            Source = source,
            FromTrackedProjectile = fromTrackedProjectile,
            FromLoadedDrop = fromLoadedDrop
        };
        _records.Add(record);

        Trim();
        return record;
    }

    internal List<SpearLocationRecord> RemoveByItem(string itemKey)
    {
        List<SpearLocationRecord> matches = FindByItem(itemKey);
        foreach (SpearLocationRecord match in matches)
        {
            _records.Remove(match);
        }

        return matches;
    }

    private SpearLocationRecord? FindRecord(string key, string itemKey, Vector3 position, bool mergeByItemNearPosition)
    {
        SpearLocationRecord? exact = _records.FirstOrDefault(record => record.Key == key);
        if (exact != null || !mergeByItemNearPosition) return exact;

        float radiusSqr = _mergeRadius * _mergeRadius;
        return _records
            .Where(record => record.ItemKey == itemKey || record.Key == itemKey)
            .Where(record => Vector3.SqrMagnitude(record.Position - position) <= radiusSqr)
            .OrderBy(record => Vector3.SqrMagnitude(record.Position - position))
            .FirstOrDefault();
    }

    private void Trim()
    {
        while (_records.Count > _maxRecords)
        {
            SpearLocationRecord oldest = _records
                .OrderBy(record => record.LastUpdated)
                .ThenBy(record => record.FromLoadedDrop)
                .ThenBy(record => record.FromTrackedProjectile)
                .First();
            _records.Remove(oldest);
        }
    }
}
