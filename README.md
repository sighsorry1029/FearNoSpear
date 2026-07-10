# FearNoSpear

![](https://i.ibb.co/dJbNstbC/Video-Project-27.gif)

FearNoSpear prevents thrown spears from disappearing on long throws, zone unloads, and network cleanup. It also adds a chat locator for tracked spears and can clean up vanilla death pins when tombstones are recovered.

## Features

- Protects recoverable thrown spear projectiles.
- Extends tracked spear projectile TTL to a fixed 60 seconds.
- Rescues the original spear before TTL expiry or unexpected projectile cleanup.
- Uses Valheim's own `Projectile.SpawnOnHit` path first, then verifies that a matching spear drop exists.
- Falls back to `ItemDrop.DropItem` only when the native path cannot produce a matching drop.
- Uses owner-only rescue and a ZDO claim flag to reduce multiplayer duplicate-spawn risk.
- Never deletes an existing spear merely because it matches the rescued spear's item data.
- Adds a configurable chat command, default `!myspear`, that pins up to 5 thrown spear locations.
- Optionally removes vanilla death pins when the local player's tombstone is recovered, and removes death pins from deaths that create no tombstone.

## Chat Locator

Type the configured command in normal in-game chat:

```text
!myspear
```

The command is consumed locally, so it is not sent as public chat. It creates saved minimap pins for known tracked spear locations and opens the map. A single result is named `Spear!`; multiple results are named `Spear 1`, `Spear 2`, and so on.

Thrown spears are tagged with a small `FearNoSpear.ThrowerPlayerID` ZDO value. When the command is used, the server searches spear drop ZDOs for that thrower metadata and returns matching positions, including unloaded areas that still have saved ZDOs.

Loaded `ItemDrop` scanning is also used to refresh the exact current position when the spear is nearby. Spears without thrower metadata are ignored by the locator so old projectile positions do not create stale pins.

When a tracked spear is picked up, locator pins created by the current session are removed by matching the exact spear drop record.

## Death Pin Cleanup

`CleanDeathPins` removes the vanilla `Death` map pin when the local player's tombstone is recovered. It also removes the death pin immediately if the death created no tombstone, such as when there was nothing to drop.

The cleanup only targets saved minimap pins of type `Death` near the matching death or tombstone position.

## Config

Available config options:

```ini
[General]
Lock Configuration = On
Enabled = true
ChatCommand = !myspear
CleanDeathPins = true

[Rescue]
TTLRescueWindowSeconds = 1.0
AllowLastKnownOwnerIfZNetViewInvalid = true
LastKnownOwnerGraceSeconds = 2
```

### General

`Lock Configuration`  
Locks synchronized settings to the authoritative config.

`Enabled`  
Master switch for spear tracking, rescue, locator, and death pin cleanup.

`ChatCommand`  
Chat command used to pin known tracked spear locations. The comparison is case-insensitive. Leave empty to disable the chat command.

`CleanDeathPins`  
Enables death pin cleanup for recovered tombstones and deaths that create no tombstone.

### Rescue

`TTLRescueWindowSeconds`  
How close to projectile TTL expiry the mod should rescue a still-airborne tracked spear. `1.0` means the final second of projectile lifetime.

`AllowLastKnownOwnerIfZNetViewInvalid`  
Allows the most recent known owner to rescue if the projectile `ZNetView` has already become invalid.

`LastKnownOwnerGraceSeconds`  
Maximum age, in seconds, for the last-known owner fallback.

## Fixed Internal Defaults

These are intentionally not exposed as config options:

- Minimum tracked spear projectile TTL: `60` seconds.
- Locator pins per command: `5`.
- Spear detection uses the item skill first, with a case-insensitive `spear` name fallback for compatible modded items.
- Rescue before TTL expiry: enabled.
- Rescue on unexpected destroy: enabled.
- ItemDrop fallback: enabled.
- Owner-only rescue: enabled.
- ZDO rescue claim flag: enabled.
- Nearby native-drop verification and metadata matching radius: `4` meters.

## Github
https://github.com/sighsorry1029/FearNoSpear
