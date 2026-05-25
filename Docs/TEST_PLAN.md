# Test plan

## Test setup

Use the same mountain/long-throw location each time.

Recommended test item:

- a low-value spear first,
- then the user's real affected spear type after safety is confirmed.

Enable Valheim console/log capture so the mod's rescue messages can be matched to each throw.

## Reproduction matrix

| Case | FearNoSpear | RenderLimits | SkadiNet | Purpose |
|---|---:|---:|---:|---|
| A | off | off | off | Check vanilla reproducibility |
| B | off | on, user settings | off | Isolate RenderLimits |
| C | off | Active 2 / Loaded 4 / Generated 6 | off | Does larger loaded range help? |
| D | off | C settings | on, `OwnershipIntensity=0` | SkadiNet with ownership disabled |
| E | off | C settings | on, user settings | Check SkadiNet ownership/scheduling contribution |
| F | on | user settings | user settings | Confirm mitigation works under real setup |
| G | on | C settings | `OwnershipIntensity=0` | Confirm most conservative stable setup |

## Expected mod log messages

Possible messages:

```text
Rescued thrown spear before projectile loss: reason=TTL expiry ...
Rescued thrown spear before projectile loss: reason=ZNetScene.Destroy before hit ...
Rescued thrown spear before projectile loss: reason=OnDestroy fallback ...
```

## Interpreting results

### If `TTL expiry` rescues are common

The spear is flying longer than the vanilla projectile lifetime. FearNoSpear fixes the minimum tracked spear projectile TTL at 60 seconds, so frequent TTL rescues usually mean the throw is exceeding even that extended lifetime.

### If `ZNetScene.Destroy before hit` or `OnDestroy fallback` rescues are common

The likely issue is scene/zone unload, ZDO cleanup, or external destroy. RenderLimits loaded/generated settings are a strong suspect. Keep rescue-on-destroy enabled.

### If rescue only works in single player but not multiplayer

Investigate ownership. Try:

```ini
AllowLastKnownOwnerIfZNetViewInvalid = true
LastKnownOwnerGraceSeconds = 2
```

Then compare with:

```ini
AllowLastKnownOwnerIfZNetViewInvalid = false
```

Disabling the last-known-owner fallback favors duplicate prevention over late invalid-view rescue.

## Duplicate-item checks

For each rescue case:

1. Throw one spear.
2. Confirm exactly one dropped item appears.
3. Confirm the projectile disappears.
4. Confirm no extra item appears on another client.
5. Confirm durability/quality/custom data is preserved.

## Regression checks

Verify these are not affected:

- arrows,
- bolts,
- enemy projectiles,
- thrown non-spear items if any,
- harpoon behavior,
- normal spear hit-and-pickup behavior,
- multiplayer with several clients in the same area.
