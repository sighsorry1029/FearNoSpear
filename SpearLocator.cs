using System.Collections.Generic;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearLocator
{
    internal const string LoadedDropSource = "loaded ItemDrop";
    internal const string WorldZdoDropSource = "world ZDO spear drop";

    private const int MaxRecords = 12;
    private const float ServerRequestTimeoutSeconds = 2f;
    private static readonly SpearRecordStore Records = new(MaxRecords);
    private static float _serverRequestFallbackAt = float.NegativeInfinity;

    internal static void Clear()
    {
        Records.Clear();
        SpearPinManager.Clear();
    }

    internal static void PinKnownSpear()
    {
        if (!FearNoSpearPlugin.Cfg.Enabled.Value)
        {
            ShowMessage("FearNoSpear is disabled.");
            return;
        }

        Player localPlayer = Player.m_localPlayer;
        if (localPlayer == null)
        {
            FearNoSpearPlugin.Log.LogInfo("Could not run !myspear because no local player exists.");
            return;
        }

        RefreshLoadedSpearDrops();

        if (SpearNetwork.RequestServerSpearLocation(localPlayer))
        {
            _serverRequestFallbackAt = Time.time + ServerRequestTimeoutSeconds;
            ShowMessage("Requesting spear location from server...");
            return;
        }

        PinLocalKnownSpears("No spear drop location found.", "Pinned local spear location");
    }

    internal static void PinServerSpears(List<SpearLocationRecord> records)
    {
        ClearPendingServerRequest();

        Player localPlayer = Player.m_localPlayer;
        if (localPlayer == null)
        {
            FearNoSpearPlugin.Log.LogInfo("Received a spear location from the server, but no local player exists.");
            return;
        }

        MergeServerRecords(records);
        PinLocalKnownSpears("No spear drop location found on server or client.", "Pinned spear location");
    }

    internal static void MarkSpearPickedUp(string recordKey, string itemKey, Vector3 pickupPosition)
    {
        if (string.IsNullOrEmpty(recordKey)) return;

        int removedPins = SpearPinManager.RemoveForPickedSpear(recordKey, pickupPosition);
        int removedRecords = RemoveRecordsForPickedSpear(recordKey);

        if (FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogInfo($"Cleared picked spear locator state: recordKey={recordKey}; itemKey={itemKey}; removedPins={removedPins}; removedRecords={removedRecords}; pos={pickupPosition}");
        }
    }

    internal static void PinLocalAfterEmptyServer()
    {
        ClearPendingServerRequest();
        PinLocalKnownSpears("No spear drop location found on server or client.", "Server had no spear record; pinned local loaded spear location");
    }

    internal static void UpdatePendingServerRequest()
    {
        if (_serverRequestFallbackAt <= 0f || Time.time < _serverRequestFallbackAt) return;

        ClearPendingServerRequest();
        PinLocalKnownSpears("No response from server and no local spear drop location found.", "Server did not respond; pinned local loaded spear location");
    }

    private static void ClearPendingServerRequest()
    {
        _serverRequestFallbackAt = float.NegativeInfinity;
    }

    private static bool PinLocalKnownSpears(string missingMessage, string pinnedPrefix)
    {
        List<SpearLocationRecord> records = Records.SelectBest(FearNoSpearPlugin.Cfg.GetMaxPinsPerCommand());
        return PinRecords(records, missingMessage, pinnedPrefix);
    }

    private static bool PinRecords(List<SpearLocationRecord> records, string missingMessage, string pinnedPrefix)
    {
        int pinned = SpearPinManager.PinRecords(records, ShowMessage);

        if (records.Count == 0)
        {
            ShowMessage(missingMessage);
            return false;
        }

        if (pinned == 0) return false;

        ShowMessage(pinned == 1 ? $"{pinnedPrefix}." : $"{pinnedPrefix}s ({pinned}).");
        return true;
    }

    private static void RefreshLoadedSpearDrops()
    {
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer == null) return;

        long localPlayerId = localPlayer.GetPlayerID();
        if (localPlayerId == 0L) return;

        foreach (ItemDrop drop in Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
        {
            if (drop == null || drop.m_itemData == null) continue;
            if (!SpearProjectileDetector.IsSpearItem(drop.m_itemData)) continue;

            string key = SpearItemIdentity.BuildLocatorKey(drop.m_itemData);
            string recordKey = SpearItemIdentity.BuildDropRecordKey(drop, key);
            long throwerPlayerId = SpearThrowerMetadata.ReadFromDrop(drop);
            if (throwerPlayerId == 0L || throwerPlayerId != localPlayerId) continue;

            Record(recordKey, key, drop.transform.position, LoadedDropSource);
        }
    }

    private static SpearLocationRecord Record(string recordKey, string itemKey, Vector3 position, string source, float lastUpdated = -1f)
    {
        return Records.Record(
            recordKey,
            itemKey,
            position,
            source,
            lastUpdated);
    }

    private static void MergeServerRecords(List<SpearLocationRecord> records)
    {
        foreach (SpearLocationRecord record in records)
        {
            if (string.IsNullOrEmpty(record.Key)) continue;

            string itemKey = string.IsNullOrEmpty(record.ItemKey) ? record.Key : record.ItemKey;
            Record(
                record.Key,
                itemKey,
                record.Position,
                record.Source,
                lastUpdated: record.LastUpdated);
        }
    }

    private static int RemoveRecordsForPickedSpear(string recordKey)
    {
        return Records.RemovePicked(recordKey);
    }

    private static void ShowMessage(string message)
    {
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer != null)
        {
            localPlayer.Message(MessageHud.MessageType.TopLeft, message, 0, null);
        }
        else
        {
            FearNoSpearPlugin.Log.LogInfo(message);
        }
    }

}
