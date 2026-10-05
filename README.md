# FearNoSpear

![Automatic indicators for up to five thrown spears](https://i.ibb.co/r17QSNR/fearnospear2.gif)

*Track up to five thrown spears automatically.*

![Thrown spears marked with light beams and HUD icons](https://i.ibb.co/B2h3GTKy/Screenshot-2026-10-03-141025.png)

*Light beams with item icons and distances.*

![Thrown spears shown with HUD icons only](https://i.ibb.co/DHSWLRy4/Screenshot-2026-10-03-141037.png)

*HUD-only markers.*

![Screen-edge markers pointing toward spears behind the player](https://i.ibb.co/Xx7X8wyT/Screenshot-2026-10-03-141102.png)

*Screen-edge markers guide you toward spears outside your view.*

FearNoSpear helps prevent thrown spears from disappearing and shows where to find them. It also tracks your tombstones, prevents accidental auto-pickup by other players, and protects tombstone access.

## Find Your Spears

Markers appear after your spear lands or is rescued. Show your nearest spear by default, or up to five at once. Nearby markers follow moving drops; distant positions refresh from the server about every five seconds, including unloaded areas. A new distant drop may take about ten seconds to first appear.

| Display mode | What you see |
| --- | --- |
| `BeamAndHud` | A light beam, item icon, and distance. Default. |
| `Beam` | A light beam only. |
| `Hud` | An item icon and distance, with screen-edge direction markers. |
| `Off` | No indicators or queries for that target type. Protection stays active. |

Weapon beams are yellow; tombstone beams are bright lavender. Beams extend 500 meters upward and widen with distance, from 0.1 to 1 meter. Terrain and render distance can hide them; HUD markers remain visible through terrain and beyond the world rendering range.

SecondaryAttacks copied throws, including ImpactBurst, also work with weapons other than spears. They use the original weapon's icon and share the same display limit. Only recoverable throws are protected: virtual SpearRain projectiles and boomerangs already returned to inventory are excluded.

Only drops tagged with your thrower ID are tracked. Markers disappear when you pick them up. The mod does not add chat commands or spear map pins.

## Auto-Pickup

Only the thrower can automatically attract and pick up a tagged weapon. Other players can still pick it up manually.

CaptainValheim shield drops and SecondaryAttacks recoverable copied throws can also provide their thrower ID for this rule. Use the updated throwing mod and FearNoSpear on participating clients. The integration adds no required mod dependency or extra settings. Shield returns stay under CaptainValheim's control; this does not add shield indicators or shield rescue.

Weapons dropped from inventory and items without a thrower tag keep normal pickup behavior. This rule is always active, including when indicators are off.

## Tombstones

Your newest remaining tombstone is shown by default. Set `MaxDisplayedTombstones` to show 1 to 5 at once. The HUD shows the death icon, distance on its right, and age below it, such as `2d 12h`. This means **two in-game days and twelve in-game hours since creation**, not a calendar date or real elapsed time. Sleeping advances the age; it uses the world's current day length.

Tombstones are selected newest first, independently of the nearest-weapon limit. Nearby markers follow the actual tombstone; distant locations refresh from the server about every five seconds. A new distant tombstone may take about ten seconds to first appear. Fully recovered or removed tombstones leave the display, while partially looted ones remain. A death without a tombstone has no indicator.

Use `TombstoneIndicatorStyle` to choose BeamAndHud, Beam, Hud, or Off. It is separate from weapon indicators and death-pin cleanup. Deleting a map pin does not hide its tombstone indicator. Only your own tombstones are shown, not those of clanmates.

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
| `SpearIndicatorStyle` | `BeamAndHud` | Display mode for your thrown weapons. |
| `MaxDisplayedSpears` | `1` | Show 1 to 5 of your nearest tracked weapon drops. |
| `TombstoneIndicatorStyle` | `BeamAndHud` | Display mode for your tombstones. |
| `MaxDisplayedTombstones` | `1` | Show 1 to 5 of your newest remaining tombstones. |
| `CleanDeathPins` | `true` | Clean up your death pins. |
| `OwnerOnlyTombstones` | `true` | Apply the owner, admin, and Clan access rules. |

Indicator and death-pin settings are personal. Only `OwnerOnlyTombstones` is synchronized through ServerSync.

## Built-In Protection

These rules are always active and have no config switches:

- Tracked recoverable weapon projectiles get a minimum lifetime of 60 seconds.
- Rescue starts in the final second, with a minimum physics-step safety margin.
- Unexpected projectile removal also triggers a rescue attempt.
- The game's normal drop method runs first, with a stored-item fallback if needed.
- With valid network data, only the current owner may rescue. If that data becomes invalid, the last confirmed owner may rescue only when its ownership was verified within the past two seconds.
- Ownership checks, a shared rescue flag, and one attempt per local tracker help prevent duplicates.
- Existing drops are never deleted just because their item data matches a rescued spear.

Protection reduces loss and duplicate risk; it cannot eliminate either in every network situation. In particular, invalid network data can prevent clients from sharing the rescue flag.

[Source code](https://github.com/sighsorry1029/FearNoSpear)
