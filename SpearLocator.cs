using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearLocator
{
    private const float ServerRequestTimeoutSeconds = 2f;

    private static readonly List<SpearLocationRecord> PendingLoadedRecords = new();
    private static float _serverRequestFallbackAt = float.NegativeInfinity;

    internal static void Clear()
    {
        PendingLoadedRecords.Clear();
        ClearPendingServerRequest();
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

        PendingLoadedRecords.Clear();
        PendingLoadedRecords.AddRange(FindLoadedSpearDrops(localPlayer));

        if (SpearNetwork.RequestServerSpearLocation(localPlayer))
        {
            _serverRequestFallbackAt = Time.time + ServerRequestTimeoutSeconds;
            ShowMessage("Requesting spear location from server...");
            return;
        }

        PinPendingLoadedSpears("No spear drop location found.", "Pinned local spear location");
    }

    internal static void PinServerSpears(List<SpearLocationRecord> serverRecords)
    {
        ClearPendingServerRequest();

        if (Player.m_localPlayer == null)
        {
            PendingLoadedRecords.Clear();
            FearNoSpearPlugin.Log.LogInfo("Received a spear location from the server, but no local player exists.");
            return;
        }

        List<SpearLocationRecord> records = MergeServerWithLoadedRecords(serverRecords);
        PendingLoadedRecords.Clear();
        PinRecords(records, "No spear drop location found on server or client.", "Pinned spear location");
    }

    internal static void MarkSpearPickedUp(string recordKey)
    {
        if (string.IsNullOrEmpty(recordKey)) return;
        SpearPinManager.RemoveForPickedSpear(recordKey);
    }

    internal static void PinLocalAfterEmptyServer()
    {
        ClearPendingServerRequest();
        PinPendingLoadedSpears(
            "No spear drop location found on server or client.",
            "Server had no spear record; pinned local loaded spear location");
    }

    internal static void UpdatePendingServerRequest()
    {
        if (_serverRequestFallbackAt <= 0f || Time.time < _serverRequestFallbackAt) return;

        ClearPendingServerRequest();
        PinPendingLoadedSpears(
            "No response from server and no local spear drop location found.",
            "Server did not respond; pinned local loaded spear location");
    }

    private static void ClearPendingServerRequest()
    {
        _serverRequestFallbackAt = float.NegativeInfinity;
    }

    private static bool PinPendingLoadedSpears(string missingMessage, string pinnedPrefix)
    {
        List<SpearLocationRecord> records = PendingLoadedRecords.ToList();
        PendingLoadedRecords.Clear();
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

    private static List<SpearLocationRecord> FindLoadedSpearDrops(Player localPlayer)
    {
        long localPlayerId = localPlayer.GetPlayerID();
        if (localPlayerId == 0L) return new List<SpearLocationRecord>();

        Vector3 playerPosition = localPlayer.transform.position;
        List<SpearLocationRecord> records = new();
        foreach (ItemDrop drop in Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
        {
            if (drop == null || drop.m_itemData == null) continue;
            if (!SpearProjectileDetector.IsSpearItem(drop.m_itemData)) continue;
            if (SpearThrowerMetadata.ReadFromDrop(drop) != localPlayerId) continue;

            records.Add(new SpearLocationRecord
            {
                Key = SpearItemIdentity.BuildDropRecordKey(drop),
                Position = drop.transform.position
            });
        }

        return records
            .OrderBy(record => Vector3.SqrMagnitude(record.Position - playerPosition))
            .Take(FearNoSpearConfig.MaxPinsPerCommand)
            .ToList();
    }

    private static List<SpearLocationRecord> MergeServerWithLoadedRecords(List<SpearLocationRecord> serverRecords)
    {
        Dictionary<string, SpearLocationRecord> loadedByKey = new();
        foreach (SpearLocationRecord record in PendingLoadedRecords)
        {
            if (!string.IsNullOrEmpty(record.Key)) loadedByKey[record.Key] = record;
        }

        HashSet<string> addedKeys = new();
        List<SpearLocationRecord> merged = new();
        foreach (SpearLocationRecord serverRecord in serverRecords)
        {
            if (string.IsNullOrEmpty(serverRecord.Key) || !addedKeys.Add(serverRecord.Key)) continue;

            if (loadedByKey.TryGetValue(serverRecord.Key, out SpearLocationRecord loadedRecord))
            {
                merged.Add(loadedRecord);
                loadedByKey.Remove(serverRecord.Key);
            }
            else
            {
                merged.Add(serverRecord);
            }
        }

        foreach (SpearLocationRecord loadedRecord in PendingLoadedRecords)
        {
            if (!loadedByKey.Remove(loadedRecord.Key) || !addedKeys.Add(loadedRecord.Key)) continue;
            merged.Add(loadedRecord);
        }

        return merged.Take(FearNoSpearConfig.MaxPinsPerCommand).ToList();
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
