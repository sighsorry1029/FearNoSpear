using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearLocator
{
    private const float PollSeconds = 5f;
    private const float RequestTimeoutSeconds = 2f;
    private static readonly FieldInfo? DropInstances = typeof(ItemDrop).GetField("s_instances", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly Dictionary<string, ItemDrop> LoadedDrops = new();
    private static readonly HashSet<TombStone> ObservedTombstones = new();
    private static readonly Dictionary<string, TombStone> LoadedTombstones = new();
    private static readonly List<Target> NewestTombstones = new(FearNoSpearConfig.MaxTombstoneResults);
    private static readonly List<Target> SelectedTargets = new(FearNoSpearConfig.MaxIndicatorResults);
    private static readonly Dictionary<string, float> PickedUpUntil = new();
    private static readonly List<SpearLocationRecord> ServerRecords = new();
    private static long _playerId;
    private static int _sequence;
    private static int _pendingRequest;
    private static float _deadline;
    private static float _nextPoll;
    private static float _nextLoadedScan;
    private static float _serverRecordsExpire;
    private static bool _weaponsEnabled;
    private static bool _tombstonesEnabled;
    private static bool _seedTombstones = true;
    private static bool _selectionDirty = true;
    private static int _selectedWeaponLimit;
    private static int _selectedTombstoneLimit;

    internal readonly struct Target
    {
        internal readonly string Key;
        internal readonly Vector3 Position;
        internal readonly Sprite? Icon;
        internal readonly LocationKind Kind;
        internal readonly long CreatedTicks;

        internal Target(string key, Vector3 position, Sprite? icon, LocationKind kind = LocationKind.Weapon, long createdTicks = 0L)
        {
            Key = key;
            Position = position;
            Icon = icon;
            Kind = kind;
            CreatedTicks = createdTicks;
        }
    }

    internal static void Clear()
    {
        LoadedDrops.Clear();
        ObservedTombstones.Clear();
        LoadedTombstones.Clear();
        NewestTombstones.Clear();
        SelectedTargets.Clear();
        _selectionDirty = true;
        _selectedWeaponLimit = _selectedTombstoneLimit = 0;
        _seedTombstones = true;
        _weaponsEnabled = _tombstonesEnabled = false;
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
        bool weapons = FearNoSpearPlugin.Cfg.SpearIndicatorStyle.Value != FearNoSpearPlugin.IndicatorStyle.Off;
        bool tombstones = FearNoSpearPlugin.Cfg.TombstoneIndicatorStyle.Value != FearNoSpearPlugin.IndicatorStyle.Off;
        if (player == null || player.GetPlayerID() == 0L || (!weapons && !tombstones))
        {
            if (_playerId != 0L) Clear();
            return;
        }
        if (_playerId != player.GetPlayerID())
        {
            Clear();
            _playerId = player.GetPlayerID();
        }

        if (_weaponsEnabled != weapons || _tombstonesEnabled != tombstones)
        {
            _weaponsEnabled = weapons;
            _tombstonesEnabled = tombstones;
            _pendingRequest = 0;
            _nextPoll = _nextLoadedScan = 0f;
            ServerRecords.Clear();
            ObservedTombstones.Clear();
            _seedTombstones = true;
        }

        float now = Time.unscaledTime;
        if (_pendingRequest != 0 && now >= _deadline)
        {
            _pendingRequest = 0;
            _nextPoll = now + 15f;
        }
        if (ServerRecords.Count > 0 && now > _serverRecordsExpire)
        {
            ServerRecords.Clear();
            _selectionDirty = true;
        }

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
        if (SpearNetwork.RequestServerSpearLocation(player, _pendingRequest, _weaponsEnabled, _tombstonesEnabled)) return;
        _pendingRequest = 0;
        _nextPoll = Time.unscaledTime + 15f;
    }

    internal static void ReceiveServerRecords(int requestId, List<SpearLocationRecord> records)
    {
        if (requestId != _pendingRequest || _pendingRequest == 0) return;
        _pendingRequest = 0;
        Player player = Player.m_localPlayer;
        if (player == null || player.GetPlayerID() != _playerId ||
            (FearNoSpearPlugin.Cfg.SpearIndicatorStyle.Value == FearNoSpearPlugin.IndicatorStyle.Off &&
             FearNoSpearPlugin.Cfg.TombstoneIndicatorStyle.Value == FearNoSpearPlugin.IndicatorStyle.Off)) return;

        ServerRecords.Clear();
        ServerRecords.AddRange(records);
        _serverRecordsExpire = Time.unscaledTime + PollSeconds * 2f;
        RefreshLoadedDrops(player, force: true);
    }

    internal static void ForgetRecoveredTarget(string recordKey)
    {
        if (_playerId == 0L || string.IsNullOrEmpty(recordKey)) return;
        LoadedDrops.Remove(recordKey);
        LoadedTombstones.Remove(recordKey);
        ServerRecords.RemoveAll(record => record.Key == recordKey);
        PickedUpUntil[recordKey] = Time.unscaledTime + PollSeconds * 2f;
        _selectionDirty = true;
    }

    private static void RefreshLoadedDrops(Player player, bool force = false)
    {
        if (!force && Time.unscaledTime < _nextLoadedScan) return;
        _nextLoadedScan = Time.unscaledTime + 0.5f;
        _selectionDirty = true;
        LoadedDrops.Clear();
        LoadedTombstones.Clear();
        foreach (string key in PickedUpUntil.Where(pair => pair.Value <= Time.unscaledTime).Select(pair => pair.Key).ToList())
            PickedUpUntil.Remove(key);

        if (_weaponsEnabled)
        {
            IEnumerable<ItemDrop> drops = DropInstances?.GetValue(null) as List<ItemDrop>
                ?? (IEnumerable<ItemDrop>)UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
            foreach (ItemDrop drop in drops)
            {
                if (drop == null || SpearThrowerMetadata.ReadFromDrop(drop) != player.GetPlayerID()) continue;
                string key = SpearItemIdentity.BuildDropRecordKey(drop);
                if (!string.IsNullOrEmpty(key) && !PickedUpUntil.ContainsKey(key)) LoadedDrops[key] = drop;
            }
        }
        if (!_tombstonesEnabled) return;
        if (_seedTombstones)
        {
            foreach (TombStone tombstone in UnityEngine.Object.FindObjectsByType<TombStone>(FindObjectsSortMode.None))
                ObservedTombstones.Add(tombstone);
            _seedTombstones = false;
        }
        ObservedTombstones.RemoveWhere(tombstone => tombstone == null);
        foreach (TombStone tombstone in ObservedTombstones)
        {
            ZNetView? nview = tombstone.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) continue;
            ZDO zdo = nview.GetZDO();
            if (zdo.GetLong(ZDOVars.s_owner, 0L) != player.GetPlayerID()) continue;
            LoadedTombstones[SpearItemIdentity.BuildTombstoneRecordKey(zdo.m_uid)] = tombstone;
        }
    }

    internal static void ObserveTombstone(TombStone tombstone)
    {
        if (FearNoSpearPlugin.Cfg.TombstoneIndicatorStyle.Value != FearNoSpearPlugin.IndicatorStyle.Off &&
            Player.m_localPlayer != null) ObservedTombstones.Add(tombstone);
    }

    internal static void HideEmptyTombstone(TombStone tombstone)
    {
        if (_playerId == 0L || !IsEmpty(tombstone)) return;
        ZNetView? nview = tombstone.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) return;
        ZDO zdo = nview.GetZDO();
        if (zdo.GetLong(ZDOVars.s_owner, 0L) == _playerId)
            ForgetRecoveredTarget(SpearItemIdentity.BuildTombstoneRecordKey(zdo.m_uid));
    }

    private static bool IsEmpty(TombStone tombstone)
    {
        Container? container = ReflectionCache.GetTombstoneContainer(tombstone);
        return container != null && container.GetInventory().NrOfItems() == 0;
    }

    internal static void GetTargets(Player player, List<Target> targets, int limit)
    {
        targets.Clear();
        limit = FearNoSpearPlugin.Cfg.SpearIndicatorStyle.Value == FearNoSpearPlugin.IndicatorStyle.Off
            ? 0 : Mathf.Clamp(limit, 1, FearNoSpearConfig.MaxLocationResults);
        int tombstoneLimit = FearNoSpearPlugin.Cfg.TombstoneIndicatorStyle.Value == FearNoSpearPlugin.IndicatorStyle.Off
            ? 0 : Mathf.Clamp(FearNoSpearPlugin.Cfg.MaxDisplayedTombstones.Value, 1, FearNoSpearConfig.MaxTombstoneResults);
        if (_selectionDirty || _selectedWeaponLimit != limit || _selectedTombstoneLimit != tombstoneLimit)
        {
            SelectTargets(player.transform.position, limit, tombstoneLimit);
            _selectionDirty = false;
            _selectedWeaponLimit = limit;
            _selectedTombstoneLimit = tombstoneLimit;
        }

        // Selection follows the half-second scan; only the selected live transforms run every frame.
        foreach (Target target in SelectedTargets)
        {
            Vector3 position = target.Position;
            if (target.Kind == LocationKind.Weapon && LoadedDrops.TryGetValue(target.Key, out ItemDrop drop))
            {
                if (drop == null) { _selectionDirty = true; continue; }
                position = drop.transform.position;
            }
            else if (target.Kind == LocationKind.Tombstone && LoadedTombstones.TryGetValue(target.Key, out TombStone tombstone))
            {
                if (tombstone == null) { _selectionDirty = true; continue; }
                if (IsEmpty(tombstone)) { ForgetRecoveredTarget(target.Key); continue; }
                position = tombstone.transform.position;
            }
            targets.Add(new Target(target.Key, position, target.Icon, target.Kind, target.CreatedTicks));
        }
    }

    private static void SelectTargets(Vector3 origin, int limit, int tombstoneLimit)
    {
        SelectedTargets.Clear();
        NewestTombstones.Clear();
        if (limit > 0)
        foreach (KeyValuePair<string, ItemDrop> pair in LoadedDrops)
        {
            ItemDrop drop = pair.Value;
            if (drop == null) continue;
            AddSelected(SelectedTargets, new Target(pair.Key, drop.transform.position,
                GetIcon(drop.m_itemData, drop.m_itemData.m_variant)), origin, limit, false);
        }
        Sprite? tombstoneIcon = tombstoneLimit > 0 && Minimap.instance != null
            ? Minimap.instance.m_icons.Find(icon => icon.m_name == Minimap.PinType.Death).m_icon : null;
        if (tombstoneLimit > 0)
        foreach (KeyValuePair<string, TombStone> pair in LoadedTombstones)
        {
            TombStone tombstone = pair.Value;
            if (tombstone == null || PickedUpUntil.ContainsKey(pair.Key) || IsEmpty(tombstone)) continue;
            ZNetView? nview = tombstone.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) continue;
            long ticks = nview.GetZDO().GetLong(ZDOVars.s_timeOfDeath, 0L);
            if (ticks <= 0L || ticks > DateTime.MaxValue.Ticks) continue;
            AddSelected(NewestTombstones, new Target(pair.Key, tombstone.transform.position, tombstoneIcon,
                LocationKind.Tombstone, ticks), origin, tombstoneLimit, true);
        }
        foreach (SpearLocationRecord record in ServerRecords)
        {
            // A loaded drop's current transform supersedes its last server position.
            if (PickedUpUntil.ContainsKey(record.Key)) continue;
            if (record.Kind == LocationKind.Tombstone)
            {
                if (tombstoneLimit == 0 || LoadedTombstones.ContainsKey(record.Key)) continue;
                AddSelected(NewestTombstones, new Target(record.Key, record.Position, tombstoneIcon,
                    record.Kind, record.CreatedTicks), origin, tombstoneLimit, true);
            }
            else
            {
                if (limit == 0 || LoadedDrops.ContainsKey(record.Key)) continue;
                GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(record.PrefabHash) : null;
                AddSelected(SelectedTargets, new Target(record.Key, record.Position,
                    GetIcon(prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData : null, record.Variant)), origin, limit, false);
            }
        }
        SelectedTargets.AddRange(NewestTombstones);
    }

    private static void AddSelected(List<Target> targets, Target candidate, Vector3 origin, int limit, bool newest)
    {
        if (string.IsNullOrEmpty(candidate.Key)) return;
        for (int i = 0; i < targets.Count; ++i)
            if (targets[i].Key == candidate.Key) return;

        float distance = (candidate.Position - origin).sqrMagnitude;
        int index = 0;
        while (index < targets.Count)
        {
            int order = newest ? targets[index].CreatedTicks.CompareTo(candidate.CreatedTicks)
                : distance.CompareTo((targets[index].Position - origin).sqrMagnitude);
            if (order < 0 || (order == 0 && string.CompareOrdinal(candidate.Key, targets[index].Key) < 0)) break;
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
