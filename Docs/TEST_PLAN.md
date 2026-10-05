# Test plan

## Indicator and auto-pickup checks

The offline checks inspect the original installed game DLL and the final merged mod DLL, then exercise location-record serialization, player-grouped candidate caches using isolated managed ZDO fixtures, nearest-weapon/newest-tombstone selection, in-game age formatting, and up to ten HUD markers without starting Unity:

```powershell
dotnet build FearNoSpear.sln -c Debug -p:DeployToGame=true
dotnet build Tests/FearNoSpear.Checks.csproj -c Debug
dotnet Tests/bin/Debug/net8.0/FearNoSpear.Checks.dll "C:/Program Files (x86)/Steam/steamapps/common/Valheim" bin/Debug/FearNoSpear.dll
```

These checks do not apply Harmony detours in Unity, render the indicator, or simulate network ownership. The original DLLs are not publicized or modified. Both server and clients must use the same updated build for the protocol-7 location response.

Run these gameplay cases on a local host and a dedicated server with two modded clients:

1. Throw a spear in BeamAndHud style. Check the 500-meter column and icon/distance. Turn around and check the HUD edge marker, including directly behind and targets passing the camera plane. Beyond the camera far clip or behind terrain, the HUD must remain. Switch live through Beam, Hud, and Off; no old beams or labels should remain.
2. Set MaxDisplayedSpears to each value 1 through 5. Throw at least 6 spears, including identical ones from two players at the same spot. Each player sees only their own nearest N drops, with stable identities and no overlapping HUD labels. Recover one and confirm the next nearest takes its place. Check count changes, narrow/ultrawide/windowed resolutions, and targets sharing a screen edge. Invalid counts 0 and 6 must clamp to 1 and 5.
3. Watch a spear bounce, roll, or float. Nearby indicators must follow loaded drops. Move far enough to unload them, then verify automatic server updates still find them.
4. Stand beside another player's thrown spear. It must not move toward you or auto-pick up. Manual interaction must still work. The thrower must auto-pick up normally, subject to vanilla inventory/weight/pickup rules. Clan membership does not bypass the spear rule.
5. Drop a spear from inventory rather than throwing it. Check that it has no thrower tag and uses vanilla pickup behavior. Check ordinary items too.
6. Change both indicator styles, count, and CleanDeathPins independently on two clients while Lock Configuration is On. These preferences must remain local. OwnerOnlyTombstones must remain server-controlled. Rescue uses fixed internal rules and must not register config entries. With both styles Off, local queries stop but protection and thrower-only pickup still work; a host using Off must still answer other clients. With just one style Off, the other category must keep working.
7. Confirm no chat-input Harmony patch or spear map-pin creation remains. Old command text is ordinary chat. Existing saved map pins must not be broadly deleted. Archive sources must be absent from the compiled assembly and build output; no migration or legacy execution path is expected.
8. Pick up a spear while a location response is delayed. It must not reappear from that stale response. Test empty responses, timeouts, reconnect, death, logout, and world changes. Toggle Off while a response is pending, then enable indicators again.
9. Test normal hits, TTL rescue, and unexpected-destroy rescue. Check the exact returned drop's ThrowerPlayerID, original item data, and absence of duplicate drops. Check non-spear projectile behavior.
10. Confirm indicators hide with the HUD, map, inventory, menu, death, and teleport. A dedicated server must allocate no render objects.
11. Profile 1 and 5 indicators in BeamAndHud, Beam, and Hud. Beams must be reused and remain within fixed width bounds of 0.1 to 1 meter. Check 0.1 meters at close range, 0.7 meters at 500 meters, and the 1-meter cap beyond about 714 meters. Width follows distance times 0.0014 and must not resize HUD markers. No beam-width config entries should be registered. Display count must not multiply requests or scans. HUD must not intercept mouse input. Actual rendering and frame-time costs require gameplay measurements.
12. On a dedicated server, compare an administrator with debug mode on/off and a non-admin with debug mode on. Only admin + debug may bypass this mod's owner restriction. Verify the local host, ordinary owner, Clan members/Guests, and unrelated players. Container-in-use and inventory restrictions must remain intact.
13. Recover your tombstone with CleanDeathPins true/false and die with no inventory. Only the local configured behavior should affect death pins. Other players' personal settings and saved custom pins must remain untouched.
14. Equip non-spear weapons configured with SecondaryAttacks ImpactBurst. Check normal landing, bounce, distant/unloaded lookup, correct icon/variant, original durability/custom data, owner-only auto-pickup, and manual pickup by another player. Repeat with either mod's patch registration order. The exact returned drop must receive the thrower tag. Inventory drops must not acquire a tag.
15. Exercise SpearRain followups, boomerang catch, full-inventory boomerang return, and other copied throws. Followups and successfully caught weapons must not create rescue drops. A real dropped copied weapon may be tracked. Test both mods without the other installed.
16. Leave six or more tombstones, including two at the same spot and old graves far away. MaxDisplayedTombstones defaults to 1; test every value from 1 to 5, including live decreases and increases. Only your newest N remaining graves should appear, regardless of distance or Clan membership. Values 0 and 6 must clamp to 1 and 5. The count is personal, independent of the weapon count, and does not multiply server requests. Fully recover the newest and confirm the next remaining grave enters the selection. Partial loot must retain the marker. Deleting a death map pin must not change the HUD. Recover a grave as an authorized clanmate/admin and observe the owner's next server refresh.
17. Verify tombstone age beneath the icon and distance on its right. At a 1200-second world day, 3000 world seconds means 2d 12h. Test sleep/time skips, clock rewind, custom day length, missing timestamps, restart, reconnect, and a world change. No real date or extra persistent timestamp is created.
18. Show five weapons and five tombstones together, including all markers on one screen edge. Test each pair of BeamAndHud/Beam/Hud/Off styles, low/high resolutions, and live toggles. Labels, age, and pointers must fit without overlaps; beam-only targets must not consume HUD placement slots. Check profiling with ten pooled renderers and no forced zone loads. These visual and performance checks require Unity gameplay.
19. Delay an old response across a style toggle or tombstone recovery. The request ID and exact-key suppression must prevent stale resurrection. Move away to unload a still-existing grave and confirm it remains discoverable from the server. Verify an old protocol-6 peer is explicitly rejected and that server-local display settings do not constrain clients.
20. Check yellow weapon beams and bright lavender tombstone beams, with the same upward fade and unchanged HUD icons. Reuse a pooled marker across weapon/tombstone/weapon targets and toggle Hud back to Beam. Colors must not carry over from the previous target type. Check that material count remains shared and stable after warm-up.
21. Profile large-world server queries with multiple clients, separating cached requests from each requested category's five-second candidate scan. Requests between refreshes must inspect only that player's candidate bucket, with live owner/existence checks. A new remote target may need roughly ten seconds for discovery, excluding network delays. Changing ownership or destroying a cached ZDO must not leak another player's target or return a destroyed one. On the client, full candidate selection should follow the half-second scan or explicit invalidation, while selected moving drops follow every frame. Measure frame time, allocations, and transparent overdraw; full world scans and half-second candidate work still need profiling.
22. Change both display counts and styles live, move between two nearly equidistant drops, and recover the selected drop/grave just after a scan. Count/style changes and local pickup must not wait for the next scan; nearest-weapon switching may wait up to half a second. Check server response/expiry, empty graves, unload/reload, map/menu hide and show, reconnect, and world changes. Cached targets must not persist beyond these invalidations, and the next eligible target must replace a recovered one.
23. Install Quick Stack Store Sort Trash Restock 1.4.15 with FearNoSpear. Check startup for AutoPickup patch errors and verify the inventory's sort, quick-stack, restock, and favorite buttons initialize and respond. Trash-flagged items must still obey QuickStackStore's pickup setting; another player's tagged weapon must still reject automatic attraction/pickup; manual pickup must remain possible. Repeat without QuickStackStore. Offline checks invoke FearNoSpear's built transpiler and compose it with the matcher/insertion pattern verified in QuickStackStore 1.4.15 in both orders, including all permission combinations and label/block entry preservation. They do not execute QuickStackStore's full startup, Harmony detours, or Unity UI.

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

