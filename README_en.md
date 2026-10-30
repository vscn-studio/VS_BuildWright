# Buildwright

Buildwright is a blueprint, projection, and build-progress mod for Vintage Story 1.22.3. The client manages blueprints, projections, and model exports; the companion server component performs world operations that require server authority.

Current open-source version: `1.0.3`

## Features

- Capture regions as `.vsbp` blueprints, including blocks, fluids, block entities, decorations, and Chisel/MicroBlock data.
- Blueprint library and file tools with backups, copies, templates, metadata, and version diagnostics.
- Ghost projection with movement, auto-facing, locking, rotation, mirroring, layers, clipping, opacity, and player-relative movement.
- Full, Missing, Wrong, and Difference display modes with Loose, Medium, and Strict matching.
- BlockEntity and Chisel diff checks, wrong-cell highlighting, material requirements, and build-progress HUDs.
- Confirmed batch placement, rebuild, clear, clone, move, rotate, mirror, and fill operations in single-player Creative mode.
- Native Vintage Story JSON import/export plus Item Shape, Blockbench, Blender GLB, and 3D-print STL exports.
- Blockbench exports preserve Chisel geometry, texture references, and UVs. Non-square chisel faces rotate the selected UV rectangle with the texture to prevent 90-degree stretching.
- A compatibility API and companion server protocol for integrations by other mods.

## Installation

The client requires VSImGui `1.2.7` as a separate mod. A Release build produces:

- `artifacts/buildwright-1.0.3.zip` for the client `Mods` directory.
- `artifacts/buildwrightserver-1.0.3.zip` for the server `Mods` directory. The server package is also required in single-player.

Do not extract both ZIPs into one directory, and remove older packages with the same mod id before installing an update.

## Building

You need the .NET 10 SDK, a Vintage Story 1.22.3 installation, and the VSImGui 1.2.7 directory. Adjust the paths below for your machine:

```powershell
$env:VINTAGE_STORY = 'D:\Vintagestory'
$env:IMGUI_DIRECTORY = 'E:\vintagestory\vsimgui_1.2.7'

dotnet run --project tests/Buildwright.CoreSmoke.csproj -c Release --no-restore `
  -p:VINTAGE_STORY=$env:VINTAGE_STORY

dotnet build -c Release --no-restore `
  -p:VINTAGE_STORY=$env:VINTAGE_STORY `
  -p:ImGuiDirectory=$env:IMGUI_DIRECTORY

dotnet build BuildwrightServer/BuildwrightServer.csproj -c Release --no-restore `
  -p:VINTAGE_STORY=$env:VINTAGE_STORY

pwsh -NoProfile -File tests/ReleasePackageSmoke.ps1 -Version 1.0.3
```

The Blockbench plugin compatibility check uses a local Vintage Story Blockbench plugin:

```powershell
$env:BUILDWRIGHT_BLOCKBENCH_FIXTURES = "$PWD/artifacts/blockbench-smoke"
dotnet run --project tests/Buildwright.CoreSmoke.csproj -c Release --no-restore `
  -p:VINTAGE_STORY=$env:VINTAGE_STORY
node tests/BlockbenchPluginSmoke.mjs E:/vintagestory/blockbenchplugin `
  $env:BUILDWRIGHT_BLOCKBENCH_FIXTURES $env:VINTAGE_STORY
```

Assemblies are written to `bin/Release/Mods/`, and Release ZIPs are written to `artifacts/`. Vintage Story, VSImGui, and other external dependencies are not copied into this source tree.

## Layout

- `Client/`: client mod, UI, projection, blueprint export, and compatibility API.
- `Core/`: blueprint format, editing, diff, model, GLB, STL, and Blockbench export logic.
- `BuildwrightServer/`: server mod and world-operation protocol implementation.
- `Shared/`: data protocol shared by client and server.
- `Api/`: compatibility API for other mods.
- `assets/`: Buildwright-owned language resources.
- `tests/`: core, model, Blockbench, and release-package checks.

## License

Buildwright original source code is released under the MIT License, copyright VSCN-Studio. Client portions adapted from Fieldwright retain Jeffrey Lueken (Venah)'s MIT copyright and license notice; see `LICENSE-Fieldwright.txt`, `THANKS-FIELDWRIGHT.md`, and `ARCHITECTURE-PROVENANCE.md`.

Vintage Story, VSImGui, ImGui.NET, OpenTK, SkiaSharp, Cairo, and protobuf-net remain under their respective owners' licenses. This project does not relicense those dependencies. Blueprints, player creations, model geometry, and textures remain the property of their respective owners; exporting a format does not grant permission to redistribute or commercially use third-party assets.

See `LICENSE`, `COPYRIGHT.md`, `BuildwrightServer/COPYRIGHT.md`, and `THIRD-PARTY-NOTICES.md` for the complete notices.
