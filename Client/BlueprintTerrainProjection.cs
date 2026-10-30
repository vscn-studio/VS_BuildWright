using System.Reflection;
using Buildwright.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Buildwright.Client;

/// <summary>
/// Generates the terrain-only portion of a blueprint with the game's chunk
/// tesselator and submits it to the native chunk render pools. TopSoil and
/// climate-coloured foliage depend on this data contract and cannot be drawn
/// correctly by PreparedStandardShader.
/// </summary>
internal sealed class BlueprintTerrainProjection : IDisposable
{
    private const int ChunkSize = 32;
    private const int ExtChunkSize = 34;
    private const int ExtLayerSize = ExtChunkSize * ExtChunkSize;
    private const int ExtVolume = ExtChunkSize * ExtChunkSize * ExtChunkSize;
    private const int FullBright = -1;
    private static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly object TesselatorLock = new();
    private static ChunkTesselator? sharedTesselator;
    private static ClientMain? sharedGame;

    private static readonly FieldInfo? CoreGame = typeof(ClientCoreAPI).GetField("game", InstanceFields);
    private static readonly FieldInfo? MainChunkRenderer = typeof(ClientMain).GetField("chunkRenderer", InstanceFields);
    private static readonly FieldInfo? BlocksExt = typeof(ChunkTesselator).GetField("currentChunkBlocksExt", InstanceFields);
    private static readonly FieldInfo? FluidBlocksExt = typeof(ChunkTesselator).GetField("currentChunkFluidBlocksExt", InstanceFields);
    private static readonly FieldInfo? LightExt = typeof(ChunkTesselator).GetField("currentChunkRgbsExt", InstanceFields);
    private static readonly FieldInfo? DrawSolid = typeof(ChunkTesselator).GetField("currentChunkDraw32", InstanceFields);
    private static readonly FieldInfo? DrawFluid = typeof(ChunkTesselator).GetField("currentChunkDrawFluids", InstanceFields);
    private static readonly FieldInfo? ClimateMap = typeof(ChunkTesselator).GetField("currentClimateRegionMap", InstanceFields);
    private static readonly FieldInfo? RegionSize = typeof(ChunkTesselator).GetField("regionSize", InstanceFields);
    private static readonly FieldInfo? Vars = typeof(ChunkTesselator).GetField("vars", InstanceFields);
    private static readonly FieldInfo? CenterPools = typeof(ChunkTesselator).GetField("centerModeldataByRenderPassByLodLevel", InstanceFields);
    private static readonly FieldInfo? EdgePools = typeof(ChunkTesselator).GetField("edgeModeldataByRenderPassByLodLevel", InstanceFields);
    private static readonly MethodInfo? SetDimension = typeof(TCTCache).GetMethod("SetDimension", InstanceFields);

    private readonly ICoreClientAPI capi;
    private readonly ChunkRenderer renderer;
    private readonly List<TerrainPart> parts;
    private readonly List<ModelDataPoolLocation> locations = new();
    private BlockPos? lastOrigin;
    private int lastRotation = -1;
    private MirrorAxis lastMirror = (MirrorAxis)(-1);
    private int lastVisibleLayers = -1;

    public bool HasParts => parts.Count > 0;

    private BlueprintTerrainProjection(ICoreClientAPI capi, ChunkRenderer renderer, List<TerrainPart> parts)
    {
        this.capi = capi;
        this.renderer = renderer;
        this.parts = parts;
    }

    // Seasonal and TopSoil blocks are rebuilt per cell so the ghost owns their
    // opacity, hidden faces and exact layer visibility. Keep only meshes whose
    // fluid vertex contract cannot be represented by the standard ghost shader.
    public static bool RequiresTerrainPipeline(Block block) =>
        block.ForFluidsLayer || block.DrawType == EnumDrawType.Liquid;

