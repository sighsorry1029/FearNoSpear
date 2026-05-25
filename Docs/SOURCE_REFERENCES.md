# Source references for context

These links are for Codex/developer orientation. The implementation should be verified against the user's installed DLLs, not blindly against web descriptions.

## RenderLimits

- GitHub: https://github.com/JereKuusela/valheim-render_limits
- Thunderstore: https://thunderstore.io/c/valheim/p/JereKuusela/Render_Limits/

Relevant context: RenderLimits describes itself as changing how far away Valheim zones are rendered, loaded, and generated. This is directly relevant because a recoverable spear is still an in-flight projectile until hit handling spawns the item.

## SkadiNet

- Thunderstore: https://thunderstore.io/c/valheim/p/sighsorry/SkadiNet/

Relevant context: SkadiNet describes adaptive ZDO scheduling, ownership recovery, payload reduction, compression, and RPC filtering. Ownership behavior is the most relevant part for thrown projectile simulation.

## Local binary references from the user's uploaded files

The user's original task environment included these DLLs for inspection:

- `SkadiNet(1).dll`
- `RenderLimits.dll`
- `assembly_valheim_publicized(4).dll`
- `Assembly-CSharp_publicized(4).dll`
- additional publicized Unity/Valheim assemblies

This zip does not bundle those binaries.
