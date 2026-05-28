using UnityEngine;

namespace FearNoSpear;

internal static class SpearThrowerMetadata
{
    internal const string ThrowerPlayerIdKey = "FearNoSpear.ThrowerPlayerID";

    internal static bool TryWriteToProjectile(Projectile projectile, out long playerId)
    {
        playerId = 0L;
        if (projectile == null) return false;
        if (!TryResolveThrowerPlayerId(projectile, out playerId)) return false;

        ZNetView? nview = ReflectionCache.GetNView(projectile);
        return TryWriteToView(nview, playerId);
    }

    internal static bool TryWriteToDrop(ItemDrop drop, long playerId)
    {
        if (drop == null || playerId == 0L) return false;

        ZNetView? nview = drop.GetComponent<ZNetView>();
        return TryWriteToView(nview, playerId);
    }

    internal static long ReadFromDrop(ItemDrop drop)
    {
        if (drop == null) return 0L;

        ZNetView? nview = drop.GetComponent<ZNetView>();
        return ReadFromView(nview);
    }

    internal static long ReadFromProjectile(Projectile projectile)
    {
        if (projectile == null) return 0L;

        ZNetView? nview = ReflectionCache.GetNView(projectile);
        return ReadFromView(nview);
    }

    internal static long ReadFromZdo(ZDO? zdo)
    {
        return zdo != null && zdo.IsValid()
            ? zdo.GetLong(ThrowerPlayerIdKey, 0L)
            : 0L;
    }

    internal static bool TryResolveThrowerPlayerId(Projectile projectile, out long playerId)
    {
        playerId = 0L;
        if (projectile == null) return false;

        Character owner = projectile.m_owner;
        Player? ownerPlayer = owner as Player;
        if (ownerPlayer == null && owner != null && owner == Player.m_localPlayer)
        {
            ownerPlayer = Player.m_localPlayer;
        }

        if (ownerPlayer == null) return false;

        playerId = ownerPlayer.GetPlayerID();
        return playerId != 0L;
    }

    private static long ReadFromView(ZNetView? nview)
    {
        if (nview == null || !nview.IsValid()) return 0L;

        ZDO zdo = nview.GetZDO();
        return ReadFromZdo(zdo);
    }

    private static bool TryWriteToView(ZNetView? nview, long playerId)
    {
        if (nview == null || !nview.IsValid() || playerId == 0L) return false;
        if (!CanWrite(nview)) return false;

        ZDO zdo = nview.GetZDO();
        if (zdo == null || !zdo.IsValid()) return false;

        long current = zdo.GetLong(ThrowerPlayerIdKey, 0L);
        if (current == playerId) return true;

        zdo.Set(ThrowerPlayerIdKey, playerId);
        return true;
    }

    private static bool CanWrite(ZNetView nview)
    {
        return (ZNet.instance != null && ZNet.instance.IsServer()) || nview.IsOwner();
    }
}