### Optional thrown-weapon integration

The data contract uses ZDO `long` values containing the throwing character's `Player.GetPlayerID()`, never the crafter ID or network peer/owner ID:

- `CaptainValheim.ThrowerPlayerID`: captured when the equipped shield is consumed, carried through chain/return controllers, and written to the actual dropped shield by CaptainValheim.
- `SecondaryAttacks.ThrowerPlayerID`: written on recoverable copied-throw projectiles independently of visual swapping. FearNoSpear's existing `Projectile.SpawnOnHit` drop wrapper copies it to the exact returned drop, even without a spear tracker.
- FearNoSpear reads these keys only for auto-pickup. They do not expand HUD candidates or rescue eligibility. Its existing tracking tag remains separate. A direct CaptainValheim drop tag takes precedence over the SecondaryAttacks projectile tag, then the generic FearNoSpear tracking tag.
- Tags live on world ZDOs, not `ItemData.m_customData`. Manual pickup followed by an ordinary inventory drop does not carry the restriction. No migration or attribution guesses are applied to old untagged drops.
- Neither producer installs an auto-pickup patch or requires FearNoSpear to load. FearNoSpear remains the only enforcement point; a client without it can still auto-pick up these items. This is not server-enforced anti-theft.

Run the cross-DLL checks after building all three projects in Debug:

