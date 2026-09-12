| `Version` | `Update Notes`                                       |
|-----------|------------------------------------------------------|
| 1.0.8     | - Rebuilt for Valheim 1.0.12 and added synchronized owner-only tombstone recovery, an admin debug-mode bypass, and optional Clan access for the active roster, including Guests. |
| 1.0.7     | - Updated for Valheim 1.0.7 using original game assembly references, adopted the validated 1.0.7 ServerSync build, and replaced publicized-only private API calls with compatible cached accessors and public networking APIs. |
| 1.0.6     | - Removed destructive same-item duplicate cleanup, simplified rescue and locator state, hardened locator RPC validation, reduced repeated ItemDrop scans, and made no-tombstone death-pin cleanup wait for delayed tombstone creation. |
| 1.0.5     | - Reduced locator state and RPC payload complexity by removing stale item-key record storage and inlining the local spear record store. |
| 1.0.4     | - Simplified the spear locator around thrower-tagged world ZDO spear drops, fixed thrown spears being skipped when `pickedUp` is set, and removed stale projectile-location fallback records. |
| 1.0.3     | - Use thrower metadata and server-side ZDO scanning as the primary !myspear locator path, with tracked records kept as fallback. |
| 1.0.2     | - Fixed tombstone deathpin cleanup patch failing to load on Valheim builds where TombStone.Setup uses ownerUID as the parameter name. |
| 1.0.1     | - Deathpin can be removed if tombstone is recovered. |
| 1.0.0     | - Initial Release                                    |
