# Buildwright

Buildwright 是面向 Vintage Story 1.22.3 的蓝图、投影和建造进度模组。客户端提供蓝图管理、投影和模型导出，服务端组件负责需要服务器权限的世界操作。

当前开源版本：`1.0.3`

## 功能

- 选择区域并保存为 `.vsbp` 蓝图，保留方块、流体、方块实体、装饰和 Chisel/MicroBlock 数据。
- 蓝图库、文件浏览、备份、复制、模板、元数据和版本诊断。
- Ghost 投影支持移动、自动朝向、锁定、旋转、镜像、分层、裁剪、透明度和相对玩家移动。
- Full、Missing、Wrong、Difference 投影模式，以及 Loose、Medium、Strict 匹配。
- BlockEntity 和 Chisel 差异检测、错误高亮、材料需求和建造进度 HUD。
- Creative 单人模式下的确认式批量放置、补建、清除、复制、移动、旋转、镜像和填充。
- 导入和导出原生 Vintage Story JSON，并导出物品 Shape、Blockbench、Blender GLB 和 3D 打印 STL。
- Blockbench 导出保留 Chisel 几何、材质引用和 UV；非正方形凿刻面会同步处理纹理旋转与 UV 选区，避免 90 度旋转导致拉伸。
- 客户端兼容 API 和配套服务端协议，便于其他模组集成。

## 安装

客户端需要单独安装 VSImGui `1.2.7`。Release 构建会生成：

- `artifacts/buildwright-1.0.3.zip`：放入客户端 `Mods` 目录。
- `artifacts/buildwrightserver-1.0.3.zip`：放入服务器 `Mods` 目录；单人游戏也需要服务端组件。

不要把两个 ZIP 解压到同一个目录，也不要同时保留同一 modid 的旧包。

## 构建

需要 .NET 10 SDK、Vintage Story 1.22.3 客户端安装目录，以及 VSImGui 1.2.7 源目录。以下命令中的路径可以按本机安装位置调整：

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

Blockbench 插件兼容性检查需要本地 Vintage Story Blockbench 插件：

```powershell
$env:BUILDWRIGHT_BLOCKBENCH_FIXTURES = "$PWD/artifacts/blockbench-smoke"
dotnet run --project tests/Buildwright.CoreSmoke.csproj -c Release --no-restore `
  -p:VINTAGE_STORY=$env:VINTAGE_STORY
node tests/BlockbenchPluginSmoke.mjs E:/vintagestory/blockbenchplugin `
  $env:BUILDWRIGHT_BLOCKBENCH_FIXTURES $env:VINTAGE_STORY
```

编译输出位于 `bin/Release/Mods/`，Release ZIP 位于 `artifacts/`。项目不会把 Vintage Story、VSImGui 或其他外部依赖复制进源代码仓库。

## 目录

- `Client/`：客户端模组、界面、投影、蓝图导出和兼容 API。
- `Core/`：蓝图格式、编辑、差异、模型、GLB、STL 和 Blockbench 导出逻辑。
- `BuildwrightServer/`：服务端模组和世界操作协议实现。
- `Shared/`：客户端与服务端共享的数据协议。
- `Api/`：供其他模组调用的兼容 API。
- `assets/`：Buildwright 自有语言资源。
- `tests/`：核心、模型、Blockbench 和发布包检查。

## 许可

Buildwright 自有源代码按照 MIT License 发布，版权归 VSCN-Studio 所有。客户端中改编自 Fieldwright 的部分保留 Jeffrey Lueken（Venah）的 MIT 版权和许可声明，详见 `LICENSE-Fieldwright.txt`、`THANKS-FIELDWRIGHT.md` 和 `ARCHITECTURE-PROVENANCE.md`。

Vintage Story、VSImGui、ImGui.NET、OpenTK、SkiaSharp、Cairo 和 protobuf-net 由各自权利人按照其许可证提供，项目不重新授权这些依赖。蓝图、玩家作品、模型几何和贴图的权利仍归各自权利人；导出格式不会自动授予第三方素材的再分发或商业使用权。

完整版权文本见 `COPYRIGHT.md`、`BuildwrightServer/COPYRIGHT.md` 和 `THIRD-PARTY-NOTICES.md`。
