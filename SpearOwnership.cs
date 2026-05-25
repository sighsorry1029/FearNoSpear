using System.Collections.Generic;

namespace FearNoSpear;

internal static class SpearOwnership
{
    private static readonly Dictionary<long, long> PeerPlayerIds = new();

    internal static void ClearPeerMappings()
    {
        PeerPlayerIds.Clear();
    }

    internal static bool IsLocalPlayerProjectile(Projectile projectile, ItemDrop.ItemData spawnItem, Player localPlayer)
    {
        Character owner = projectile.m_owner;
        if (owner != null)
        {
            if (owner == localPlayer) return true;

            Player? ownerPlayer = owner as Player;
            if (ownerPlayer != null)
            {
                return ownerPlayer.GetPlayerID() == localPlayer.GetPlayerID();
            }

            return false;
        }

        return SpearItemIdentity.BelongsToPlayer(spawnItem, localPlayer);
    }

    internal static bool TryResolveProjectileOwnerPlayerId(Projectile projectile, out long playerId)
    {
        playerId = 0L;

        Character owner = projectile.m_owner;
        Player? ownerPlayer = owner as Player;
        if (ownerPlayer == null) return false;

        playerId = ownerPlayer.GetPlayerID();
        return playerId != 0L;
    }

    internal static bool TryResolveReportedPeerPlayerId(long senderPeerId, long reportedPlayerId, out long playerId)
    {
        return TryResolveAuthoritativePlayerId(senderPeerId, reportedPlayerId, out playerId);
    }

    internal static bool TryResolveRequestedPeerPlayerId(long senderPeerId, long requestedPlayerId, out long playerId)
    {
        return TryResolveAuthoritativePlayerId(senderPeerId, requestedPlayerId, out playerId) ||
               TryResolveMappedPlayerId(senderPeerId, out playerId);
    }

    private static bool TryResolveMappedPlayerId(long senderPeerId, out long playerId)
    {
        if (PeerPlayerIds.TryGetValue(senderPeerId, out long knownPlayerId) && knownPlayerId != 0L)
        {
            playerId = knownPlayerId;
            return true;
        }

        playerId = 0L;
        return false;
    }

    private static bool TryResolveAuthoritativePlayerId(long senderPeerId, long reportedPlayerId, out long playerId)
    {
        playerId = 0L;
        if (senderPeerId == 0L) return false;

        if (TryResolvePeerCharacterPlayerId(senderPeerId, reportedPlayerId, out playerId))
        {
            return true;
        }

        foreach (Player player in Player.GetAllPlayers())
        {
            if (player == null) continue;

            ZNetView? nview = player.m_nview;
            if (nview == null || !nview.IsValid()) continue;

            ZDO zdo = nview.GetZDO();
            if (zdo == null || !zdo.IsValid()) continue;
            if (zdo.GetOwner() != senderPeerId) continue;

            long resolvedPlayerId = player.GetPlayerID();
            if (resolvedPlayerId == 0L) continue;

            if (reportedPlayerId != 0L && reportedPlayerId != resolvedPlayerId && FearNoSpearConfig.Verbose)
            {
                FearNoSpearPlugin.Log.LogDebug($"Ignoring mismatched client player id: peer={senderPeerId}; reported={reportedPlayerId}; resolved={resolvedPlayerId}");
            }

            playerId = resolvedPlayerId;
            PeerPlayerIds[senderPeerId] = playerId;
            return true;
        }

        return false;
    }

    private static bool TryResolvePeerCharacterPlayerId(long senderPeerId, long reportedPlayerId, out long playerId)
    {
        playerId = 0L;

        ZNetPeer? peer = ZNet.instance != null ? ZNet.instance.GetPeer(senderPeerId) : null;
        if (peer == null || peer.m_characterID.IsNone()) return false;

        ZDO? zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
        if (zdo == null || !zdo.IsValid()) return false;

        long resolvedPlayerId = zdo.GetLong(ZDOVars.s_playerID, 0L);
        if (resolvedPlayerId == 0L) return false;

        if (reportedPlayerId != 0L && reportedPlayerId != resolvedPlayerId && FearNoSpearConfig.Verbose)
        {
            FearNoSpearPlugin.Log.LogDebug($"Ignoring mismatched client player id from peer character: peer={senderPeerId}; reported={reportedPlayerId}; resolved={resolvedPlayerId}");
        }

        playerId = resolvedPlayerId;
        PeerPlayerIds[senderPeerId] = playerId;
        return true;
    }
}
