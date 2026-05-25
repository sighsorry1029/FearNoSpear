# Valheim projectile context for thrown spear loss

## Observed symptom

The user reports that thrown spears sometimes disappear completely, mainly when thrown from high mountains or over very long distances. The spear cannot be found with commands such as `find`, which suggests that the dropped item itself was never created.

## Working lifecycle model

Based on Valheim publicized assembly inspection and known modding patterns, thrown recoverable weapons likely follow this lifecycle:

1. The player throws a spear.
2. The game creates a `Projectile` GameObject/ZDO.
3. The projectile stores the original item data in a field such as `Projectile.m_spawnItem` when `Projectile.m_respawnItemOnHit` is true.
4. While in flight, projectile movement and collision checks occur during `Projectile.FixedUpdate`.
5. The projectile hit path calls `Projectile.OnHit` and then an internal spawn path, likely `Projectile.SpawnOnHit(...)`.
6. The spawn path recreates the original weapon as an `ItemDrop` in the world.
7. The projectile is then destroyed.

The important detail is that the spear item does not exist as a dropped item while the spear is flying. The recoverable item is only recreated after the hit path.

## Failure path to mitigate

If the projectile is removed before `OnHit` / `SpawnOnHit`, the original item data can be lost with the projectile. Possible removal paths include:

- TTL expiry before a terrain or object hit occurs.
- Scene/zone unload before the projectile hits terrain.
- ZDO destruction after leaving loaded/current scene sectors.
- Ownership handoff/desync causing projectile simulation to stop before collision.

This package focuses on preventing loss at the last moment: if the projectile is about to expire or be destroyed while still holding a recoverable spear item, force the normal item-spawn path first.

## Valheim fields/methods to confirm

Codex/developer should confirm the exact field names against the user's build:

- `Projectile.m_ttl`: remaining lifetime.
- `Projectile.m_vel`: projectile velocity.
- `Projectile.m_nview`: `ZNetView` for ownership/ZDO validity.
- `Projectile.m_didHit`: whether normal hit processing already occurred.
- `Projectile.m_weapon`: weapon item metadata.
- `Projectile.m_spawnItem`: original item to respawn on hit.
- `Projectile.m_respawnItemOnHit`: whether recoverable item behavior is enabled.
- `Projectile.SpawnOnHit(...)`: internal item/effects spawn method.
- `ZNetScene.Destroy(GameObject)`: common network object destroy path.

If field names differ, update only `ReflectionCache` in the source.

## Why not just increase TTL?

TTL extension can help long mountain throws, but it does not solve scene unload or ownership problems. A projectile with longer TTL can still be culled, removed, or stop simulating before it hits. Therefore the draft has two layers:

1. extend TTL to reduce avoidable expiry,
2. rescue on expiry or unexpected destroy.

## Why not make projectile ZDO persistent?

Marking projectiles persistent could prevent some loss cases, but it risks leaving orphaned moving projectiles or stale ZDOs on the server. This mod should not globally change projectile persistence. The safer default is to spawn the original item before the projectile is lost.
