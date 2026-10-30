using Buildwright.Core;
using Buildwright.Client;
using Buildwright.Protocol;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static uint ChiselCuboid(int x0, int y0, int z0, int x1, int y1, int z1, int material) =>
    (uint)(x0 | (y0 << 4) | (z0 << 8) | ((x1 - 1) << 12) | ((y1 - 1) << 16) | ((z1 - 1) << 20) | (material << 24));

var root = Path.Combine(Path.GetTempPath(), "buildwright-core-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var codec = new BlueprintCodec();
    var document = new BlueprintDocument
    {
        Metadata = new BlueprintMetadata
        {
            Name = "smoke",
            Author = "Smoke Author",
            Dimensions = new Int3(3, 2, 4),
            AnchorOffset = new Int3(1, 0, 2),
            Capabilities = new List<string> { "schematic", "blockentities", "chisel-voxels" }
        },
        Schematic = JObject.Parse("{\"GameVersion\":\"1.22.3\",\"SizeX\":3,\"SizeY\":2,\"SizeZ\":4,\"BlockCodes\":{},\"ItemCodes\":{},\"Indices\":[],\"BlockIds\":[],\"BlockEntities\":{}}"),
        BlockEntities = new List<BlockEntityRecord> { new() { Position = new Int3(1, 0, 1), EntityClass = "Test", TreeAscii85 = "payload" } },
        ChiselData = new List<ChiselVoxelData> { new() { Position = new Int3(0, 0, 0), BlockIds = new[] { 1 }, VoxelCuboids = new uint[] { 1, 2 } } }
    };

    var vsbp = Path.Combine(root, "smoke.vsbp");
    codec.Write(vsbp, document);
    var loaded = codec.Read(vsbp);
    Assert(loaded.Metadata.Dimensions.X == 3 && loaded.Metadata.Dimensions.Z == 4, "VSBP metadata roundtrip failed");
    Assert(loaded.Metadata.Author == "Smoke Author",
        "VSBP author roundtrip failed");
    using (var archive = System.IO.Compression.ZipFile.OpenRead(vsbp))
    using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
        Assert(!reader.ReadToEnd().Contains("copyrightId", StringComparison.OrdinalIgnoreCase),
            "VSBP manifest must not contain an installation copyright identity");
    Assert(loaded.BlockEntities.Count == 1 && loaded.ChiselData.Count == 1, "VSBP optional entries roundtrip failed");

    var json = Path.Combine(root, "native.json");
    codec.Write(json, document);
    var native = codec.Read(json);
    Assert(native.Schematic["SizeY"]?.Value<int>() == 2, "Native schematic roundtrip failed");
    Assert(native.Metadata.Dimensions.X == 3 && native.Metadata.Dimensions.Z == 4, "Native metadata inference failed");
    Assert(native.Metadata.BlockCount == 0 && native.Metadata.MaterialCount == 0, "Native empty metadata inference failed");

    var exportDirectory = Path.Combine(root, "Exports");
    BlueprintShapeSmoke.Run(Path.Combine(root, "Models"));
    var nativeExport = BlueprintExporter.Export(exportDirectory, loaded, BlueprintFileKind.NativeJson);
    var duplicateExport = BlueprintExporter.Export(exportDirectory, loaded, BlueprintFileKind.NativeJson);
    Assert(Path.GetFileName(nativeExport) == "smoke.json" && Path.GetFileName(duplicateExport) == "smoke (2).json",
        "One-click exports must choose a name and preserve existing files");
    Assert(JToken.DeepEquals(JObject.Parse(File.ReadAllText(nativeExport)), loaded.Schematic),
        "Native export must contain the unwrapped game schematic");
    var vsbpExport = BlueprintExporter.Export(exportDirectory, loaded, BlueprintFileKind.BuildwrightZip);
    Assert(codec.Read(vsbpExport).ChiselData.Count == 1, "Library VSBP export must retain optional data");
    loaded.Metadata.Name = "子目录/中文蓝图";
    var unicodeExport = BlueprintExporter.Export(exportDirectory, loaded, BlueprintFileKind.NativeJson);
    Assert(Path.GetDirectoryName(unicodeExport) == exportDirectory && File.Exists(unicodeExport),
        "Export names must stay within the export folder and support Unicode");

    var repository = new BlueprintRepository(root, codec);
    repository.Save(document, "backup", BlueprintFileKind.BuildwrightZip);
    var savedSummary = repository.List().Single(item => item.Name == "backup");
    Assert(savedSummary.Author == "Smoke Author",
        "Blueprint library summary must retain author metadata");
    var changed = new BlueprintDocument { Metadata = document.Metadata, Schematic = JObject.Parse("{\"changed\":true}") };
    repository.Save(changed, "backup", BlueprintFileKind.BuildwrightZip, overwrite: true);
    Assert(File.Exists(repository.GetPath("backup") + ".bak"), "Backup was not created");
    repository.Restore("backup");
    Assert(codec.Read(repository.GetPath("backup")).Schematic["SizeX"]?.Value<int>() == 3, "Backup restore failed");

    var transformed = BlueprintTransforms.CellToWorld(new Int3(2, 1, 3), new Int3(1, 0, 1), new Int3(10, 20, 30), 1, MirrorAxis.None);
    Assert(transformed.X == 12 && transformed.Y == 21 && transformed.Z == 29, "Transform rotation failed");
    var transformedBounds = BlueprintTransforms.BoxToWorldBounds(new Int3(5, 3, 6), new Int3(4, 0, 5),
        new Int3(10, 20, 30), 1, MirrorAxis.None);
    Assert(transformedBounds.Min.X == 5 && transformedBounds.Max.X == 10 &&
           transformedBounds.Min.Y == 20 && transformedBounds.Max.Y == 22 &&
           transformedBounds.Min.Z == 30 && transformedBounds.Max.Z == 34,
        "Rotated move-preview bounds must preserve a non-minimum anchor");
    var chisel = new ChiselVoxelData { Position = new Int3(0, 0, 0), VoxelCuboids = new uint[] { 0x000ff000 } };
    var rotatedChisel = ChiselTransforms.Transform(chisel, 1, MirrorAxis.None);
    Assert(rotatedChisel.VoxelCuboids.Length == 1 && rotatedChisel.Rotation == 90, "Chisel transform failed");
    var canonicalChisel = new ChiselVoxelData
    {
        BlockIds = new[] { 101, 202 },
        VoxelCuboids = new[]
        {
            ChiselCuboid(0, 0, 0, 8, 16, 16, 0),
            ChiselCuboid(8, 0, 0, 16, 16, 16, 1)
        }
    };
    var equivalentChisel = new ChiselVoxelData
    {
        BlockIds = new[] { 202, 999, 101 },
        VoxelCuboids = new[]
        {
            ChiselCuboid(8, 0, 0, 16, 16, 16, 0),
            ChiselCuboid(0, 8, 0, 8, 16, 16, 2),
            ChiselCuboid(0, 0, 0, 8, 8, 16, 2)
        },
        OriginalVoxelCuboids = new[] { ChiselCuboid(0, 0, 0, 16, 16, 16, 0) }
    };
    Assert(ChiselVoxelComparer.HasSameVoxelMaterials(canonicalChisel, equivalentChisel),
        "Equivalent chisel voxels must ignore cuboid decomposition, ordering, and material table ordering");
    equivalentChisel.VoxelCuboids[0] = ChiselCuboid(8, 0, 0, 15, 16, 16, 0);
    Assert(!ChiselVoxelComparer.HasSameVoxelMaterials(canonicalChisel, equivalentChisel),
        "A genuine chisel voxel difference must fail strict comparison");
    var classifiedExpected = new ChiselVoxelData
    {
        BlockIds = new[] { 10, 20 },
        VoxelCuboids = new[]
        {
            ChiselCuboid(0, 0, 0, 1, 1, 1, 0),
            ChiselCuboid(1, 0, 0, 2, 1, 1, 1)
        }
    };
    var classifiedActual = new ChiselVoxelData
    {
        BlockIds = new[] { 20 },
        VoxelCuboids = new[]
        {
            ChiselCuboid(0, 0, 0, 1, 1, 1, 0),
            ChiselCuboid(2, 0, 0, 3, 1, 1, 0)
        }
    };
    var classifiedSnapshot = ChiselVoxelComparer.CreateSnapshot(classifiedExpected);
    var classifiedDiff = ChiselVoxelComparer.GetDifferences(classifiedSnapshot, classifiedActual);
    Assert(classifiedDiff.Count == 3
           && classifiedDiff.Count(item => item.Kind == ChiselVoxelDiffKind.Material) == 1
           && classifiedDiff.Count(item => item.Kind == ChiselVoxelDiffKind.Missing) == 1
           && classifiedDiff.Count(item => item.Kind == ChiselVoxelDiffKind.Extra) == 1,
        "Chisel differences must distinguish material, missing, and extra voxels");
    var diff = new DiffEngine().Compare(new[] { (new Int3(0, 0, 0), (string?)"game:stone") },
        new Dictionary<(int X, int Y, int Z), string?> { [(0, 0, 0)] = "game:stone" }, MatchingMode.Strict);
    Assert(diff.MissingCount == 0 && diff.WrongCount == 0 && diff.ExtraCount == 0, "Diff smoke failed");
    var mediumDiff = new DiffEngine().Compare(new[] { (new Int3(0, 0, 0), (string?)"game:stone-north") },
        new Dictionary<(int X, int Y, int Z), string?> { [(0, 0, 0)] = "game:stone-east" }, MatchingMode.Medium);
    Assert(mediumDiff.MatchedCount == 1, "Cached medium matching must still ignore orientation suffixes");
    var strictDirectionalDiff = new DiffEngine().Compare(new[] { (new Int3(0, 0, 0), (string?)"game:stone-north") },
        new Dictionary<(int X, int Y, int Z), string?> { [(0, 0, 0)] = "game:stone-east" }, MatchingMode.Strict);
    Assert(strictDirectionalDiff.WrongCount == 1, "Strict matching must still require the full block code");
    var lockedPosition = BlueprintTransforms.CellToWorld(new Int3(2, 0, 1), new Int3(1, 0, 1), new Int3(100, 64, 200), 1, MirrorAxis.None);
    var lockedDiff = new DiffEngine().Compare(new[] { (lockedPosition, (string?)"game:stone") },
        new Dictionary<(int X, int Y, int Z), string?> { [(lockedPosition.X, lockedPosition.Y, lockedPosition.Z)] = "game:stone" }, MatchingMode.Strict);
    Assert(lockedDiff.MatchedCount == 1 && lockedDiff.ExtraCount == 0, "Locked projection coordinates must compare in world space");
    var emptyCell = new DiffEngine().Compare(Array.Empty<(Int3 Position, string? ExpectedCode)>(),
        new Dictionary<(int X, int Y, int Z), string?> { [(1, 1, 1)] = null }, MatchingMode.Strict);
    Assert(emptyCell.ExtraCount == 0, "Empty blueprint cells must not count as extras");

    var requirements = MaterialCounter.CountRequired(new[] { "game:stone-north", "game:stone-south", "game:plank-oak" }, MatchingMode.Medium);
    MaterialCounter.MergeInventory(new[] { ("game:stone-east", 1), ("game:plank-oak", 3) }, requirements, MatchingMode.Medium);
    Assert(requirements.Single(x => x.Code == "stone").Completed == 1, "Material inventory matching failed");

    var safeName = BlueprintRepository.SanitizeName("  house:one?  ");
    Assert(!safeName.Contains(':') && !safeName.Contains('?'), "Blueprint filename sanitization failed");

    var terrainProjectionType = typeof(BlueprintCodec).Assembly.GetType("Buildwright.Client.BlueprintTerrainProjection")!;
    var requiresTerrainPipeline = terrainProjectionType.GetMethod("RequiresTerrainPipeline",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    var topSoil = new Block { DrawType = EnumDrawType.TopSoil, RenderPass = EnumChunkRenderPass.TopSoil };
    var seasonalPlant = new Block
    {
        DrawType = EnumDrawType.Cross,
        RenderPass = EnumChunkRenderPass.BlendNoCull,
        ShapeUsesColormap = true,
        Frostable = true
    };
    var liquid = new Block { DrawType = EnumDrawType.Liquid, RenderPass = EnumChunkRenderPass.Liquid };
    Assert(!(bool)requiresTerrainPipeline.Invoke(null, new object[] { topSoil })!
           && !(bool)requiresTerrainPipeline.Invoke(null, new object[] { seasonalPlant })!
           && (bool)requiresTerrainPipeline.Invoke(null, new object[] { liquid })!,
        "TopSoil and seasonal plants must remain in the alpha-aware per-layer mesh while liquids use the native pipeline");

    var clipToVisibleLayers = terrainProjectionType.GetMethod("ClipToVisibleLayers",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var nativeMesh = new MeshData(8, 12)
    {
        VerticesPerFace = 4,
        IndicesPerFace = 6,
        xyz = new float[]
        {
            0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 1, 1,
            0, 1, 0, 1, 1, 0, 1, 2, 1, 0, 2, 1
        },
        Indices = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 },
        CustomInts = new CustomMeshDataPartInt(8)
        {
            Count = 8,
            InterleaveSizes = new[] { 1 },
            InterleaveOffsets = new[] { 0 },
            InterleaveStride = 4
        }
    };
    nativeMesh.SetVerticesCount(8);
    nativeMesh.SetIndicesCount(12);
    var clippedNativeMesh = (MeshData)clipToVisibleLayers.Invoke(null, new object[] { nativeMesh, 0, 1 })!;
    Assert(ReferenceEquals(nativeMesh, clippedNativeMesh) && clippedNativeMesh.IndicesCount == 6
           && clippedNativeMesh.CustomInts.Count == 8,
        "Native layer clipping must preserve custom vertex buffers and filter only the index buffer");
    clippedNativeMesh.Dispose();

    var ghostMeshType = typeof(BlueprintCodec).Assembly.GetType("Buildwright.Client.BuildwrightGhostMesh")!;
    var normalizeCubeFace = ghostMeshType.GetMethod("NormalizeCubeFace",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    foreach (var facing in BlockFacing.ALLFACES)
    {
        var face = CubeMeshUtil.GetCubeFace(facing);
        normalizeCubeFace.Invoke(null, new object[] { face });
        for (var vertex = 0; vertex < face.VerticesCount; vertex++)
        for (var axis = 0; axis < 3; axis++)
            Assert(face.xyz[vertex * 3 + axis] is >= 0f and <= 1f,
                "TopSoil overlay faces must remain inside one block-local 0..1 cell");
        face.Dispose();
    }
    var shouldBuildTopSoilFace = ghostMeshType.GetMethod("ShouldBuildTopSoilFace",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var localCell = new Int3(0, 0, 0);
    var opaqueNorthNeighbor = new Dictionary<(int X, int Y, int Z), Block>
    {
        [(0, 0, -1)] = new Block()
    };
    Assert((bool)shouldBuildTopSoilFace.Invoke(null,
               new object[] { BlockFacing.UP, localCell, opaqueNorthNeighbor })!
           && !(bool)shouldBuildTopSoilFace.Invoke(null,
               new object[] { BlockFacing.DOWN, localCell, opaqueNorthNeighbor })!
           && !(bool)shouldBuildTopSoilFace.Invoke(null,
               new object[] { BlockFacing.NORTH, localCell, opaqueNorthNeighbor })!,
        "TopSoil coverage must render only on upward or horizontally exposed faces");
    var mapTopSoilV = ghostMeshType.GetMethod("MapTopSoilV",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Assert((float)mapTopSoilV.Invoke(null, new object[] { BlockFacing.NORTH, 0f })! == 1f
           && (float)mapTopSoilV.Invoke(null, new object[] { BlockFacing.NORTH, 1f })! == 0f
           && (float)mapTopSoilV.Invoke(null, new object[] { BlockFacing.UP, 0f })! == 0f,
        "TopSoil side coverage must flip V while the top face keeps its original orientation");
    var applyMappedFaceTints = ghostMeshType.GetMethod("ApplyMappedFaceTints",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var mappedMesh = new MeshData(8, 12).WithColorMaps();
    mappedMesh.SetVerticesCount(8);
    mappedMesh.VerticesPerFace = 4;
    mappedMesh.ColorMapIdsCount = 2;
    mappedMesh.ClimateColorMapIds[0] = 1;
    mappedMesh.ClimateColorMapIds[1] = 2;
    Array.Fill(mappedMesh.Rgba, byte.MaxValue);
    System.Func<byte, byte, byte[]?> faceTint = (climate, _) => climate == 1
        ? new byte[] { 200, 100, 50, 255 }
        : new byte[] { 20, 180, 240, 255 };
    applyMappedFaceTints.Invoke(null, new object[] { mappedMesh, faceTint });
    Assert(mappedMesh.Rgba[0] == 200 && mappedMesh.Rgba[1] == 100 && mappedMesh.Rgba[2] == 50
           && mappedMesh.Rgba[16] == 20 && mappedMesh.Rgba[17] == 180 && mappedMesh.Rgba[18] == 240,
        "Seasonal plant meshes must apply the color maps recorded on each shape face");
    mappedMesh.Dispose();

    var beamGuideType = typeof(BlueprintCodec).Assembly.GetType("Buildwright.Client.BuildwrightSupportBeamGuideMesh")!;
    var resolveBeamFace = beamGuideType.GetMethod("ResolveStartFace",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Assert(ReferenceEquals(resolveBeamFace.Invoke(null, new object[] { new Vec3f(0, 0.5f, 0.5f), BlockFacing.EAST.Index }),
               BlockFacing.WEST)
           && ReferenceEquals(resolveBeamFace.Invoke(null, new object[] { new Vec3f(1, 0.5f, 0.5f), BlockFacing.EAST.Index }),
               BlockFacing.EAST)
           && ReferenceEquals(resolveBeamFace.Invoke(null, new object[] { new Vec3f(0.5f), BlockFacing.UP.Index }),
               BlockFacing.UP),
        "Support beam guides must derive the actual host-cell face before falling back to FacingIndex");
    var inferBeamGrid = beamGuideType.GetMethod("InferGridSize",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Assert((int)inferBeamGrid.Invoke(null, new object[] { new Vec3f(0, 0.25f, 0.5f) })! == 4
           && (int)inferBeamGrid.Invoke(null, new object[] { new Vec3f(0, 0.0625f, 0.5f) })! == 16,
        "Support beam guide grid must infer quarter and sixteenth placement precision");
    var resolveEndFace = beamGuideType.GetMethod("ResolveEndFace",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Assert(ReferenceEquals(resolveEndFace.Invoke(null,
               new object[] { new Vec3f(0, 0.25f, 0.5f), new Vec3f(3, 1.25f, 0.5f) }), BlockFacing.WEST)
           && ReferenceEquals(resolveEndFace.Invoke(null,
               new object[] { new Vec3f(1, 0.5f, 0.5f), new Vec3f(-2, 0.5f, 0.5f) }), BlockFacing.EAST),
        "Support beam endpoint guides must face back toward the beam on the dominant snapped boundary");
    var intersectGuidePlane = beamGuideType.GetMethod("TryIntersectGuidePlane",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    object?[] rayArgs =
    {
        new Vec3f(-2, 0.37f, 0.62f), new Vec3f(1, 0, 0), new Int3(0, 0, 0),
        new Vec3f(0, 0.25f, 0.5f), BlockFacing.WEST, new Vec3f(), 0f
    };
    Assert((bool)intersectGuidePlane.Invoke(null, rayArgs)!,
        "Crosshair ray must intersect a visible support beam guide face");
    var guideIntersection = (Vec3f)rayArgs[5]!;
    var snapGuideGrid = beamGuideType.GetMethod("SnapToGuideGrid",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var snappedGuide = (Vec3f)snapGuideGrid.Invoke(null, new object[]
    {
        guideIntersection, new Int3(0, 0, 0), new Vec3f(0, 0.25f, 0.5f), BlockFacing.WEST, 4
    })!;
    Assert(MathF.Abs(snappedGuide.X) < 0.001f && MathF.Abs(snappedGuide.Y - 0.25f) < 0.001f
           && MathF.Abs(snappedGuide.Z - 0.5f) < 0.001f,
        "Crosshair guide highlighting must snap the hit to the placement grid on the selected face");
    var projectCursorHit = beamGuideType.GetMethod("TryProjectCursorHit",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    object?[] cursorArgs =
    {
        new Vec3f(-0.03f, 0.37f, 0.62f), new Int3(0, 0, 0), new Vec3f(0, 0.25f, 0.5f),
        BlockFacing.WEST, new Vec3f()
    };
    Assert((bool)projectCursorHit.Invoke(null, cursorArgs)!
           && MathF.Abs(((Vec3f)cursorArgs[4]!).X) < 0.001f,
        "A real block-selection hit near the guide face must override the fallback camera ray precisely");
    var resolveGameStart = beamGuideType.GetMethod("TryResolveGameStartPoint",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    object?[] solidStartArgs =
    {
        new BlockPos(10, 20, 30), new Vec3d(1, 0.26, 0.49), BlockFacing.EAST,
        false, 0, false, 6000, 4, new Vec3d()
    };
    Assert((bool)resolveGameStart.Invoke(null, solidStartArgs)!,
        "Support beam placement guide must accept a free adjacent host cell");
    var solidStart = (Vec3d)solidStartArgs[8]!;
    Assert(Math.Abs(solidStart.X - 11) < 0.001 && Math.Abs(solidStart.Y - 20.25) < 0.001
           && Math.Abs(solidStart.Z - 30.5) < 0.001,
        "Support beam guide must reproduce the game's adjacent-cell startPos and snapped startOffset");
    object?[] replaceableStartArgs =
    {
        new BlockPos(10, 20, 30), new Vec3d(0.31, 0.62, 1), BlockFacing.SOUTH,
        false, 6000, false, 0, 16, new Vec3d()
    };
    Assert((bool)resolveGameStart.Invoke(null, replaceableStartArgs)!,
        "Support beam placement guide must accept the selected replaceable host cell");
    var replaceableStart = (Vec3d)replaceableStartArgs[8]!;
    Assert(Math.Abs(replaceableStart.X - 10.3125) < 0.001 && Math.Abs(replaceableStart.Y - 20.625) < 0.001
           && Math.Abs(replaceableStart.Z - 31) < 0.001,
        "Support beam guide must use Ctrl's sixteenth-grid precision exactly");
    object?[] blockedStartArgs =
    {
        new BlockPos(10, 20, 30), new Vec3d(1, 0.5, 0.5), BlockFacing.EAST,
        false, 0, false, 0, 4, new Vec3d()
    };
    Assert(!(bool)resolveGameStart.Invoke(null, blockedStartArgs)!,
        "Support beam placement guide must reject the same blocked adjacent cell as the game");

    var beamTree = new Vintagestory.API.Datastructures.TreeAttribute();
    beamTree.SetBytes("beams", SerializerUtil.Serialize(new[]
    {
        new PlacedBeam
        {
            Start = new Vec3f(0, 0.25f, 0.5f),
            End = new Vec3f(3, 1.25f, 0.5f),
            FacingIndex = BlockFacing.EAST.Index
        }
    }));
    var captureType = typeof(BlueprintCodec).Assembly.GetType("Buildwright.Client.BlockEntityCapture")!;
    var encodeTree = captureType.GetMethod("EncodeTree",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    var beamDocument = new BlueprintDocument
    {
        BlockEntities = new List<BlockEntityRecord>
        {
            new()
            {
                Position = new Int3(2, 3, 4),
                TreeAscii85 = (string)encodeTree.Invoke(null, new object[] { beamTree })!
            }
        }
    };
    var readBeamGuides = beamGuideType.GetMethod("ReadGuides",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var parsedGuides = (System.Collections.ICollection)readBeamGuides.Invoke(null,
        new object?[] { beamDocument, null })!;
    Assert(parsedGuides.Count == 1, "Support beam guide extraction must decode the captured beams payload");
    var findGuide = beamGuideType.GetMethod("FindMatchingGuideIndex",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var guideList = parsedGuides;
    Assert((int)findGuide.Invoke(null, new object[]
        {
            guideList, new Vec3f(2, 3.25f, 4.5f), new Vec3f(5, 4.25f, 4.5f), 8
        })! == 0,
        "Support beam end-stage guide must select the blueprint beam from the game's locked start and end");
    var beamComparerType = typeof(BlueprintCodec).Assembly.GetType("Buildwright.Client.SupportBeamDataComparer")!;
    var compareBeams = beamComparerType.GetMethod("AreEquivalent",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var expectedBeamSet = new[]
    {
        new PlacedBeam { Start = new Vec3f(0, 0.25f, 0.5f), End = new Vec3f(3, 1.25f, 0.5f), BlockId = 1, FacingIndex = BlockFacing.EAST.Index },
        new PlacedBeam { Start = new Vec3f(0.5f, 0, 0.5f), End = new Vec3f(0.5f, 2, 0.5f), BlockId = 2, FacingIndex = BlockFacing.UP.Index }
    };
    var actualBeamSet = new[]
    {
        new PlacedBeam { Start = new Vec3f(0.5f, 0, 0.5f), End = new Vec3f(0.5f, 2.0005f, 0.5f), BlockId = 22, FacingIndex = BlockFacing.UP.Index },
        new PlacedBeam { Start = new Vec3f(0, 0.25f, 0.5f), End = new Vec3f(3.0005f, 1.25f, 0.5f), BlockId = 11, FacingIndex = BlockFacing.EAST.Index }
    };
    var codeById = new System.Func<int, string?>(id => id switch
    {
        1 or 11 => "game:oakbeam",
        2 or 22 => "game:oakbeam-thin",
        _ => null
    });
    Assert((bool)compareBeams.Invoke(null, new object[] { expectedBeamSet, actualBeamSet, codeById, codeById })!,
        "Support beam strict comparison must ignore array order, numeric id remapping, and tiny serialization drift");
    actualBeamSet[1].FacingIndex = BlockFacing.WEST.Index;
    Assert(!(bool)compareBeams.Invoke(null, new object[] { expectedBeamSet, actualBeamSet, codeById, codeById })!,
        "Support beam strict comparison must retain the placed start face");
    var transformBeamPoint = beamComparerType.GetMethod("TransformPoint",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var rotatedOnly = (Vec3f)transformBeamPoint.Invoke(null,
        new object[] { new Vec3f(0, 0.25f, 0.5f), 1, MirrorAxis.X })!;
    var mirroredOnly = (Vec3f)transformBeamPoint.Invoke(null,
        new object[] { new Vec3f(0, 0.25f, 0.5f), 0, MirrorAxis.X })!;
    var transformFacing = beamComparerType.GetMethod("TransformFacingIndex",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var rotatedFacing = (int)transformFacing.Invoke(null,
        new object[] { BlockFacing.EAST.Index, 1, MirrorAxis.None })!;
    Assert(MathF.Abs(rotatedOnly.X - 0.5f) < 0.001f && MathF.Abs(rotatedOnly.Z) < 0.001f
           && MathF.Abs(mirroredOnly.X - 1f) < 0.001f,
        "Support beam transforms must apply the projection mirror before rotation");
    Assert(rotatedFacing == BlockFacing.NORTH.Index,
        "Support beam facing must rotate with the transformed beam geometry");
    var exactPacket = new ExactSupportBeamPlacementRequest
    {
        RequestId = "exact-test",
        ProtocolVersion = 2,
        Dimension = 7,
        SchematicJson = "{\"SizeX\":1}",
        AnchorX = 10,
        AnchorY = 20,
        AnchorZ = 30,
        AnchorOffsetX = 1,
        AnchorOffsetY = 2,
        AnchorOffsetZ = 3,
        RotationQuarterTurns = 1,
        MirrorAxis = 2,
        OnlyMissing = true,
        ClearExtra = true
    };
    using (var packetStream = new MemoryStream())
    {
        ProtoBuf.Serializer.Serialize(packetStream, exactPacket);
        packetStream.Position = 0;
        var decodedPacket = ProtoBuf.Serializer.Deserialize<ExactSupportBeamPlacementRequest>(packetStream);
        Assert(decodedPacket.RequestId == exactPacket.RequestId && decodedPacket.Dimension == 7
               && decodedPacket.RotationQuarterTurns == 1 && decodedPacket.MirrorAxis == 2
               && decodedPacket.OnlyMissing && decodedPacket.ClearExtra,
            "Exact support beam protocol must preserve placement and transform fields");
    }

    var trackerType = typeof(BlueprintCodec).Assembly.GetType("Buildwright.Client.BuildwrightTracker")!;
    var isSpecialDataWrong = trackerType.GetMethod("IsSpecialDataWrong",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Assert((bool)isSpecialDataWrong.Invoke(null,
               new object[] { new DiffCell { Kind = DiffKind.Wrong, Detail = "BlockEntity data differs" } })!
           && !(bool)isSpecialDataWrong.Invoke(null,
               new object[] { new DiffCell { Kind = DiffKind.Wrong, ExpectedCode = "game:stone", ActualCode = "game:soil" } })!,
        "Strict entity-data mismatches must use outlines while real block-code errors retain filled highlights");

    Assert(BuildwrightToolModes.Count == 10, "Tool mode count must include the disabled pass-through mode");
    Assert((int)BuildwrightToolMode.Rebuild == 8 && (int)BuildwrightToolMode.Disabled == 9,
        "Adding tool modes must preserve persisted values for the original nine modes");
    Assert(!BuildwrightToolMode.Disabled.IsExecutable(), "Disabled tool mode must not execute an operation");
    var survivalModes = new List<BuildwrightToolMode>();
    var survivalMode = BuildwrightToolMode.AreaSelection;
    for (var i = 0; i < BuildwrightToolModes.Count; i++)
    {
        survivalModes.Add(survivalMode);
        survivalMode = survivalMode.Cycle(forward: true, creative: false);
    }
    Assert(!survivalModes.Any(mode => mode.IsCreativeOnly()), "Survival mode cycling must skip creative-only modes");
    Assert(survivalModes.Contains(BuildwrightToolMode.Disabled), "Disabled tool mode must be available in Survival");
    var creativeMode = BuildwrightToolMode.AreaSelection;
    for (var i = 0; i < BuildwrightToolModes.Count; i++) creativeMode = creativeMode.Cycle(forward: true, creative: true);
    Assert(creativeMode == BuildwrightToolMode.AreaSelection, "Creative tool mode cycle must wrap after ten modes");

    var selection = new SelectionState();
    selection.SetCorner1(new BlockPos(0, 0, 0), BlockFacing.NORTH);
    selection.SetCorner2(new BlockPos(2, 2, 2));
    Assert(selection.SelectedCorner == 2, "Setting the second corner must select it for adjustment");
    selection.NudgeSelectedCorner(BlockFacing.EAST, 1);
    Assert(selection.Min!.X == 0 && selection.Max!.X == 3 && selection.Anchor!.X == 0,
        "Nudging corner 2 must preserve corner 1 and expand the selection");
    selection.SelectCorner(1);
    selection.NudgeSelectedCorner(BlockFacing.WEST, 1);
    Assert(selection.Min!.X == -1 && selection.Max!.X == 3 && selection.Anchor!.X == -1,
        "Nudging corner 1 must preserve corner 2 and move the anchor");
    selection.Nudge(BlockFacing.UP, 2);
    Assert(selection.Min!.Y == 2 && selection.Max!.Y == 4 && selection.Anchor!.Y == 2,
        "Nudging the complete selection must include the anchor");
    var moveSelection = new SelectionState();
    moveSelection.SetCorner1(new BlockPos(4, 2, 6), BlockFacing.NORTH);
    moveSelection.SetCorner2(new BlockPos(0, 4, 1));
    moveSelection.MoveToAnchor(new BlockPos(10, 20, 30));
    Assert(moveSelection.Anchor!.X == 10 && moveSelection.Anchor.Y == 20 && moveSelection.Anchor.Z == 30,
        "Moving a selection must place its anchor at the requested target");
    Assert(moveSelection.Min!.X == 6 && moveSelection.Max!.X == 10 &&
           moveSelection.Min.Y == 20 && moveSelection.Max.Y == 22 &&
           moveSelection.Min.Z == 25 && moveSelection.Max.Z == 30,
        "Moving a selection must preserve its bounds relative to a non-minimum anchor");
    Assert(moveSelection.AnchorFace == BlockFacing.NORTH && moveSelection.SelectedCorner == 2,
        "Moving a selection must preserve its anchor face and selected corner");
    moveSelection.SetTransformedBounds(new BlockPos(10, 20, 30), BlockFacing.EAST,
        new BlockPos(5, 20, 30), new BlockPos(10, 22, 34));
    var transformedOpposite = moveSelection.GetCorner(2)!;
    Assert(transformedOpposite.X == 5 && transformedOpposite.Y == 22 && transformedOpposite.Z == 34,
        "A transformed move must keep the selection opposite corner across mixed min/max axes");
    Assert(moveSelection.AnchorFace == BlockFacing.EAST,
        "A transformed move must retain its transformed anchor facing");

    var smallHighlight = SelectionHighlightSampler.Build(new Int3(0, 0, 0), new Int3(9, 9, 9), 32768);
    Assert(smallHighlight.Count == 1000 && smallHighlight.Any(point => point.X == 5 && point.Y == 5 && point.Z == 5),
        "Small selections must retain full-volume highlighting");
    var largeHighlight = SelectionHighlightSampler.Build(new Int3(0, 0, 0), new Int3(199, 99, 199), 32768);
    Assert(largeHighlight.Count <= 32768, "Large selection highlighting exceeded its fixed budget");
    Assert(largeHighlight.Any(point => point.Z == 0 && point.X > 0 && point.X < 199 && point.Y > 0 && point.Y < 99),
        "Large selection highlighting must sample face interiors instead of only edges");
    Assert(largeHighlight.Any(point => point.X == 0 && point.Y == 0 && point.Z == 0) &&
           largeHighlight.Any(point => point.X == 199 && point.Y == 99 && point.Z == 199),
        "Large selection highlighting must retain all corners");

    repository.CreateDirectory("", "projects");
    repository.SavePath("projects/edit.vsbp", document, overwrite: false);
    Assert(repository.ListEntries("projects").Single().Summary?.RelativePath == "projects/edit.vsbp", "Folder listing failed");
    var copiedPath = repository.Copy("projects/edit.vsbp", "");
    Assert(copiedPath.EndsWith(".vsbp", StringComparison.OrdinalIgnoreCase) && File.Exists(repository.ResolveRelativePath(copiedPath)), "Library copy failed");
    var renamedPath = repository.Rename(copiedPath, "renamed");
    Assert(renamedPath == "renamed.vsbp", "Library rename failed");
    var escaped = false;
    try { repository.ResolveRelativePath("..\\outside.vsbp"); } catch (ArgumentException) { escaped = true; }
    Assert(escaped, "Repository allowed path traversal");

    var editable = new BlueprintDocument
    {
        Metadata = new BlueprintMetadata { Name = "edit", Dimensions = new Int3(3, 2, 3), AnchorOffset = new Int3(1, 0, 1) },
        Schematic = JObject.Parse("{\"SizeX\":3,\"SizeY\":2,\"SizeZ\":3,\"BlockCodes\":{\"1\":\"game:stone\",\"2\":\"game:oakplanks\"},\"Indices\":[0,1049601],\"BlockIds\":[1,2],\"DecorIndices\":[0],\"DecorIds\":[1],\"BlockEntities\":{\"0\":\"tree\"}}"),
        BlockEntities = new List<BlockEntityRecord> { new() { Position = new Int3(0, 0, 0), TreeAscii85 = "tree" } },
        ChiselData = new List<ChiselVoxelData> { new() { Position = new Int3(1, 1, 1), BlockIds = new[] { 1 }, VoxelCuboids = new uint[] { 3 } } }
    };
    BlueprintEditor.Extend(editable, BlueprintSide.West, 1);
    Assert(BlueprintEditor.Dimensions(editable).X == 4 && editable.Metadata.AnchorOffset.X == 2, "Negative-side extend did not move anchor");
    BlueprintEditor.Crop(editable, new Int3(1, 0, 0), new Int3(4, 2, 3));
    Assert(BlueprintEditor.Dimensions(editable).X == 3 && editable.BlockEntities.Single().Position.X == 0, "Crop did not move BlockEntity coordinates");
    Assert(BlueprintEditor.ReplaceMaterial(editable, "game:stone", "game:granite") == 1, "Material mapping replacement failed");
    var changedBlueprint = new BlueprintDocument { Metadata = new BlueprintMetadata { Dimensions = new Int3(3, 2, 3) }, Schematic = (JObject)editable.Schematic.DeepClone(), ChiselData = editable.ChiselData };
    ((JArray)changedBlueprint.Schematic["BlockIds"]!)[0] = 2;
    var blueprintDiff = BlueprintEditor.Diff(editable, changedBlueprint);
    Assert(blueprintDiff.ChangedCount == 1, "Blueprint-to-blueprint diff failed");

    Console.WriteLine("Buildwright core smoke tests passed.");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
