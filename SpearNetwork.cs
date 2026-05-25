using System;
using System.Collections.Generic;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearNetwork
{
    private const string ReportRpcName = FearNoSpearPlugin.ModName + "_SpearLocationReport";
    private const string RequestRpcName = FearNoSpearPlugin.ModName + "_SpearLocationRequest";
    private const string ResponseRpcName = FearNoSpearPlugin.ModName + "_SpearLocationResponse";
    private const string PickupRpcName = FearNoSpearPlugin.ModName + "_SpearLocationPickup";
    private const float ReportIntervalSeconds = 1f;
    private const float ReportMoveDistance = 32f;

    private static readonly Dictionary<string, LastReportState> LastReports = new();

    internal static void ClearSession()
    {
        LastReports.Clear();
        SpearOwnership.ClearPeerMappings();
        SpearServerRegistry.Clear();
    }

    internal static void RegisterRpcs()
    {
        ZRoutedRpc? rpc = ZRoutedRpc.instance;
        if (rpc == null) return;

        TryRegister(rpc, ReportRpcName, new Action<long, ZPackage>(RPC_ReportSpearLocation));
        TryRegister(rpc, RequestRpcName, new Action<long, ZPackage>(RPC_RequestSpearLocation));
        TryRegister(rpc, ResponseRpcName, new Action<long, ZPackage>(RPC_SpearLocationResponse));
        TryRegister(rpc, PickupRpcName, new Action<long, ZPackage>(RPC_SpearPickedUp));
    }

    internal static void ReportSpearLocation(string key, Vector3 position, string source, bool fromTrackedProjectile, bool immediate)
    {
        ReportSpearLocation(key, key, position, source, fromTrackedProjectile, immediate);
    }

    internal static void ReportSpearLocation(string key, string itemKey, Vector3 position, string source, bool fromTrackedProjectile, bool immediate)
    {
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer == null) return;

        long playerId = localPlayer.GetPlayerID();
        if (playerId == 0L) return;
        if (!ShouldReport(key, position, immediate)) return;
        if (!TryGetServerPeerId(out long serverPeerId)) return;

        ZPackage package = new();
        SpearLocatorProtocol.WriteHeader(package);
        package.Write(playerId);
        package.Write(key);
        package.Write(itemKey);
        package.Write(position);
        package.Write(source);
        package.Write(fromTrackedProjectile);

        ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, ReportRpcName, package);
    }

    internal static bool RequestServerSpearLocation(Player localPlayer)
    {
        if (localPlayer == null) return false;
        if (!TryGetServerPeerId(out long serverPeerId)) return false;

        long playerId = localPlayer.GetPlayerID();
        if (playerId == 0L) return false;

        ZPackage package = new();
        SpearLocatorProtocol.WriteHeader(package);
        package.Write(playerId);
        package.Write(FearNoSpearPlugin.Cfg.GetMaxPinsPerCommand());
        ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, RequestRpcName, package);
        return true;
    }

    internal static void ReportSpearPickup(string itemKey, Vector3 position)
    {
        if (string.IsNullOrEmpty(itemKey)) return;

        Player localPlayer = Player.m_localPlayer;
        if (localPlayer == null) return;

        long playerId = localPlayer.GetPlayerID();
        if (playerId == 0L) return;
        if (!TryGetServerPeerId(out long serverPeerId)) return;

        ZPackage package = new();
        SpearLocatorProtocol.WriteHeader(package);
        package.Write(playerId);
        package.Write(itemKey);
        package.Write(position);
        ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, PickupRpcName, package);
    }

    internal static void RecordServerKnownSpear(long playerId, string key, string itemKey, Vector3 position, string source, bool fromTrackedProjectile)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        SpearServerRegistry.Record(playerId, key, itemKey, position, source, fromTrackedProjectile);
    }

    private static void TryRegister(ZRoutedRpc rpc, string name, Action<long, ZPackage> handler)
    {
        try
        {
            rpc.Register(name, handler);
        }
        catch (ArgumentException)
        {
            // The same routed RPC instance can be touched by multiple lifecycle patches.
        }
    }

    private static bool TryGetServerPeerId(out long serverPeerId)
    {
        serverPeerId = 0L;
        if (ZNet.instance == null || ZRoutedRpc.instance == null) return false;

        serverPeerId = ZRoutedRpc.instance.GetServerPeerID();
        if (serverPeerId == 0L && !ZNet.instance.IsServer()) return false;
        return true;
    }

    private static bool ShouldReport(string key, Vector3 position, bool immediate)
    {
        if (immediate)
        {
            LastReports[key] = new LastReportState(position, Time.time);
            return true;
        }

        if (!LastReports.TryGetValue(key, out LastReportState state))
        {
            LastReports[key] = new LastReportState(position, Time.time);
            return true;
        }

        float age = Time.time - state.LastTime;
        float distanceSqr = Vector3.SqrMagnitude(position - state.Position);
        if (age < ReportIntervalSeconds && distanceSqr < ReportMoveDistance * ReportMoveDistance) return false;

        state.Position = position;
        state.LastTime = Time.time;
        LastReports[key] = state;
        return true;
    }

    private static void RPC_ReportSpearLocation(long senderPeerId, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

        try
        {
            if (!SpearLocatorProtocol.TryReadHeader(package, ReportRpcName)) return;
            long playerId = package.ReadLong();
            string key = package.ReadString();
            string itemKey = package.ReadString();
            Vector3 position = package.ReadVector3();
            string source = package.ReadString();
            bool fromTrackedProjectile = package.ReadBool();

            if (string.IsNullOrEmpty(key)) return;
            if (!SpearOwnership.TryResolveReportedPeerPlayerId(senderPeerId, playerId, out long mappedPlayerId))
            {
                if (FearNoSpearConfig.Verbose)
                {
                    FearNoSpearPlugin.Log.LogDebug($"Ignoring spear location report from unmapped peer={senderPeerId}; reportedPlayerId={playerId}");
                }

                return;
            }

            SpearServerRegistry.Record(mappedPlayerId, key, itemKey, position, source, fromTrackedProjectile);
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"Failed to read spear location report RPC: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RPC_RequestSpearLocation(long senderPeerId, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

        long requestedPlayerId = 0L;
        int requestedMaxPins = FearNoSpearPlugin.Cfg.GetMaxPinsPerCommand();
        try
        {
            if (!SpearLocatorProtocol.TryReadHeader(package, RequestRpcName))
            {
                SendSpearLocationResponse(senderPeerId, new List<SpearLocationRecord>());
                return;
            }

            requestedPlayerId = package.ReadLong();
            requestedMaxPins = package.ReadInt();
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"Failed to read spear location request RPC: {ex.GetType().Name}: {ex.Message}");
            SendSpearLocationResponse(senderPeerId, new List<SpearLocationRecord>());
            return;
        }

        if (!SpearOwnership.TryResolveRequestedPeerPlayerId(senderPeerId, requestedPlayerId, out long playerId))
        {
            if (FearNoSpearConfig.Verbose)
            {
                FearNoSpearPlugin.Log.LogDebug($"Ignoring spear location request from unmapped peer={senderPeerId}; requestedPlayerId={requestedPlayerId}");
            }

            SendSpearLocationResponse(senderPeerId, new List<SpearLocationRecord>());
            return;
        }

        int maxPins = Mathf.Clamp(Mathf.Min(requestedMaxPins, FearNoSpearPlugin.Cfg.GetMaxPinsPerCommand()), 1, 20);
        List<SpearLocationRecord> records = SpearServerRegistry.SelectBest(playerId, maxPins);
        SendSpearLocationResponse(senderPeerId, records);
    }

    private static void RPC_SpearPickedUp(long senderPeerId, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

        try
        {
            if (!SpearLocatorProtocol.TryReadHeader(package, PickupRpcName)) return;
            long requestedPlayerId = package.ReadLong();
            string itemKey = package.ReadString();
            Vector3 position = package.ReadVector3();

            if (string.IsNullOrEmpty(itemKey)) return;
            if (!SpearOwnership.TryResolveRequestedPeerPlayerId(senderPeerId, requestedPlayerId, out long playerId))
            {
                if (FearNoSpearConfig.Verbose)
                {
                    FearNoSpearPlugin.Log.LogDebug($"Ignoring spear pickup report from unmapped peer={senderPeerId}; requestedPlayerId={requestedPlayerId}");
                }

                return;
            }

            SpearServerRegistry.RemovePickedUp(playerId, itemKey, position);
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"Failed to read spear pickup RPC: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RPC_SpearLocationResponse(long senderPeerId, ZPackage package)
    {
        try
        {
            if (!SpearLocatorProtocol.TryReadHeader(package, ResponseRpcName))
            {
                SpearLocator.PinLocalAfterEmptyServer();
                return;
            }

            int count = package.ReadInt();
            if (count <= 0)
            {
                SpearLocator.PinLocalAfterEmptyServer();
                return;
            }

            List<SpearLocationRecord> records = new();
            for (int i = 0; i < count; ++i)
            {
                records.Add(SpearLocatorProtocol.ReadRecord(package));
            }

            SpearLocator.PinServerSpears(records);
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"Failed to read spear location response RPC: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SendSpearLocationResponse(long targetPeerId, List<SpearLocationRecord> records)
    {
        if (ZRoutedRpc.instance == null) return;

        ZPackage package = new();
        SpearLocatorProtocol.WriteHeader(package);
        package.Write(records.Count);

        foreach (SpearLocationRecord record in records)
        {
            SpearLocatorProtocol.WriteRecord(package, record);
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(targetPeerId, ResponseRpcName, package);
    }

    private struct LastReportState
    {
        internal Vector3 Position;
        internal float LastTime;

        internal LastReportState(Vector3 position, float lastTime)
        {
            Position = position;
            LastTime = lastTime;
        }
    }
}
