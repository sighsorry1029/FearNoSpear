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

Detection requires recoverable item behavior plus spear-like item metadata/name or SecondaryAttacks' copied-throw marker. The marker covers copied throws, not only ImpactBurst. Suppressed SpearRain followups and boomerangs already returned to inventory must never become rescue candidates. Ordinary arrows, bolts, inventory drops, and unrelated projectiles remain outside this integration.

## Indicators and world time

Weapon drops use thrower tags and exact ZDO identities regardless of weapon skill type. Tombstones use their native owner ID and creation time. The client merges live transforms with periodically refreshed server records; remote recovery or destruction can remain visible until the next response. Unloading a tombstone is not recovery and must not permanently suppress it.

Tombstone age uses the current world day length and world clock. Sleeping, time commands, or a changed day length affect the displayed age; it is not real elapsed time. No UTC timestamps or extra death history are persisted. Missing native creation times are skipped rather than invented.

Protocol 7 adds target types, tombstone timestamps, and independent query flags. It explicitly rejects earlier payloads even if a development build shares the same mod version. Install the same development DLL on server and clients. Automated geometry tests do not establish rendering performance or multiplayer behavior.

## Indicator performance

Render objects are pooled up to five weapons plus five tombstones. Each beam uses two line positions, a shared material, and no lights, shadows, physics, or forced zone loading. Weapon/tombstone colors change only when a pooled beam changes target kind. This bounds visual object count, not all lookup work or GPU cost.

Server candidate scans are shared across clients with a five-second cache per requested category. Candidates are grouped by player ID, so requests inspect only the requester's bucket and revalidate live ownership, existence, position, and prefab. Each client normally requests locations every five seconds. A newly created distant target may take about ten seconds to be discovered when cache refresh and request timing do not align; network timeouts can add delay. No new remote requests or scans are added for count changes.

In the original Valheim 1.0.16 code, `GetAllZDOIDsWithHash(Type.Long, ...)` still traverses all stored long fields; it is not a direct hash-to-object index. The patch reduces scan frequency and repeated cross-player filtering, but does not eliminate full-scan spikes. Large worlds and many players still need server main-thread profiling.

The client scans loaded drops every half second and invalidates its selected-target cache. Server responses, expiry, recovery, and session resets also invalidate selection; count/style changes are checked on the next visible frame. Only up to ten selected live targets update their transforms every frame, retaining smooth bounce/float tracking. Prefab/icon lookups and full candidate selection no longer run every frame. A newly nearer weapon can take up to half a second to replace the selected one. Distance/age strings and transparent beam overdraw still require in-game profiling; offline checks cannot establish FPS or allocation budgets.

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
