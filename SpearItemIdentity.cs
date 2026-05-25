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

    internal static bool BelongsToPlayer(ItemDrop.ItemData item, Player player)
    {
        long playerId = player.GetPlayerID();
        return playerId != 0L && item.m_crafterID == playerId;
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
