# RenderLimits and SkadiNet interaction review

## RenderLimits

RenderLimits changes how far away zones are rendered, loaded, generated, and/or treated as active. The user's symptom happens when a spear travels far from the thrower, especially from a high mountain. That is exactly the situation where a projectile can cross from a loaded/current sector into an unloaded or not-yet-generated sector before collision is processed.

### Why RenderLimits is suspicious

A thrown spear is not a normal dropped item until the projectile hits. If RenderLimits reduces or alters loaded/current zone handling, a long-distance projectile can be removed from scene/ZDO object lists before `Projectile.SpawnOnHit` runs.

Likely suspicious settings:

- low `Loaded zones`,
- low `Generated zones`,
- unusual `Active zones`,
- server/client RenderLimits mismatch,
- changing limits during play while projectiles are in flight.

### Practical mitigation while testing

For reproduction testing, temporarily increase rather than decrease loaded/generated range:

```ini
Active zones = 2
Loaded zones = 4
Generated zones = 6
```

If the bug becomes rarer with larger loaded/generated zones, RenderLimits is probably contributing.

## SkadiNet

SkadiNet is a network optimization mod. It advertises adaptive ZDO scheduling, ownership recovery, payload reduction, compression, and visual RPC filtering. It is less likely to directly delete a projectile, but its ownership behavior can interact with Valheim's owner-driven projectile simulation.

### Why SkadiNet is suspicious

Valheim projectile simulation is expected to be owner-driven. If ownership is transferred or recovered while a projectile is in flight, the previous owner may stop simulating movement/collision, while the new owner may not have the projectile fully active or loaded. This is most concerning in multiplayer and when RenderLimits also reduces loaded/current object availability.

Likely suspicious setting:

```ini
OwnershipIntensity = 0/45/60/etc.
```

The first A/B test should set:

```ini
OwnershipIntensity = 0
```

Then compare against the user's normal setting.

## Combined-risk scenario

The highest-risk case is:

1. player throws spear from high mountain,
2. spear travels far and downwards for a long time,
3. RenderLimits loaded/current area excludes the projectile before hit,
4. SkadiNet ownership/scheduling changes delay or move ownership,
5. projectile is destroyed or stops simulating before it spawns the recoverable item.

The safety net mod does not need to prove exactly which of these happened. It only needs to notice that a recoverable spear projectile is being lost without a hit and force item recreation.
