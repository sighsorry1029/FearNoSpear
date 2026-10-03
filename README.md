# FearNoSpear

![Automatic indicators for up to five thrown spears](https://i.ibb.co/r17QSNR/fearnospear2.gif)

*Track up to five thrown spears automatically.*

![Thrown spears marked with light beams and HUD icons](https://i.ibb.co/B2h3GTKy/Screenshot-2026-10-03-141025.png)

*Light beams with item icons and distances.*

![Thrown spears shown with HUD icons only](https://i.ibb.co/DHSWLRy4/Screenshot-2026-10-03-141037.png)

*HUD-only markers.*

![Screen-edge markers pointing toward spears behind the player](https://i.ibb.co/Xx7X8wyT/Screenshot-2026-10-03-141102.png)

*Screen-edge markers guide you toward spears outside your view.*

FearNoSpear helps prevent thrown spears from disappearing and shows where to find them. It also prevents accidental auto-pickup by other players and adds simple tombstone protections.

## Find Your Spears

Markers appear after your spear lands or is rescued. Show your nearest spear by default, or up to five at once. Nearby markers follow moving drops; distant positions refresh from the server about every five seconds, including unloaded areas.

| Display mode | What you see |
| --- | --- |
| `BeamAndHud` | A light beam, item icon, and distance. Default. |
| `Beam` | A light beam only. |
| `Hud` | An item icon and distance, with screen-edge direction markers. |
| `Off` | No indicators or automatic location queries. Spear protection stays active. |

Beams extend 500 meters upward and widen with distance, from 0.1 to 1 meter. Terrain and render distance can hide them; HUD markers remain visible through terrain and beyond the world rendering range.

Only your tagged spear drops are tracked. Markers disappear when you pick them up. The mod does not add chat commands or spear map pins.

## Spear Auto-Pickup

Only the thrower can automatically attract and pick up a tagged spear. Other players can still pick it up manually.

Spears dropped from inventory and items without a thrower tag keep normal pickup behavior. This rule is always active, including when indicators are off.

## Tombstones

By default, only the owner can open or recover a tombstone, with two exceptions:

- Server admins and the local host can bypass this mod's lock while in debug mode.
- With Clan, members of your active roster, including Guests, can recover clanmates' tombstones.

These exceptions do not bypass other mods' locks, full inventories, or containers already in use.

Death-pin cleanup removes your map pin when your tombstone is recovered. If a death creates no tombstone, it removes the pin after an eight-second grace period.

## Settings

All settings are in `[General]`.

| Setting | Default | Effect |
| --- | --- | --- |
| `Lock Configuration` | `On` | Locks the tombstone access setting to the server's value. |
| `SpearIndicatorStyle` | `BeamAndHud` | Choose a display mode from the table above. |
| `MaxDisplayedSpears` | `1` | Show 1 to 5 of your nearest spear drops. |
| `CleanDeathPins` | `true` | Clean up your death pins. |
| `OwnerOnlyTombstones` | `true` | Apply the owner, admin, and Clan access rules. |

Indicator and death-pin settings are personal. Only `OwnerOnlyTombstones` is synchronized through ServerSync.

## Built-In Protection

These rules are always active and have no config switches:

- Tracked spear projectiles get a minimum lifetime of 60 seconds.
- Rescue starts in the final second, with a minimum physics-step safety margin.
- Unexpected projectile removal also triggers a rescue attempt.
- The game's normal drop method runs first, with a stored-item fallback if needed.
- With valid network data, only the current owner may rescue. If that data becomes invalid, the last confirmed owner may rescue only when its ownership was verified within the past two seconds.
- Ownership checks, a shared rescue flag, and one attempt per local tracker help prevent duplicates.
- Existing drops are never deleted just because their item data matches a rescued spear.

Protection reduces loss and duplicate risk; it cannot eliminate either in every network situation. In particular, invalid network data can prevent clients from sharing the rescue flag.

[Source code](https://github.com/sighsorry1029/FearNoSpear)
