# Architecture Provenance

This note records the Fieldwright audit for the current Buildwright tree. It is
not a claim that every Buildwright line came from Fieldwright; it identifies
the implementation paths that were adapted and the paths that are
interoperability or Buildwright-only work.

## Audit result

Buildwright directly incorporates and modifies portions of Fieldwright's
client implementation. Fieldwright was therefore a source for these paths,
not only a conceptual or behavioral reference:

| Buildwright path | Fieldwright counterpart | Current status |
| --- | --- | --- |
| `Client/SelectionState.cs` | `SelectionState.cs` | Adapted selection and anchor state; Buildwright adds corner/tool operations. |
| `Client/BuildwrightGhostMesh.cs` | `GhostMesh.cs` | Adapted layered ghost mesh, packed-cell traversal, alpha handling, and tessellation fallback; Buildwright adds filters, Chisel data, and compatibility providers. |
| `Client/BuildwrightGhostRenderer.cs` | `GhostRenderer.cs` | Adapted floating placement, anchor-facing rotation, mirror transform, and layer rendering; Buildwright adds terrain and support-beam overlays. |
| `Client/BuildwrightTracker.cs` | `GhostMatchTracker.cs` | Adapted transformed-cell matching and progress tracking; Buildwright adds strict BlockEntity/Chisel/compatibility comparisons and diff rendering. |
| `Client/BuildwrightConfig.cs` | `FieldwrightConfig.cs` | Adapted matching, alpha, render-distance, and HUD configuration fields; Buildwright uses its own file and additional settings. |
| `Client/BlockEntityCapture.cs` | `BEMeshSources.cs`, `BEUtil.cs` | Adapted temporary BlockEntity creation, lifecycle calls, reflection-based mesh builders, and mesh-field extraction; Buildwright adds public API paths and game-specific providers. |
| `Client/BuildwrightModSystem.cs` | `FieldwrightModSystem.cs` | Adapted the selection/paste workflow and related controls, then substantially expanded and renamed it. |

## Format boundary

`Core/BlueprintCodec.cs` accepts the Fieldwright JSON wrapper fields
`schematic`, `anchorOffset`, and `anchorFace`. This is an independently
implemented compatibility parser. Buildwright's `.vsbp` ZIP container,
metadata, Chisel entries, and server operation protocol are Buildwright-owned.

## What is not shipped

The Buildwright projects do not reference or package `Fieldwright.dll`, a
Fieldwright binary, texture, shape, or other asset. They reference the
installed Vintage Story API/game assemblies at build time. The server project
and exact support-beam protocol are Buildwright additions and are not taken
from Fieldwright's client mod.

## License handling

The adapted Fieldwright portions are covered by the MIT License reproduced in
`LICENSE-Fieldwright.txt`. Keep that copyright and permission notice when
redistributing those portions. Original Buildwright material is also released
under the MIT License; see `COPYRIGHT.md` and `THIRD-PARTY-NOTICES.md` for the
complete boundary.
