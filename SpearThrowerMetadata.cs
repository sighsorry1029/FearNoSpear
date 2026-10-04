using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearThrowerMetadata
{
    internal const string ThrowerPlayerIdKey = "FearNoSpear.ThrowerPlayerID";
    internal static readonly int ThrowerPlayerIdHash = ThrowerPlayerIdKey.GetStableHashCode();

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

        Character? owner = ReflectionCache.GetProjectileOwner(projectile);
        Player? ownerPlayer = owner as Player;
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

[HarmonyPatch(typeof(Projectile), "SpawnOnHit", typeof(GameObject), typeof(Collider), typeof(Vector3))]
internal static class ProjectileSpawnItemPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        var dropMethod = AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.DropItem),
            new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(Vector3), typeof(Quaternion) });
        if (code.Count(instruction => instruction.Calls(dropMethod)) != 1)
            throw new InvalidOperationException("FearNoSpear: expected one ItemDrop.DropItem call in Projectile.SpawnOnHit.");

        foreach (CodeInstruction instruction in code)
        {
            if (!instruction.Calls(dropMethod))
            {
                yield return instruction;
                continue;
            }

            yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ProjectileSpawnItemPatch), nameof(DropThrownItem)));
        }
    }

    private static ItemDrop DropThrownItem(ItemDrop.ItemData item, int amount, Vector3 position, Quaternion rotation, Projectile projectile)
    {
        // Resolve the tracker before spawning, then tag only the actual returned drop.
        SpearSafetyTracker? tracker = SpearSafetyTracker.GetOrArmIfTracked(projectile);
        ItemDrop drop = ItemDrop.DropItem(item, amount, position, rotation);
        if (tracker != null && drop != null)
        {
            try { tracker.RecordSpawnedDrop(drop); }
            catch (Exception ex) { FearNoSpearPlugin.Log.LogWarning($"Could not tag thrown spear: {ex.Message}"); }
        }
        return drop!;
    }
}

[HarmonyPatch(typeof(Player), "AutoPickup")]
internal static class SpearAutoPickupPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        var autoPickupField = typeof(ItemDrop).GetField(nameof(ItemDrop.m_autoPickup));
        if (code.Count(instruction => instruction.LoadsField(autoPickupField)) != 1)
            throw new InvalidOperationException("FearNoSpear: expected one ItemDrop.m_autoPickup check in Player.AutoPickup.");
        var filterMethod = typeof(SpearAutoPickupPatch).GetMethod(nameof(CanAutoPickup), BindingFlags.Static | BindingFlags.NonPublic);

        foreach (CodeInstruction instruction in code)
        {
            if (!instruction.LoadsField(autoPickupField))
            {
                yield return instruction;
                continue;
            }

            // Keep the native field read so other auto-pickup filters can compose in either order.
            yield return new CodeInstruction(OpCodes.Dup).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
            yield return instruction;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, filterMethod);
        }
    }

    internal static bool CanAutoPickup(ItemDrop drop, bool allowed, Player player)
    {
        if (!allowed) return false;
        long throwerId = SpearThrowerMetadata.ReadFromDrop(drop);
        return throwerId == 0L || throwerId == player.GetPlayerID();
    }
}
