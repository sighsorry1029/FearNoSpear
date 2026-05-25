using System.Collections.Generic;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearLocator
{
    internal const string LoadedDropSource = "loaded ItemDrop";

    private const int MaxRecords = 12;
    internal const float LoadedDropMergeRadius = 64f;
    private const float ServerRequestTimeoutSeconds = 2f;
    private static readonly SpearRecordStore Records = new(MaxRecords, LoadedDropMergeRadius);
    private static float _serverRequestFallbackAt = float.NegativeInfinity;

    internal static void Clear()
    {
        Records.Clear();
        SpearPinManager.Clear();
    }

    internal static void RecordProjectilePosition(Projectile projectile, Vector3 position, string source)
    {
        if (!FearNoSpearPlugin.Cfg.Enabled.Value) return;
        if (!SpearProjectileDetector.IsTrackedSpearProjectile(projectile)) return;

        ItemDrop.ItemData? spawnItem = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_spawnItem, projectile, null);
        if (spawnItem == null) return;

        string itemKey = SpearItemIdentity.BuildLocatorKey(spawnItem);
        string recordKey = BuildProjectileRecordKey(projectile, itemKey);
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer == null)
        {
            if (SpearOwnership.TryResolveProjectileOwnerPlayerId(projectile, out long serverPlayerId))
            {
                SpearNetwork.RecordServerKnownSpear(serverPlayerId, recordKey, itemKey, position, source, fromTrackedProjectile: true);
            }

            return;
        }

        if (!SpearOwnership.IsLocalPlayerProjectile(projectile, spawnItem, localPlayer)) return;

        Record(recordKey, itemKey, position, source, fromTrackedProjectile: true, immediateServerReport: false);
    }

    internal static void RecordKnownSpearDrop(ItemDrop.ItemData? item, Vector3 position, string source)
    {
        if (item == null) return;
        if (!SpearProjectileDetector.IsSpearItem(item)) return;

        string itemKey = SpearItemIdentity.BuildLocatorKey(item);
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer == null) return;

        if (!HasRecordForItem(itemKey) && !SpearItemIdentity.BelongsToPlayer(item, localPlayer)) return;

        bool known = HasRecordForItem(itemKey);
        Record(itemKey, itemKey, position, source, fromTrackedProjectile: known, mergeByKeyOnly: false, immediateServerReport: true, fromLoadedDrop: true);
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

        PinLocalKnownSpears("No tracked spear location found.", "Pinned local spear location");
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
        PinLocalKnownSpears("No tracked spear location found on server or client.", "Pinned spear location");
    }

    internal static void MarkSpearPickedUp(string itemKey, Vector3 pickupPosition)
    {
        if (string.IsNullOrEmpty(itemKey)) return;

        int removedPins = SpearPinManager.RemoveForPickedSpear(itemKey, pickupPosition);
        int removedRecords = RemoveRecordsForPickedSpear(itemKey);
        SpearNetwork.ReportSpearPickup(itemKey, pickupPosition);

        if (FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogInfo($"Cleared picked spear locator state: itemKey={itemKey}; removedPins={removedPins}; removedRecords={removedRecords}; pos={pickupPosition}");
        }
    }

    internal static void PinLocalAfterEmptyServer()
    {
        ClearPendingServerRequest();
        PinLocalKnownSpears("No tracked spear location found on server or client.", "Server had no spear record; pinned local last-known location");
    }

    internal static void UpdatePendingServerRequest()
    {
        if (_serverRequestFallbackAt <= 0f || Time.time < _serverRequestFallbackAt) return;

        ClearPendingServerRequest();
        PinLocalKnownSpears("No response from server and no local tracked spear location found.", "Server did not respond; pinned local last-known location");
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
        foreach (ItemDrop drop in Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
        {
            if (drop == null || drop.m_itemData == null) continue;
            if (!SpearProjectileDetector.IsSpearItem(drop.m_itemData)) continue;

            string key = SpearItemIdentity.BuildLocatorKey(drop.m_itemData);
            string recordKey = BuildDropRecordKey(drop, key);
            bool known = HasRecordForItem(key);
            if (!known) continue;

            Record(recordKey, key, drop.transform.position, LoadedDropSource, fromTrackedProjectile: known, mergeByKeyOnly: false, immediateServerReport: true, fromLoadedDrop: true);
        }
    }

    private static SpearLocationRecord Record(string recordKey, string itemKey, Vector3 position, string source, bool fromTrackedProjectile, bool mergeByKeyOnly = true, bool immediateServerReport = false, bool fromLoadedDrop = false, bool reportToServer = true, float lastUpdated = -1f)
    {
        SpearLocationRecord record = Records.Record(
            recordKey,
            itemKey,
            position,
            source,
            fromTrackedProjectile,
            fromLoadedDrop,
            mergeByItemNearPosition: !mergeByKeyOnly,
            lastUpdated);
        if (reportToServer)
        {
            SpearNetwork.ReportSpearLocation(recordKey, itemKey, position, source, record.FromTrackedProjectile, immediateServerReport);
        }
        return record;
    }

    private static void MergeServerRecords(List<SpearLocationRecord> records)
    {
        foreach (SpearLocationRecord record in records)
        {
            if (string.IsNullOrEmpty(record.Key)) continue;

            string itemKey = string.IsNullOrEmpty(record.ItemKey) ? record.Key : record.ItemKey;
            bool dropSource = IsDropSource(record.Source);
            Record(
                record.Key,
                itemKey,
                record.Position,
                record.Source,
                fromTrackedProjectile: true,
                mergeByKeyOnly: false,
                immediateServerReport: false,
                fromLoadedDrop: dropSource,
                reportToServer: false,
                lastUpdated: record.LastUpdated);
        }
    }

    private static bool HasRecordForItem(string itemKey)
    {
        return Records.HasItem(itemKey);
    }

    private static string BuildProjectileRecordKey(Projectile projectile, string itemKey)
    {
        ZNetView? nview = ReflectionCache.GetNView(projectile);
        string? zdoKey = TryGetZdoKey(nview);
        if (!string.IsNullOrEmpty(zdoKey)) return $"{itemKey}#projectile:{zdoKey}";

        return $"{itemKey}#projectile-local:{projectile.GetInstanceID()}";
    }

    private static string BuildDropRecordKey(ItemDrop drop, string itemKey)
    {
        ZNetView? nview = drop.GetComponent<ZNetView>();
        string? zdoKey = TryGetZdoKey(nview);
        if (!string.IsNullOrEmpty(zdoKey)) return $"{itemKey}#drop:{zdoKey}";

        return $"{itemKey}#drop-local:{drop.GetInstanceID()}";
    }

    private static string? TryGetZdoKey(ZNetView? nview)
    {
        if (nview == null || !nview.IsValid()) return null;

        ZDO zdo = nview.GetZDO();
        if (zdo == null || !zdo.IsValid()) return null;

        return zdo.m_uid.ToString();
    }

    private static int RemoveRecordsForPickedSpear(string itemKey)
    {
        return Records.RemoveByItem(itemKey).Count;
    }

    private static bool IsDropSource(string source)
    {
        return source == LoadedDropSource || source == "rescued spear" || source == "existing spear drop";
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
