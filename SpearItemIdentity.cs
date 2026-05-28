using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearItemIdentity
{
    internal static string BuildLocatorKey(ItemDrop.ItemData item)
    {
        // Locator identity should survive normal durability changes; duplicate cleanup uses IsEquivalent for stricter matching.
        string prefabName = item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty;
        string sharedName = item.m_shared?.m_name ?? string.Empty;
        List<string> parts = new()
        {
            Escape(prefabName),
            Escape(sharedName),
            item.m_quality.ToString(),
            item.m_variant.ToString(),
            item.m_worldLevel.ToString(),
            item.m_crafterID.ToString(),
            Escape(item.m_crafterName)
        };

        if (item.m_customData != null)
        {
            foreach (KeyValuePair<string, string> pair in item.m_customData.OrderBy(pair => pair.Key))
            {
                parts.Add($"{Escape(pair.Key)}={Escape(pair.Value)}");
            }
        }

        return string.Join("|", parts);
    }

    internal static string BuildLocatorKey(string prefabName, ItemDrop.ItemData prefabItem, ZDO zdo)
    {
        string sharedName = prefabItem.m_shared?.m_name ?? string.Empty;
        int quality = zdo.GetInt(ZDOVars.s_quality, prefabItem.m_quality);
        int variant = zdo.GetInt(ZDOVars.s_variant, prefabItem.m_variant);
        int worldLevel = zdo.GetInt(ZDOVars.s_worldLevel, prefabItem.m_worldLevel);
        long crafterId = zdo.GetLong(ZDOVars.s_crafterID, prefabItem.m_crafterID);
        string crafterName = zdo.GetString(ZDOVars.s_crafterName, prefabItem.m_crafterName);

        List<string> parts = new()
        {
            Escape(prefabName),
            Escape(sharedName),
            quality.ToString(),
            variant.ToString(),
            worldLevel.ToString(),
            crafterId.ToString(),
            Escape(crafterName)
        };

        int dataCount = zdo.GetInt(ZDOVars.s_dataCount, 0);
        Dictionary<string, string> customData = new();
        for (int i = 0; i < dataCount; ++i)
        {
            string key = zdo.GetString($"data_{i}");
            if (string.IsNullOrEmpty(key)) continue;

            customData[key] = zdo.GetString($"data__{i}");
        }

        foreach (KeyValuePair<string, string> pair in customData.OrderBy(pair => pair.Key))
        {
            parts.Add($"{Escape(pair.Key)}={Escape(pair.Value)}");
        }

        return string.Join("|", parts);
    }

    internal static string BuildDropRecordKey(ItemDrop drop, string itemKey)
    {
        ZNetView? nview = drop.GetComponent<ZNetView>();
        string? zdoKey = TryGetZdoKey(nview);
        if (zdoKey != null && zdoKey.Length > 0) return BuildDropRecordKey(zdoKey);

        return $"{itemKey}#drop-local:{drop.GetInstanceID()}";
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

    private static string Escape(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("|", "\\p")
            .Replace("=", "\\e");
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
