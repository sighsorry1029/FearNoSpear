# Legacy Indicator and Chat Reference

Snapshot taken before the indicator simplification on 2026-10-03.

- `SpearIndicator.cs.txt`: character-relative 3D arrow, placement, mesh, and former beam/HUD implementation.
- `SpearChatCommand.cs.txt` and `SpearPinManager.cs.txt`: chat parsing and session-owned spear map pins.
- `SpearLocator.cs.txt` and `Plugin.cs.txt`: request, configuration, and Harmony integration used by those features.

These are reference snapshots, not a standalone build. They retain their original dependencies and old configuration names. The project includes them as `None`, never `Compile`; they are not copied to build output or release packages. Active locator, metadata, rescue, and death-pin code remains in the project root. New changes belong there, not in this archive.
