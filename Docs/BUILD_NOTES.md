# Build notes

## Active project

- `FearNoSpear.sln` builds the explicit C# source list in `FearNoSpear.csproj`, targeting .NET Framework 4.8.
- `FearNoSpearPlugin : BaseUnityPlugin` is a BepInEx plugin, not a preloader patcher. `Awake` binds settings, initializes cached private-member access, and installs Harmony patches.
- `Tests/FearNoSpear.Checks.csproj` is a separate .NET 8 command-line checker. It is not compiled into the mod.
- `Archive/LegacyIndicatorAndChat/*.txt` is reference material only. It is not executable compatibility or migration code.

## References and validation baseline

`environment.props` defines `ValheimGamePath`, `BepInExPath`, `CorlibPath`, and the local plugin deployment folder. The active references use the original installed Valheim DLLs under `valheim_Data/Managed` and BepInEx/Harmony under `BepInEx/core`. There is no publicizer build step. Never publicize game DLLs for analysis or copy references into a mod package.

The 2026-10-03 structural review used the installed Windows x64 client, Valheim **1.0.16**, Steam build **25527674**. Its `assembly_valheim.dll` SHA-256 is:

```text
96CFC004F7F4A6F30D070BEF39EAFD79C466A137121C4665A2F19FB9C15C6127
```

Relevant spawn/classification contracts were also checked in the preserved original **1.0.16 dedicated-server build 25527701**. Both roles expose public `ItemDrop.DropItem(ItemData, int, Vector3, Quaternion)`, private `Projectile.SpawnOnHit(GameObject, Collider, Vector3)`, and `Skills.SkillType.Spears = 5`. Private invocation remains explicit reflection; a successful compile alone does not prove Harmony or Unity execution.

The 1.0.8 changelog records an earlier 1.0.12 rebuild. The current reference baseline is not a new release or an expanded runtime-support claim. Version-specific evidence starts at `C:/Users/blizz/.codex/references/valheim/INDEX.md`; use the existing snapshots rather than re-extracting the game during normal builds.

## Dependencies and merge

- `Libs/ServerSync.dll` is the pinned `valheim-1.0.7-r1` reference, SHA-256 `B4DD786997F4E90D770F09EF3E9D64154754FE7E8EDFB4841795751895B35846`. The baseline label is not the current game's version.
- `ILRepack.targets` merges the built mod with `$(OutputPath)Libs/ServerSync.dll`, internalizing the library into the final `FearNoSpear.dll`. Do not deploy ServerSync as a separate plugin.
- Clan is a soft BepInEx dependency. Its API v5 public snapshot properties are bound through cached reflection; no Clan assembly is linked or merged. Keep access authorization distinct from network ownership and personal indicators.
- Game, Unity, Harmony, and BepInEx assemblies are compile/runtime references, not files to distribute with the mod. The Thunderstore manifest uses `denikson-BepInExPack_Valheim-5.4.2351`.

## Debug verification

Run from the repository root:

```powershell
dotnet build FearNoSpear.sln -c Debug -p:DeployToGame=true
dotnet build Tests/FearNoSpear.Checks.csproj -c Debug
dotnet Tests/bin/Debug/net8.0/FearNoSpear.Checks.dll "C:/Program Files (x86)/Steam/steamapps/common/Valheim" bin/Debug/FearNoSpear.dll
```

Stop on any failing exit code; do not run a stale checker after its build fails. The active target order is build, ILRepack, assembly-version inspection, then `CopyOutputDLL`. With `DeployToGame=true`, only the final merged mod DLL is copied to:

```text
C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/plugins/FearNoSpear.dll
```

Compare the source and deployed DLL SHA-256 after success. Never copy an older DLL after a failed build, or deploy `obj/ref` intermediates and reference assemblies.

The checker inspects original game metadata and the merged mod's IL, and executes managed fixtures, serialization, selection, and HUD geometry checks outside Unity. It does not run Harmony detours, render beams, measure FPS, or simulate network ownership. Use `Docs/TEST_PLAN.md` for host/dedicated gameplay cases. Existing source changes must remain separate from new refactor commits.

## Release boundary

Only an explicit Release request permits the existing Release packaging targets to run. Ordinary Debug validation must not produce deployment ZIPs or update the mod version.

Release packaging reads the final DLL assembly version, updates `Thunderstore/manifest.json`, and copies the root `README.md` only into a temporary package directory. The Thunderstore ZIP contains the DLL, README, CHANGELOG, manifest, and icon. The Nexus ZIP contains only the DLL. `Thunderstore/README.md` is not a second source.

Generated `bin`/`obj` files, ZIPs, archived code, and vendored libraries are outside active source refactoring. Dependency identity and merge inputs are checked, not rewritten. Full game-resource analysis, external-mod internals, actual multiplayer execution, and GPU profiling require separate evidence.
