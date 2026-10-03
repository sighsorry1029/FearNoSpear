using System;
using System.Collections.Generic;
using UnityEngine;

namespace FearNoSpear;

internal sealed class SpearLocationRecord
{
    internal string Key = string.Empty;
    internal Vector3 Position;
    internal int PrefabHash;
    internal int Variant;
}

internal static class SpearNetwork
{
    private const int ProtocolVersion = 6;
    private const string RequestRpcName = FearNoSpearPlugin.ModName + "_SpearLocationRequest";
    private const string ResponseRpcName = FearNoSpearPlugin.ModName + "_SpearLocationResponse";

    private static ZRoutedRpc? _registeredRpc;
    private static bool _warnedProtocolMismatch;

    internal static void ClearSession()
    {
        SpearServerRegistry.Clear();
        _warnedProtocolMismatch = false;
    }

    internal static void RegisterRpcs()
    {
        ZRoutedRpc? rpc = ZRoutedRpc.instance;
        if (rpc == null || ReferenceEquals(_registeredRpc, rpc)) return;

        rpc.Register(RequestRpcName, new Action<long, ZPackage>(RPC_RequestSpearLocation));
        rpc.Register(ResponseRpcName, new Action<long, ZPackage>(RPC_SpearLocationResponse));
        _registeredRpc = rpc;
    }

    internal static bool RequestServerSpearLocation(Player localPlayer, int requestId)
    {
        if (localPlayer == null) return false;
        if (!TryGetServerPeerId(out long serverPeerId)) return false;

        ZPackage package = new();
        WriteHeader(package);
        package.Write(requestId);
        package.Write(localPlayer.transform.position);
        ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, RequestRpcName, package);
        return true;
    }

    private static bool TryGetServerPeerId(out long serverPeerId)
    {
        serverPeerId = 0L;
        if (ZNet.instance == null || ZRoutedRpc.instance == null) return false;

        if (ZNet.instance.IsServer())
        {
            serverPeerId = ZNet.GetUID();
            return true;
        }

        ZNetPeer? serverPeer = ZNet.instance.GetServerPeer();
        if (serverPeer == null) return false;

        serverPeerId = serverPeer.m_uid;
        return serverPeerId != 0L;
    }

    private static void RPC_RequestSpearLocation(long senderPeerId, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

        Vector3 requestPosition;
        int requestId;
        try
        {
            if (!TryReadHeader(package, RequestRpcName))
            {
                return;
            }

            requestId = package.ReadInt();
            requestPosition = package.ReadVector3();
            if (!IsFinite(requestPosition)) return;
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"Failed to read spear location request RPC: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!TryResolvePeerPlayerId(senderPeerId, out long playerId))
        {
            SendSpearLocationResponse(senderPeerId, requestId, new List<SpearLocationRecord>());
            return;
        }

        List<SpearLocationRecord> records = SpearServerRegistry.SelectBest(playerId, requestPosition);
        SendSpearLocationResponse(senderPeerId, requestId, records);
    }

    private static void RPC_SpearLocationResponse(long senderPeerId, ZPackage package)
    {
        if (!TryGetServerPeerId(out long serverPeerId) || senderPeerId != serverPeerId)
        {
            FearNoSpearPlugin.Log.LogWarning($"Ignored spear location response from non-server peer {senderPeerId}.");
            return;
        }

        try
        {
            if (!TryReadHeader(package, ResponseRpcName))
            {
                return;
            }

            int requestId = package.ReadInt();
            int count = package.ReadInt();
            if (count < 0 || count > FearNoSpearConfig.MaxLocationResults)
            {
                FearNoSpearPlugin.Log.LogWarning($"Ignored spear location response with invalid record count {count}.");
                return;
            }

            List<SpearLocationRecord> records = new(count);
            for (int i = 0; i < count; ++i)
            {
                SpearLocationRecord record = ReadRecord(package);
                if (string.IsNullOrEmpty(record.Key) || !IsFinite(record.Position)) return;
                records.Add(record);
            }

            SpearLocator.ReceiveServerRecords(requestId, records);
        }
        catch (Exception ex)
        {
            FearNoSpearPlugin.Log.LogWarning($"Failed to read spear location response RPC: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SendSpearLocationResponse(long targetPeerId, int requestId, List<SpearLocationRecord> records)
    {
        if (ZRoutedRpc.instance == null) return;

        int count = Mathf.Min(records.Count, FearNoSpearConfig.MaxLocationResults);
        ZPackage package = new();
        WriteHeader(package);
        package.Write(requestId);
        package.Write(count);

        for (int i = 0; i < count; ++i)
        {
            WriteRecord(package, records[i]);
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(targetPeerId, ResponseRpcName, package);
    }

    private static bool TryResolvePeerPlayerId(long senderPeerId, out long playerId)
    {
        playerId = 0L;

        if (ZNet.instance != null && ZNet.instance.IsServer() && senderPeerId == ZNet.GetUID())
        {
            Player? localPlayer = Player.m_localPlayer;
            if (localPlayer != null)
            {
                playerId = localPlayer.GetPlayerID();
                if (playerId != 0L) return true;
            }
        }

        if (senderPeerId == 0L) return false;

        ZNetPeer? peer = ZNet.instance != null ? ZNet.instance.GetPeer(senderPeerId) : null;
        if (peer != null && !peer.m_characterID.IsNone())
        {
            ZDO? characterZdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
            if (characterZdo != null && characterZdo.IsValid())
            {
                playerId = characterZdo.GetLong(ZDOVars.s_playerID, 0L);
                if (playerId != 0L) return true;
            }
        }

        foreach (Player player in Player.GetAllPlayers())
        {
            if (player == null) continue;

            ZNetView? nview = ReflectionCache.GetNView(player);
            if (nview == null || !nview.IsValid()) continue;

            ZDO zdo = nview.GetZDO();
            if (zdo == null || !zdo.IsValid() || zdo.GetOwner() != senderPeerId) continue;

            playerId = player.GetPlayerID();
            if (playerId != 0L) return true;
        }

        playerId = 0L;
        return false;
    }

    private static void WriteHeader(ZPackage package)
    {
        package.Write(ProtocolVersion);
    }

    private static bool TryReadHeader(ZPackage package, string rpcName)
    {
        int protocol = package.ReadInt();
        if (protocol == ProtocolVersion) return true;

        if (!_warnedProtocolMismatch)
        {
            _warnedProtocolMismatch = true;
            FearNoSpearPlugin.Log.LogWarning($"Ignoring incompatible {rpcName} payload: protocol={protocol}; expected={ProtocolVersion}. Make sure server and client use the same FearNoSpear build.");
        }
        return false;
    }

    private static bool IsFinite(Vector3 position)
    {
        return !float.IsNaN(position.sqrMagnitude) && !float.IsInfinity(position.sqrMagnitude);
    }

    private static void WriteRecord(ZPackage package, SpearLocationRecord record)
    {
        package.Write(record.Key);
        package.Write(record.Position);
        package.Write(record.PrefabHash);
        package.Write(record.Variant);
    }

    private static SpearLocationRecord ReadRecord(ZPackage package)
    {
        return new SpearLocationRecord
        {
            Key = package.ReadString(),
            Position = package.ReadVector3(),
            PrefabHash = package.ReadInt(),
            Variant = package.ReadInt()
        };
    }
}