    public static BlueprintTerrainProjection? TryCreate(ICoreClientAPI capi, BlockSchematic schematic,
        BlockPos environmentOrigin, System.Func<int, int, int, bool>? includeCell = null)
    {
        try
        {
            var game = CoreGame?.GetValue(capi) as ClientMain;
            var renderer = game == null ? null : MainChunkRenderer?.GetValue(game) as ChunkRenderer;
            if (game == null || renderer == null) return null;
            List<TerrainPart> parts;
            lock (TesselatorLock)
            {
                var tesselator = GetTesselator(game);
                parts = BuildParts(capi, tesselator, schematic, environmentOrigin, includeCell);
            }
            if (parts.Count == 0) return null;
            var passes = string.Join(", ", parts.GroupBy(part => part.Pass)
                .OrderBy(group => (int)group.Key).Select(group => $"{group.Key}={group.Count()}"));
            capi.Logger.Notification(
                "[buildwright] Native terrain projection created {0} mesh parts ({1}); calendar yearRel={2:0.000}, seasonOverride={3}.",
                parts.Count, passes, capi.World.Calendar.YearRel,
                capi.World.Calendar.SeasonOverride?.ToString("0.000") ?? "none");
            return new BlueprintTerrainProjection(capi, renderer, parts);
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[buildwright] Native terrain projection initialization failed: {0}", exception);
            return null;
        }
    }

    private static ChunkTesselator GetTesselator(ClientMain game)
    {
        if (sharedTesselator != null && ReferenceEquals(sharedGame, game)) return sharedTesselator;
        var tesselator = new ChunkTesselator(game);
        tesselator.LightlevelsReceived();
        tesselator.BlockTexturesLoaded();
        sharedGame = game;
        return sharedTesselator = tesselator;
    }

    private static List<TerrainPart> BuildParts(ICoreClientAPI capi, ChunkTesselator tesselator,
        BlockSchematic schematic, BlockPos environmentOrigin, System.Func<int, int, int, bool>? includeCell)
    {
        EnsureReflectionTargets();
        var air = capi.World.GetBlock(0);
        var cells = ReadCells(capi, schematic, includeCell);
        var blocks = (Block[])BlocksExt!.GetValue(tesselator)!;
        var fluids = (Block[])FluidBlocksExt!.GetValue(tesselator)!;
        var lights = (int[])LightExt!.GetValue(tesselator)!;
        var drawSolid = (byte[])DrawSolid!.GetValue(tesselator)!;
        var drawFluid = (byte[])DrawFluid!.GetValue(tesselator)!;
        var vars = (TCTCache)Vars!.GetValue(tesselator)!;
        vars.textureAtlasPositionsByTextureSubId = capi.BlockTextureAtlas.Positions;
        vars.aoAndSmoothShadows = false;
        vars.rainHeightMap = new ushort[ChunkSize * ChunkSize];
        var regionSize = (int)RegionSize!.GetValue(tesselator)!;
        ClimateMap!.SetValue(tesselator, BuildClimateMap(capi, environmentOrigin, regionSize));

        var parts = new List<TerrainPart>();
        var chunksX = Math.Max(1, (schematic.SizeX + ChunkSize - 1) / ChunkSize);
        var chunksY = Math.Max(1, (schematic.SizeY + ChunkSize - 1) / ChunkSize);
        var chunksZ = Math.Max(1, (schematic.SizeZ + ChunkSize - 1) / ChunkSize);
        for (var cy = 0; cy < chunksY; cy++)
        for (var cz = 0; cz < chunksZ; cz++)
        for (var cx = 0; cx < chunksX; cx++)
        {
            Array.Fill(blocks, air);
            Array.Fill(fluids, air);
            Array.Fill(lights, FullBright);
            Array.Clear(drawSolid);
            Array.Clear(drawFluid);
            FillExtendedArrays(cells, blocks, fluids, cx * ChunkSize, cy * ChunkSize, cz * ChunkSize, air);

            var baseX = FloorToChunk(environmentOrigin.X + cx * ChunkSize);
            var baseY = FloorToChunk(environmentOrigin.Y + cy * ChunkSize);
            var baseZ = FloorToChunk(environmentOrigin.Z + cz * ChunkSize);
            SetDimension!.Invoke(vars, new object[] { environmentOrigin.dimension });
            vars.xMin = vars.yMin = vars.zMin = ChunkSize;
            vars.xMax = vars.yMax = vars.zMax = 0;
            var any = tesselator.CalculateVisibleFaces_Fluids(false, baseX, baseY, baseZ)
                | tesselator.CalculateVisibleFaces(false, baseX, baseY, baseZ);
            if (!any) continue;
            SuppressStandardBlocks(blocks, fluids, drawSolid, drawFluid);
            if (!drawSolid.Any(value => value != 0) && !drawFluid.Any(value => value != 0)) continue;

            ClearPools(tesselator);
            tesselator.BuildBlockPolygons(baseX / ChunkSize,
                baseY / ChunkSize + environmentOrigin.dimension * 1024, baseZ / ChunkSize);
            CapturePools(tesselator, new Int3(cx * ChunkSize, cy * ChunkSize, cz * ChunkSize), parts);
        }
        return parts;
    }

