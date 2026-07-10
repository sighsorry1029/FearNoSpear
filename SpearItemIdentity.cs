using System;
using System.Collections.Generic;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearItemIdentity
{
    internal static string BuildDropRecordKey(ItemDrop drop)
    {
        ZNetView? nview = drop.GetComponent<ZNetView>();
        string? zdoKey = TryGetZdoKey(nview);
        if (zdoKey != null && zdoKey.Length > 0) return BuildDropRecordKey(zdoKey);

        return $"drop-local:{drop.GetInstanceID()}";
    }

    internal static string BuildWorldDropRecordKey(ZDOID zdoId)
    {
        return BuildDropRecordKey(zdoId.ToString());
    }

    internal static string? TryGetZdoKey(ZNetView? nview)
    {
        if (nview == null || !nview.IsValid()) return null;

        ZDO zdo = nview.GetZDO();
        if (zdo == null || !zdo.IsValid()) return null;

        return zdo.m_uid.ToString();
    }

    private static string BuildDropRecordKey(string zdoKey)
    {
        return $"drop:{zdoKey}";
    }

    internal static bool IsEquivalent(ItemDrop.ItemData expected, ItemDrop.ItemData actual)
    {
        if (expected.m_dropPrefab != actual.m_dropPrefab) return false;
        if (expected.m_shared?.m_name != actual.m_shared?.m_name) return false;
        if (expected.m_quality != actual.m_quality) return false;
        if (expected.m_variant != actual.m_variant) return false;
        if (expected.m_worldLevel != actual.m_worldLevel) return false;
        if (expected.m_crafterID != actual.m_crafterID) return false;
        if (!string.Equals(expected.m_crafterName, actual.m_crafterName, StringComparison.Ordinal)) return false;

        float expectedDurability = Mathf.Max(0f, expected.m_durability);
        float actualDurability = Mathf.Max(0f, actual.m_durability);
        if (Mathf.Abs(expectedDurability - actualDurability) > 0.01f) return false;

        return DictionariesEqual(expected.m_customData, actual.m_customData);
    }

    private static bool DictionariesEqual(Dictionary<string, string>? expected, Dictionary<string, string>? actual)
    {
        int expectedCount = expected?.Count ?? 0;
        int actualCount = actual?.Count ?? 0;
        if (expectedCount != actualCount) return false;
        if (expectedCount == 0) return true;
        if (expected == null || actual == null) return false;

        foreach (KeyValuePair<string, string> pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out string value)) return false;
            if (!string.Equals(pair.Value, value, StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
