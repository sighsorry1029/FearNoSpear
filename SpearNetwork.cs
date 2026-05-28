using System;
using System.Collections.Generic;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearNetwork
{
    private const string RequestRpcName = FearNoSpearPlugin.ModName + "_SpearLocationRequest";
    private const string ResponseRpcName = FearNoSpearPlugin.ModName + "_SpearLocationResponse";

    internal static void ClearSession()
    {
        SpearOwnership.ClearPeerMappings();
        SpearServerRegistry.Clear();
    }

    internal static void RegisterRpcs()
    {
        ZRoutedRpc? rpc = ZRoutedRpc.instance;
        if (rpc == null) return;

        TryRegister(rpc, RequestRpcName, new Action<long, ZPackage>(RPC_RequestSpearLocation));
        TryRegister(rpc, ResponseRpcName, new Action<long, ZPackage>(RPC_SpearLocationResponse));
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
        package.Write(localPlayer.transform.position);
        ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, RequestRpcName, package);
        return true;
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

    private static void RPC_RequestSpearLocation(long senderPeerId, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

        long requestedPlayerId = 0L;
        int requestedMaxPins = FearNoSpearPlugin.Cfg.GetMaxPinsPerCommand();
        Vector3 requestPosition = Vector3.zero;
        try
        {
            if (!SpearLocatorProtocol.TryReadHeader(package, RequestRpcName))
            {
                SendSpearLocationResponse(senderPeerId, new List<SpearLocationRecord>());
                return;
            }

            requestedPlayerId = package.ReadLong();
            requestedMaxPins = package.ReadInt();
            requestPosition = package.ReadVector3();
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
        List<SpearLocationRecord> records = SpearServerRegistry.SelectBest(playerId, maxPins, requestPosition);
        SendSpearLocationResponse(senderPeerId, records);
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

}