    private static Dictionary<(int X, int Y, int Z), Cell> ReadCells(ICoreClientAPI capi,
        BlockSchematic schematic, System.Func<int, int, int, bool>? includeCell)
    {
        var cells = new Dictionary<(int X, int Y, int Z), Cell>();
        for (var index = 0; index < schematic.Indices.Count && index < schematic.BlockIds.Count; index++)
        {
            var packed = schematic.Indices[index];
            var x = (int)(packed & 0x3ff);
            var z = (int)((packed >> 10) & 0x3ff);
            var y = (int)((packed >> 20) & 0x3ff);
            if (includeCell != null && !includeCell(x, y, z)) continue;
            if (!schematic.BlockCodes.TryGetValue(schematic.BlockIds[index], out var code)) continue;
            var block = capi.World.GetBlock(code);
            if (block == null || block.Id == 0) continue;
            cells.TryGetValue((x, y, z), out var cell);
            if (block.ForFluidsLayer) cell.Fluid = block;
            else cell.Solid = block;
            cells[(x, y, z)] = cell;
        }
        return cells;
    }

    private static void FillExtendedArrays(IReadOnlyDictionary<(int X, int Y, int Z), Cell> cells,
        Block[] blocks, Block[] fluids, int offsetX, int offsetY, int offsetZ, Block air)
    {
        for (var y = 0; y < ExtChunkSize; y++)
        for (var z = 0; z < ExtChunkSize; z++)
        for (var x = 0; x < ExtChunkSize; x++)
        {
            var index = y * ExtLayerSize + z * ExtChunkSize + x;
            if (!cells.TryGetValue((offsetX + x - 1, offsetY + y - 1, offsetZ + z - 1), out var cell))
                continue;
            blocks[index] = cell.Solid ?? air;
            fluids[index] = cell.Fluid ?? air;
        }
    }

    private static void SuppressStandardBlocks(Block[] blocks, Block[] fluids, byte[] solidDraw, byte[] fluidDraw)
    {
        var index3d = 0;
        for (var y = 0; y < ChunkSize; y++)
        for (var z = 0; z < ChunkSize; z++)
        for (var x = 0; x < ChunkSize; x++, index3d++)
        {
            var ext = (y + 1) * ExtLayerSize + (z + 1) * ExtChunkSize + x + 1;
            if (!RequiresTerrainPipeline(blocks[ext])) solidDraw[index3d] = 0;
            if (!RequiresTerrainPipeline(fluids[ext])) fluidDraw[index3d] = 0;
        }
    }

    private static int[] BuildClimateMap(ICoreClientAPI capi, BlockPos origin, int regionSize)
    {
        var map = new int[Math.Max(1, regionSize * regionSize)];
        var temperature = 128;
        var rainfall = 128;
        try
        {
            var climate = capi.World.BlockAccessor.GetClimateAt(origin, EnumGetClimateMode.WorldGenValues, 0);
            temperature = Math.Clamp((int)MathF.Round((climate.Temperature + 20f) * Climate.TemperatureScaleConversion), 0, 255);
            rainfall = Math.Clamp((int)MathF.Round(climate.Rainfall * 255f), 0, 255);
        }
        catch { }
        Array.Fill(map, temperature << 16 | rainfall << 8);
        return map;
    }

