# Test plan

## Indicator and auto-pickup checks

The offline checks inspect the original installed game DLL and the final merged mod DLL, then exercise location-record serialization, nearest-N selection, and HUD placement without starting Unity:

```powershell
dotnet build FearNoSpear.sln -c Debug -p:DeployToGame=true
dotnet build Tests/FearNoSpear.Checks.csproj -c Debug
dotnet Tests/bin/Debug/net8.0/FearNoSpear.Checks.dll "C:/Program Files (x86)/Steam/steamapps/common/Valheim" bin/Debug/FearNoSpear.dll
```

These checks do not apply Harmony detours in Unity, render the indicator, or simulate network ownership. The original DLLs are not publicized or modified. Both server and clients must use the same updated build for the protocol-6 location response.

Run these gameplay cases on a local host and a dedicated server with two modded clients:

1. Throw a spear in BeamAndHud style. Check the 500-meter column and icon/distance. Turn around and check the HUD edge marker, including directly behind and targets passing the camera plane. Beyond the camera far clip or behind terrain, the HUD must remain. Switch live through Beam, Hud, and Off; no old beams or labels should remain.
2. Set MaxDisplayedSpears to each value 1 through 5. Throw at least 6 spears, including identical ones from two players at the same spot. Each player sees only their own nearest N drops, with stable identities and no overlapping HUD labels. Recover one and confirm the next nearest takes its place. Check count changes, narrow/ultrawide/windowed resolutions, and targets sharing a screen edge. Invalid counts 0 and 6 must clamp to 1 and 5.
3. Watch a spear bounce, roll, or float. Nearby indicators must follow loaded drops. Move far enough to unload them, then verify automatic server updates still find them.
4. Stand beside another player's thrown spear. It must not move toward you or auto-pick up. Manual interaction must still work. The thrower must auto-pick up normally, subject to vanilla inventory/weight/pickup rules. Clan membership does not bypass the spear rule.
5. Drop a spear from inventory rather than throwing it. Check that it has no thrower tag and uses vanilla pickup behavior. Check ordinary items too.
6. Change style, count, and CleanDeathPins independently on two clients while Lock Configuration is On. These preferences must remain local. OwnerOnlyTombstones must remain server-controlled. Rescue uses fixed internal rules and must not register config entries. With style Off, local queries stop but protection and thrower-only pickup still work; a host using Off must still answer other clients.
7. Confirm no chat-input Harmony patch or spear map-pin creation remains. Old command text is ordinary chat. Existing saved map pins must not be broadly deleted. Archive sources must be absent from the compiled assembly and build output; no migration or legacy execution path is expected.
8. Pick up a spear while a location response is delayed. It must not reappear from that stale response. Test empty responses, timeouts, reconnect, death, logout, and world changes. Toggle Off while a response is pending, then enable indicators again.
9. Test normal hits, TTL rescue, and unexpected-destroy rescue. Check the exact returned drop's ThrowerPlayerID, original item data, and absence of duplicate drops. Check non-spear projectile behavior.
10. Confirm indicators hide with the HUD, map, inventory, menu, death, and teleport. A dedicated server must allocate no render objects.
11. Profile 1 and 5 indicators in BeamAndHud, Beam, and Hud. Beams must be reused and remain within fixed width bounds of 0.1 to 1 meter. Check 0.1 meters at close range, 0.7 meters at 500 meters, and the 1-meter cap beyond about 714 meters. Width follows distance times 0.0014 and must not resize HUD markers. No beam-width config entries should be registered. Display count must not multiply requests or scans. HUD must not intercept mouse input. Actual rendering and frame-time costs require gameplay measurements.
12. On a dedicated server, compare an administrator with debug mode on/off and a non-admin with debug mode on. Only admin + debug may bypass this mod's owner restriction. Verify the local host, ordinary owner, Clan members/Guests, and unrelated players. Container-in-use and inventory restrictions must remain intact.
13. Recover your tombstone with CleanDeathPins true/false and die with no inventory. Only the local configured behavior should affect death pins. Other players' personal settings and saved custom pins must remain untouched.

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

The likely issue is scene/zone unload, ZDO cleanup, or external destroy. RenderLimits loaded/generated settings are a strong suspect. Rescue-on-destroy remains active as a fixed rule.

### If rescue only works in single player but not multiplayer

Investigate ownership using client and server logs. With a valid ZNetView, only its owner may rescue. After invalidation, this client must have last been confirmed as owner no more than 2 seconds ago; otherwise rescue must be skipped. Test ownership transfer, invalidation within and beyond that window, and a tracker that has never confirmed local ownership. These rules are fixed, not configurable.

For TTL rescue, check that the 1-second window retains the minimum margin of 1.5 physics steps. Normal hits must not rescue again. A failed spawn must release its ZDO claim, and shutdown must not spawn items. Offline checks inspect the compiled guards but do not replace these runtime cases.

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
