# Known risks and design constraints

## Duplicate items

The main risk is duplicate spear creation in multiplayer. The mod mitigates this with:

- owner-only rescue,
- a fixed 2-second freshness limit on last-confirmed ownership for invalid-view rescue,
- a ZDO rescue claim flag,
- one rescue attempt per local tracker.

The mod does not delete nearby equivalent drops as duplicate cleanup because item-data equivalence is not a unique throw identity.

Invalid-view rescue is always enabled for a sufficiently recent last-confirmed owner. This prioritizes loss prevention; if the view is invalid, the shared ZDO claim may be unavailable. The freshness limit does not guarantee that simultaneous rescues cannot occur. Rescue timing is fixed at 1 second before TTL expiry, retaining the physics-step margin; there are no rescue config switches. A reproducible duplicate issue requires investigating ownership and spawn paths rather than changing config.

These must be tested on a dedicated server.

## False positives

The mod should avoid rescuing arrows, bolts, enemy projectiles, harpoons, or modded recoverable projectiles unless intentionally configured. Detection currently requires recoverable item behavior plus spear-like item metadata/name.

## Rescue spawn contract

Rescue invokes the same private `SpawnOnHit(GameObject, Collider, Vector3)` overload targeted by the drop transpiler, with null hit object/collider and the stored-velocity normal. Ground-hit-only projectiles skip that path because null terrain would make it a no-op. Only the exact drop recorded by the wrapper confirms success; otherwise the public `ItemDrop.DropItem(ItemData, int, Vector3, Quaternion)` fallback runs. Both call paths contain spawn exceptions, and failed rescue still releases its claim. Client and dedicated-server 1.0.16 originals have these contracts; this inspection and offline checks do not verify Harmony execution or multiplayer duplication behavior.

## OnDestroy timing

If rescue happens during `OnDestroy`, the projectile's `ZNetView` or ZDO may already be invalid. The tracker therefore stores last-known owner and last-known position/TTL. Prefer `ZNetScene.Destroy` prefix or TTL rescue over OnDestroy fallback when possible.

## Item placement

Spawning the item at the projectile position may create an item far from the player, inside terrain, or in an unloaded area. That is still preferable to deletion, but tests should verify item recovery. A future diagnostic-only mode could spawn near the thrower if needed.

## Mod compatibility

RenderLimits and SkadiNet may patch nearby systems. Keep this mod narrow:

- keep rescue behavior limited to `Projectile` and `ZNetScene.Destroy(GameObject)`,
- use postfix/prefix conservatively,
- avoid changing global zone or ZDO scheduling behavior.