    private static int FloorToChunk(int coordinate)
    {
        var quotient = coordinate / ChunkSize;
        if (coordinate < 0 && coordinate % ChunkSize != 0) quotient--;
        return quotient * ChunkSize;
    }

    private static void ClearPools(ChunkTesselator tesselator)
    {
        Clear((MeshData[][][])CenterPools!.GetValue(tesselator)!);
        Clear((MeshData[][][])EdgePools!.GetValue(tesselator)!);
        static void Clear(MeshData[][][] pools)
        {
            foreach (var lod in pools)
            foreach (var pass in lod)
            foreach (var mesh in pass) mesh.Clear();
        }
    }

    private static void CapturePools(ChunkTesselator tesselator, Int3 chunkOffset, ICollection<TerrainPart> output)
    {
        Capture((MeshData[][][])CenterPools!.GetValue(tesselator)!);
        Capture((MeshData[][][])EdgePools!.GetValue(tesselator)!);
        void Capture(MeshData[][][] pools)
        {
            for (var lod = 0; lod < pools.Length; lod++)
            for (var pass = 0; pass < pools[lod].Length; pass++)
            for (var atlas = 0; atlas < pools[lod][pass].Length; atlas++)
            {
                var source = pools[lod][pass][atlas];
                if (source.VerticesCount == 0) continue;
                var mesh = source.Clone();
                mesh.CompactBuffers();
                NormalizeInterleave(mesh, (EnumChunkRenderPass)pass);
                output.Add(new TerrainPart(chunkOffset.Copy(), (EnumChunkRenderPass)pass, atlas, lod, mesh));
            }
        }
    }

    private static void NormalizeInterleave(MeshData mesh, EnumChunkRenderPass pass)
    {
        if (mesh.CustomInts != null)
        {
            mesh.CustomInts.Conversion = DataConversion.Integer;
            mesh.CustomInts.InterleaveSizes = pass == EnumChunkRenderPass.Liquid ? new[] { 1, 1 } : new[] { 1 };
            mesh.CustomInts.InterleaveOffsets = pass == EnumChunkRenderPass.Liquid ? new[] { 0, 4 } : new[] { 0 };
            mesh.CustomInts.InterleaveStride = pass == EnumChunkRenderPass.Liquid ? 8 : 4;
        }
        if (mesh.CustomFloats != null && pass == EnumChunkRenderPass.Liquid)
        {
            mesh.CustomFloats.InterleaveSizes = new[] { 2 };
            mesh.CustomFloats.InterleaveOffsets = new[] { 0 };
            mesh.CustomFloats.InterleaveStride = 8;
        }
        if (mesh.CustomShorts != null && pass == EnumChunkRenderPass.TopSoil)
        {
            mesh.CustomShorts.Conversion = DataConversion.NormalizedFloat;
            mesh.CustomShorts.InterleaveSizes = new[] { 2 };
            mesh.CustomShorts.InterleaveOffsets = new[] { 0 };
            mesh.CustomShorts.InterleaveStride = 4;
        }
    }

    private static void EnsureReflectionTargets()
    {
        if (CoreGame == null || MainChunkRenderer == null || BlocksExt == null || FluidBlocksExt == null
            || LightExt == null || DrawSolid == null || DrawFluid == null || ClimateMap == null
            || RegionSize == null || Vars == null || CenterPools == null || EdgePools == null || SetDimension == null)
            throw new MissingMemberException("Vintage Story chunk tesselator fields are unavailable.");
    }

