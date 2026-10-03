using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearLocator
{
    private const float PollSeconds = 5f;
    private const float RequestTimeoutSeconds = 2f;
    private static readonly FieldInfo? DropInstances = AccessTools.Field(typeof(ItemDrop), "s_instances");
    private static readonly Dictionary<string, ItemDrop> LoadedDrops = new();
    private static readonly Dictionary<string, float> PickedUpUntil = new();
    private static readonly List<SpearLocationRecord> ServerRecords = new();
    private static long _playerId;
    private static int _sequence;
    private static int _pendingRequest;
    private static float _deadline;
    private static float _nextPoll;
    private static float _nextLoadedScan;
    private static float _serverRecordsExpire;

    internal readonly struct Target
    {
        internal readonly string Key;
        internal readonly Vector3 Position;
        internal readonly Sprite? Icon;

        internal Target(string key, Vector3 position, Sprite? icon)
        {
            Key = key;
            Position = position;
            Icon = icon;
        }
    }

    internal static void Clear()
    {
        LoadedDrops.Clear();
        PickedUpUntil.Clear();
        ServerRecords.Clear();
        _playerId = 0L;
        _pendingRequest = 0;
        _deadline = _nextPoll = _nextLoadedScan = _serverRecordsExpire = 0f;
        // Do not reuse request IDs when reconnecting to the same server.
    }

    internal static void Update()
    {
        Player player = Player.m_localPlayer;
        if (player == null || player.GetPlayerID() == 0L ||
            FearNoSpearPlugin.Cfg.SpearIndicatorStyle.Value == FearNoSpearPlugin.IndicatorStyle.Off)
        {
            if (_playerId != 0L) Clear();
            return;
        }
        if (_playerId != player.GetPlayerID())
        {
            Clear();
            _playerId = player.GetPlayerID();
        }

        float now = Time.unscaledTime;
        if (_pendingRequest != 0 && now >= _deadline)
        {
            _pendingRequest = 0;
            _nextPoll = now + 15f;
        }
        if (now > _serverRecordsExpire) ServerRecords.Clear();

        RefreshLoadedDrops(player);
        if (_pendingRequest == 0 && now >= _nextPoll) Request(player);
    }

    private static void Request(Player player)
    {
        _pendingRequest = ++_sequence;
        if (_pendingRequest == 0) _pendingRequest = ++_sequence;
        _deadline = Time.unscaledTime + RequestTimeoutSeconds;
        _nextPoll = Time.unscaledTime + PollSeconds;
        // Register state before invoking RPC: a local host can respond synchronously.
        if (SpearNetwork.RequestServerSpearLocation(player, _pendingRequest)) return;
        _pendingRequest = 0;
        _nextPoll = Time.unscaledTime + 15f;
    }

    internal static void ReceiveServerRecords(int requestId, List<SpearLocationRecord> records)
    {
        if (requestId != _pendingRequest || _pendingRequest == 0) return;
        _pendingRequest = 0;
        Player player = Player.m_localPlayer;
        if (player == null || player.GetPlayerID() != _playerId ||
            FearNoSpearPlugin.Cfg.SpearIndicatorStyle.Value == FearNoSpearPlugin.IndicatorStyle.Off) return;

        ServerRecords.Clear();
        ServerRecords.AddRange(records);
        _serverRecordsExpire = Time.unscaledTime + PollSeconds * 2f;
        RefreshLoadedDrops(player, force: true);
    }

    internal static void MarkSpearPickedUp(string recordKey)
    {
        if (_playerId == 0L || string.IsNullOrEmpty(recordKey)) return;
        LoadedDrops.Remove(recordKey);
        ServerRecords.RemoveAll(record => record.Key == recordKey);
        PickedUpUntil[recordKey] = Time.unscaledTime + PollSeconds * 2f;
    }

    private static void RefreshLoadedDrops(Player player, bool force = false)
    {
        if (!force && Time.unscaledTime < _nextLoadedScan) return;
        _nextLoadedScan = Time.unscaledTime + 0.5f;
        LoadedDrops.Clear();
        foreach (string key in PickedUpUntil.Where(pair => pair.Value <= Time.unscaledTime).Select(pair => pair.Key).ToList())
            PickedUpUntil.Remove(key);

        IEnumerable<ItemDrop> drops = DropInstances?.GetValue(null) as List<ItemDrop>
            ?? (IEnumerable<ItemDrop>)UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
        foreach (ItemDrop drop in drops)
        {
            if (drop == null || !SpearProjectileDetector.IsSpearItem(drop.m_itemData)) continue;
            if (SpearThrowerMetadata.ReadFromDrop(drop) != player.GetPlayerID()) continue;
            string key = SpearItemIdentity.BuildDropRecordKey(drop);
            if (!string.IsNullOrEmpty(key) && !PickedUpUntil.ContainsKey(key)) LoadedDrops[key] = drop;
        }
    }

    internal static void GetNearest(Player player, List<Target> targets, int limit)
    {
        targets.Clear();
        limit = Mathf.Clamp(limit, 1, FearNoSpearConfig.MaxLocationResults);
        Vector3 origin = player.transform.position;
        foreach (KeyValuePair<string, ItemDrop> pair in LoadedDrops)
        {
            ItemDrop drop = pair.Value;
            if (drop == null) continue;
            AddNearest(targets, new Target(pair.Key, drop.transform.position,
                GetIcon(drop.m_itemData, drop.m_itemData.m_variant)), origin, limit);
        }
        foreach (SpearLocationRecord record in ServerRecords)
        {
            // A loaded drop's current transform supersedes its last server position.
            if (LoadedDrops.ContainsKey(record.Key) || PickedUpUntil.ContainsKey(record.Key)) continue;
            GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(record.PrefabHash) : null;
            AddNearest(targets, new Target(record.Key, record.Position,
                GetIcon(prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData : null, record.Variant)), origin, limit);
        }
    }

    private static void AddNearest(List<Target> targets, Target candidate, Vector3 origin, int limit)
    {
        if (string.IsNullOrEmpty(candidate.Key)) return;
        for (int i = 0; i < targets.Count; ++i)
            if (targets[i].Key == candidate.Key) return;

        float distance = (candidate.Position - origin).sqrMagnitude;
        int index = 0;
        while (index < targets.Count)
        {
            float otherDistance = (targets[index].Position - origin).sqrMagnitude;
            if (distance < otherDistance ||
                (distance == otherDistance && string.CompareOrdinal(candidate.Key, targets[index].Key) < 0)) break;
            ++index;
        }
        if (index >= limit) return;
        targets.Insert(index, candidate);
        if (targets.Count > limit) targets.RemoveAt(limit);
    }

    private static Sprite? GetIcon(ItemDrop.ItemData? item, int variant)
    {
        Sprite[]? icons = item?.m_shared?.m_icons;
        return icons != null && icons.Length > 0 ? icons[Mathf.Clamp(variant, 0, icons.Length - 1)] : null;
    }
}
