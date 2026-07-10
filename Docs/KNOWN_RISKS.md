# Known risks and design constraints

## Duplicate items

The main risk is duplicate spear creation in multiplayer. The mod mitigates this with:

- owner-only rescue,
- `AllowLastKnownOwnerIfZNetViewInvalid`,
- a ZDO rescue claim flag,
- one rescue attempt per local tracker.

The mod does not delete nearby equivalent drops as duplicate cleanup because item-data equivalence is not a unique throw identity.

These must be tested on a dedicated server.

## False positives

The mod should avoid rescuing arrows, bolts, enemy projectiles, harpoons, or modded recoverable projectiles unless intentionally configured. Detection currently requires recoverable item behavior plus spear-like item metadata/name.

## Null SpawnOnHit parameters

The draft builds `SpawnOnHit` arguments dynamically and passes null/default values except for a `Vector3` normal. Codex should confirm this matches the current Valheim method behavior.

## OnDestroy timing

If rescue happens during `OnDestroy`, the projectile's `ZNetView` or ZDO may already be invalid. The tracker therefore stores last-known owner and last-known position/TTL. Prefer `ZNetScene.Destroy` prefix or TTL rescue over OnDestroy fallback when possible.

## Item placement

Spawning the item at the projectile position may create an item far from the player, inside terrain, or in an unloaded area. That is still preferable to deletion, but tests should verify item recovery. A future diagnostic-only mode could spawn near the thrower if needed.

## Mod compatibility

RenderLimits and SkadiNet may patch nearby systems. Keep this mod narrow:

- keep rescue behavior limited to `Projectile` and `ZNetScene.Destroy(GameObject)`,
- use postfix/prefix conservatively,
- avoid changing global zone or ZDO scheduling behavior.
