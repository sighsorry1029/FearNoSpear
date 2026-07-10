using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearPinManager
{
    private const string PinName = "Spear!";
    private const float PinPickupRemoveRadius = 24f;

    private static readonly List<SpearPinRecord> ActivePins = new();

    internal static void Clear()
    {
        ActivePins.Clear();
    }

    internal static int PinRecords(List<SpearLocationRecord> records, Action<string> showMessage)
    {
        RemoveActivePinsFromMap();

        int pinned = 0;
        int count = records.Count;
        for (int i = 0; i < count; ++i)
        {
            SpearLocationRecord record = records[i];
            string pinName = count == 1 ? PinName : $"Spear {i + 1}";
            if (!TryPinPosition(record.Position, pinName, showMessage)) continue;

            ++pinned;
            ActivePins.Add(new SpearPinRecord
            {
                Key = record.Key,
                Name = pinName,
                Position = record.Position
            });
        }

        return pinned;
    }

    internal static int RemoveForPickedSpear(string recordKey)
    {
        Minimap minimap = Minimap.instance;
        if (minimap == null)
        {
            ActivePins.RemoveAll(pin => MatchesPickedSpear(pin, recordKey));
            return 0;
        }

        List<SpearPinRecord> trackedPins = ActivePins
            .Where(pin => MatchesPickedSpear(pin, recordKey))
            .ToList();
        if (trackedPins.Count == 0) return 0;

        int removed = 0;
        foreach (SpearPinRecord trackedPin in trackedPins)
        {
            Minimap.PinData? pinData = FindMatchingMinimapPin(minimap, trackedPin);
            if (pinData == null) continue;

            minimap.RemovePin(pinData);
            ++removed;
        }

        ActivePins.RemoveAll(pin => MatchesPickedSpear(pin, recordKey));
        return removed;
    }

    private static bool MatchesPickedSpear(SpearPinRecord pin, string recordKey)
    {
        return !string.IsNullOrEmpty(recordKey) &&
               string.Equals(pin.Key, recordKey, StringComparison.Ordinal);
    }

    private static int RemoveActivePinsFromMap()
    {
        Minimap minimap = Minimap.instance;
        if (minimap == null)
        {
            ActivePins.Clear();
            return 0;
        }

        int removed = 0;
        foreach (SpearPinRecord activePin in ActivePins)
        {
            Minimap.PinData? pinData = FindMatchingMinimapPin(minimap, activePin);
            if (pinData == null) continue;

            minimap.RemovePin(pinData);
            ++removed;
        }

        ActivePins.Clear();
        return removed;
    }

    private static bool TryPinPosition(Vector3 position, string pinName, Action<string> showMessage)
    {
        Minimap minimap = Minimap.instance;
        if (minimap == null)
        {
            showMessage("No minimap is available yet.");
            return false;
        }

        minimap.DiscoverLocation(position, Minimap.PinType.Icon3, pinName, showMap: true);
        return true;
    }

    private static Minimap.PinData? FindMatchingMinimapPin(Minimap minimap, SpearPinRecord trackedPin)
    {
        List<Minimap.PinData>? pins = ReflectionCache.F_minimapPins?.GetValue(minimap) as List<Minimap.PinData>;
        if (pins == null) return null;

        return pins
            .Where(pin => pin != null && pin.m_save && pin.m_type == Minimap.PinType.Icon3)
            .Where(pin => string.Equals(pin.m_name, trackedPin.Name, StringComparison.Ordinal))
            .Where(pin => Vector3.Distance(pin.m_pos, trackedPin.Position) <= PinPickupRemoveRadius)
            .OrderBy(pin => Vector3.SqrMagnitude(pin.m_pos - trackedPin.Position))
            .FirstOrDefault();
    }

    private sealed class SpearPinRecord
    {
        internal string Key = string.Empty;
        internal string Name = string.Empty;
        internal Vector3 Position;
    }
}
