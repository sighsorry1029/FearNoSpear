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
}

internal sealed class SpearRecordStore
{
    private readonly int _maxRecords;
    private readonly List<SpearLocationRecord> _records = new();

    internal SpearRecordStore(int maxRecords)
    {
        _maxRecords = maxRecords;
    }

    internal void Clear()
    {
        _records.Clear();
    }

    internal List<SpearLocationRecord> SelectBest(int maxRecords)
    {
        if (_records.Count == 0) return new List<SpearLocationRecord>();

        return _records
            .OrderByDescending(record => record.LastUpdated)
            .Take(Mathf.Clamp(maxRecords, 1, _maxRecords))
            .ToList();
    }

    internal SpearLocationRecord Record(
        string key,
        string itemKey,
        Vector3 position,
        string source,
        float lastUpdated = -1f)
    {
        float timestamp = lastUpdated >= 0f ? lastUpdated : Time.time;
        SpearLocationRecord? existing = _records.FirstOrDefault(record => record.Key == key);
        if (existing != null)
        {
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
            Source = source
        };
        _records.Add(record);

        Trim();
        return record;
    }

    internal int RemovePicked(string recordKey)
    {
        if (string.IsNullOrEmpty(recordKey)) return 0;

        SpearLocationRecord? exact = _records.FirstOrDefault(record => record.Key == recordKey);
        if (exact == null) return 0;

        _records.Remove(exact);
        return 1;
    }

    private void Trim()
    {
        while (_records.Count > _maxRecords)
        {
            SpearLocationRecord oldest = _records
                .OrderBy(record => record.LastUpdated)
                .First();
            _records.Remove(oldest);
        }
    }
}