    public void Update(BlockPos origin, Int3 anchorOffset, int rotation, MirrorAxis mirror, int visibleLayers)
    {
        if (lastOrigin != null && lastOrigin.Equals(origin) && lastRotation == rotation && lastMirror == mirror
            && lastVisibleLayers == visibleLayers) return;
        RemoveLocations();
        var modelOrigin = new Vec3i(origin.X, origin.Y, origin.Z);
        var sphere = new Sphere(origin.X, origin.Y, origin.Z, 4096, 4096, 4096);
        foreach (var part in parts)
        {
            if (part.ChunkOffset.Y >= visibleLayers) continue;
            var pass = (int)part.Pass;
            if (pass < 0 || pass >= renderer.poolsByRenderPass.Length
                || part.Atlas < 0 || part.Atlas >= renderer.poolsByRenderPass[pass].Length) continue;
            var mesh = ClipToVisibleLayers(part.Mesh.Clone(), part.ChunkOffset.Y, visibleLayers);
            try
            {
                if (mesh.VerticesCount == 0) continue;
                Prepare(mesh, part.ChunkOffset, anchorOffset, rotation, mirror);
                var location = renderer.poolsByRenderPass[pass][part.Atlas]
                    .AddModel(mesh, modelOrigin, origin.dimension, sphere);
                if (location != null)
                {
                    location.LodLevel = part.Lod;
                    locations.Add(location);
                }
            }
            finally { mesh.Dispose(); }
        }
        lastOrigin = origin.Copy();
        lastRotation = rotation;
        lastMirror = mirror;
        lastVisibleLayers = visibleLayers;
    }

    private static MeshData ClipToVisibleLayers(MeshData source, int chunkOffsetY, int visibleLayers)
    {
        if (chunkOffsetY + ChunkSize <= visibleLayers) return source;
        if (source.Indices == null || source.xyz == null)
        {
            source.SetIndicesCount(0);
            return source;
        }

        var verticesPerFace = Math.Max(1, source.VerticesPerFace);
        var indicesPerFace = Math.Max(1, source.IndicesPerFace);
        var faceCount = Math.Min(source.VerticesCount / verticesPerFace,
            source.IndicesCount / indicesPerFace);
        var writeIndex = 0;
        for (var face = 0; face < faceCount; face++)
        {
            var first = face * verticesPerFace;
            var y = 0f;
            for (var vertex = first; vertex < first + verticesPerFace; vertex++)
                y += source.xyz[vertex * 3 + 1];
            if (chunkOffsetY + y / verticesPerFace >= visibleLayers + 0.01f) continue;
            Array.Copy(source.Indices, face * indicesPerFace, source.Indices, writeIndex, indicesPerFace);
            writeIndex += indicesPerFace;
        }
        source.SetIndicesCount(writeIndex);
        return source;
    }

    private static void Prepare(MeshData mesh, Int3 chunkOffset, Int3 anchor, int rotation, MirrorAxis mirror)
    {
        mesh.Translate(chunkOffset.X - anchor.X, chunkOffset.Y - anchor.Y, chunkOffset.Z - anchor.Z);
        var center = new Vec3f(0.5f, 0.5f, 0.5f);
        if (mirror != MirrorAxis.None)
        {
            mesh.Scale(center, mirror == MirrorAxis.X ? -1 : 1,
                mirror == MirrorAxis.Y ? -1 : 1, mirror == MirrorAxis.Z ? -1 : 1);
            ReverseWinding(mesh);
        }
        if (rotation != 0) mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, rotation * MathF.PI / 2f, 0);
    }

    private static void ReverseWinding(MeshData mesh)
    {
        if (mesh.mode != EnumDrawMode.Triangles || mesh.Indices == null) return;
        for (var index = 0; index + 2 < mesh.IndicesCount; index += 3)
            (mesh.Indices[index + 1], mesh.Indices[index + 2]) = (mesh.Indices[index + 2], mesh.Indices[index + 1]);
    }

    private void RemoveLocations()
    {
        if (locations.Count > 0) renderer.RemoveDataPoolLocations(locations.ToArray());
        locations.Clear();
        lastOrigin = null;
    }

    public void Dispose()
    {
        RemoveLocations();
        foreach (var part in parts) part.Mesh.Dispose();
        parts.Clear();
    }

    private struct Cell
    {
        public Block? Solid;
        public Block? Fluid;
    }

    private sealed record TerrainPart(Int3 ChunkOffset, EnumChunkRenderPass Pass, int Atlas, int Lod, MeshData Mesh);
}