```powershell
dotnet build Tests/FearNoSpear.Checks.csproj -c Debug
dotnet Tests/bin/Debug/net8.0/FearNoSpear.Checks.dll 'C:/Program Files (x86)/Steam/steamapps/common/Valheim' bin/Debug/FearNoSpear.dll ../CaptainValheim/bin/Debug/CaptainValheim.dll ../SecondaryAttacks/bin/Debug/SecondaryAttacks.dll
```

These checks execute the metadata readers with the original game's managed ZDO data, verify the producer/consumer key and ownership contracts in the merged DLLs, and retain the two-order QuickStackStore gate composition checks. They do not run Unity, install Harmony detours, or simulate actual world drops or network delivery.

Remaining in-game cases, with two players on a host and then a dedicated server:

1. Throw identical-spec weapons as each player. Only the actual thrower attracts each item; crafting or carrying it earlier gives no pickup privilege. Repeat while indicators are Off and with QuickStackStore installed.
2. For shields, cover ordinary landing, chained flight, blocked return, full inventory, projectile creation failure and destruction with the original player object unavailable. Verify one item and the original thrower tag on every resulting drop. Successful direct returns must not spawn a drop.
3. For SecondaryAttacks, cover ImpactBurst, a copied throw retaining its native visual, ordinary impact, boomerang catch and failed/full-inventory catch. Inspect the projectile and resulting drop tag. SpearRain virtual follow-ups must not spawn items or acquire the auto-pickup tag.
4. Manually pick up another player's thrown item, then drop it from inventory. Verify ordinary behavior and no retained thrower custom data. Retry after a landed item moves, its network owner changes, and the world is saved/reloaded.
5. Test each producer without FearNoSpear and FearNoSpear without either producer. No missing-dependency errors, extra auto-pickup patches, new shield HUD markers, or new shield TTL/rescue behavior should appear.

Verify these are not affected:

- arrows,
- bolts,
- enemy projectiles,
- unrelated thrown non-spear items (SecondaryAttacks copied recoverable throws are intentionally supported),
- harpoon behavior,
- normal spear hit-and-pickup behavior,
- multiplayer with several clients in the same area.
