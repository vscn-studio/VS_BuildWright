using Buildwright.Core;
using Buildwright.Api;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Buildwright.Client;

internal sealed class BuildwrightGhostMesh : IDisposable
{
    private const uint Mask = 0x3ff;
    private readonly ICoreClientAPI capi;
    private readonly Dictionary<string, Block?> blockCache = new(StringComparer.OrdinalIgnoreCase);
    private MultiTextureMeshRef?[] layers;

    public int LayerCount => layers.Length;
    public int BlockCount { get; private set; }
    public int SkippedCount { get; private set; }
    public bool HasMesh => layers.Any(mesh => mesh?.Initialized == true);
    public MultiTextureMeshRef? GetLayer(int index) => index >= 0 && index < layers.Length ? layers[index] : null;

    public BuildwrightGhostMesh(ICoreClientAPI capi, BlockSchematic schematic, float alpha,
        System.Func<int, int, int, bool>? includeCell = null, IEnumerable<ChiselVoxelData>? chiselData = null,
        IReadOnlyDictionary<uint, string>? entityClasses = null,
        IReadOnlyDictionary<uint, IReadOnlyList<BuildwrightCompatPayload>>? compatData = null,
        System.Func<Block, bool>? excludeBlock = null, BlockPos? environmentOrigin = null)
        : this(capi, schematic, alpha, includeCell, chiselData, entityClasses, compatData, excludeBlock,
            environmentOrigin, uploadMeshes: true, captureLayer: null)
    {
    }

    private BuildwrightGhostMesh(ICoreClientAPI capi, BlockSchematic schematic, float alpha,
        System.Func<int, int, int, bool>? includeCell, IEnumerable<ChiselVoxelData>? chiselData,
        IReadOnlyDictionary<uint, string>? entityClasses,
        IReadOnlyDictionary<uint, IReadOnlyList<BuildwrightCompatPayload>>? compatData,
        System.Func<Block, bool>? excludeBlock,
        BlockPos? environmentOrigin,
        bool uploadMeshes, Action<MeshData>? captureLayer)
    {
        this.capi = capi;
        layers = new MultiTextureMeshRef?[Math.Max(1, schematic.SizeY)];
        var chiselByPosition = (chiselData ?? Array.Empty<ChiselVoxelData>())
            .GroupBy(data => RegionCapture.Pack(data.Position.X, data.Position.Y, data.Position.Z))
            .ToDictionary(group => group.Key, group => group.Last());
        Build(schematic, alpha, includeCell, chiselByPosition, entityClasses, compatData, excludeBlock, environmentOrigin,
            uploadMeshes, captureLayer);
    }

    public static MeshData BuildCpuMesh(ICoreClientAPI capi, BlockSchematic schematic,
        IEnumerable<ChiselVoxelData>? chiselData = null,
        IReadOnlyDictionary<uint, string>? entityClasses = null,
        IReadOnlyDictionary<uint, IReadOnlyList<BuildwrightCompatPayload>>? compatData = null)
    {
        var combined = new MeshData(1024, 2048).WithColorMaps().WithXyzFaces().WithRenderpasses();
        using var builder = new BuildwrightGhostMesh(capi, schematic, 1f, null, chiselData, entityClasses,
            compatData, excludeBlock: null, environmentOrigin: null, uploadMeshes: false,
            captureLayer: layer => combined.AddMeshData(layer));
        return combined;
    }

    private void Build(BlockSchematic schematic, float alpha, System.Func<int, int, int, bool>? includeCell,
        IReadOnlyDictionary<uint, ChiselVoxelData> chiselByPosition,
        IReadOnlyDictionary<uint, string>? entityClasses,
        IReadOnlyDictionary<uint, IReadOnlyList<BuildwrightCompatPayload>>? compatData,
        System.Func<Block, bool>? excludeBlock,
        BlockPos? environmentOrigin,
        bool uploadMeshes, Action<MeshData>? captureLayer)
    {
        var defaultMeshes = new Dictionary<int, MeshData?>();
        var chiselMeshes = new Dictionary<string, MeshData>(StringComparer.Ordinal);
        var perLayer = Enumerable.Range(0, Math.Max(1, schematic.SizeY))
            .Select(_ => new MeshData(256, 512)).ToArray();
        foreach (var mesh in perLayer)
        {
            mesh.WithColorMaps();
            mesh.WithXyzFaces();
            mesh.WithRenderpasses();
        }

        var alphaByte = (byte)Math.Clamp((int)(alpha * 255f), 0, 255);
        var suppressedMultiblockCells = FindStoneSlabPartCells(schematic);
        var solidBlocksByCell = ResolveSolidBlocks(schematic);
        for (var i = 0; i < schematic.Indices.Count && i < schematic.BlockIds.Count; i++)
        {
            var encoded = schematic.Indices[i];
            var x = (int)(encoded & Mask);
            var y = (int)((encoded >> 20) & Mask);
            var z = (int)((encoded >> 10) & Mask);
            if (includeCell != null && !includeCell(x, y, z)) continue;
            if (y < 0 || y >= perLayer.Length || !schematic.BlockCodes.TryGetValue(schematic.BlockIds[i], out var code))
            {
                SkippedCount++;
                continue;
            }

            if (suppressedMultiblockCells.Contains((x, y, z))
                && code.Domain.Equals("game", StringComparison.OrdinalIgnoreCase)
                && code.Path.StartsWith("multiblock-monolithic-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var block = ResolveExactBlock(code);
            if (block == null || block.Id == 0 || BlueprintBlockFilter.IsTransient(block))
            {
                SkippedCount++;
                continue;
            }

            var isChisel = chiselByPosition.TryGetValue(encoded, out var chisel);
            MeshData? cell = null;
            if (chisel != null)
            {
                var key = ChiselMeshKey(chisel);
                if (chiselMeshes.TryGetValue(key, out var cached)) cell = cached.Clone();
                else if (BlockEntityCapture.TryBuildChiselMesh(capi, schematic, chisel) is { } chiselMesh)
                {
                    chiselMeshes[key] = chiselMesh.Clone();
                    cell = chiselMesh;
                }
            }
            if (schematic.BlockEntities.TryGetValue(encoded, out var payload)
                && (cell == null || string.Equals(block.Code?.Domain, "vsroofing", StringComparison.OrdinalIgnoreCase)))
            {
                string? entityClass = null;
                entityClasses?.TryGetValue(encoded, out entityClass);
                IReadOnlyList<BuildwrightCompatPayload>? payloads = null;
                compatData?.TryGetValue(encoded, out payloads);
                var entityMesh = BlockEntityCapture.TryBuildMesh(capi, schematic, block, payload, entityClass, payloads);
                if (entityMesh != null)
                {
                    if (cell == null) cell = entityMesh;
                    else cell.AddMeshData(entityMesh);
                }
            }
            if (cell == null)
            {
                cell = GetDefaultMesh(block, defaultMeshes);
            }
            if (cell == null || cell.VerticesCount == 0)
            {
                SkippedCount++;
                continue;
            }
            if (excludeBlock?.Invoke(block) == true) continue;
            if (isChisel) BlockEntityCapture.PrepareChiselGhostMesh(cell);
            if (block.DrawType != EnumDrawType.TopSoil && block.RenderPass != EnumChunkRenderPass.TopSoil)
                ApplyEnvironmentTint(cell, block, encoded, schematic, environmentOrigin);
            if (BuildTopSoilOverlay(block, encoded, schematic, environmentOrigin, solidBlocksByCell) is { } overlay)
            {
                cell.AddMeshData(overlay);
                overlay.Dispose();
            }
            ApplyAlpha(cell, alphaByte);
            perLayer[y].AddMeshData(cell, x, y, z);
            BlockCount++;
        }

        for (var y = 0; y < perLayer.Length; y++)
        {
            if (perLayer[y].VerticesCount > 0)
            {
                captureLayer?.Invoke(perLayer[y]);
                if (uploadMeshes) layers[y] = capi.Render.UploadMultiTextureMesh(perLayer[y]);
                // Upload copies the vertex/index data to the GPU. Dispose the
                // CPU-side builder immediately to avoid large-blueprint leaks.
                perLayer[y].Dispose();
            }
            else perLayer[y].Dispose();
        }
    }

    private MeshData? GetDefaultMesh(Block block, IDictionary<int, MeshData?> cache)
    {
        if (!cache.TryGetValue(block.Id, out var baseMesh))
        {
            try { capi.Tesselator.TesselateBlock(block, out baseMesh); }
            catch
            {
                try { baseMesh = capi.TesselatorManager.GetDefaultBlockMesh(block); }
                catch { baseMesh = null; }
            }
            cache[block.Id] = baseMesh;
        }
        return baseMesh?.Clone();
    }

    private Block? ResolveExactBlock(AssetLocation code)
    {
        var key = code.ToString();
        if (blockCache.TryGetValue(key, out var cached)) return cached;
        var block = capi.World.Blocks.FirstOrDefault(candidate =>
            string.Equals(candidate.Code?.ToString(), code.ToString(), StringComparison.OrdinalIgnoreCase));
        blockCache[key] = block;
        return block;
    }

    private Dictionary<(int X, int Y, int Z), Block> ResolveSolidBlocks(BlockSchematic schematic)
    {
        var result = new Dictionary<(int X, int Y, int Z), Block>();
        for (var index = 0; index < schematic.Indices.Count && index < schematic.BlockIds.Count; index++)
        {
            if (!schematic.BlockCodes.TryGetValue(schematic.BlockIds[index], out var code)) continue;
            var block = ResolveExactBlock(code);
            if (block == null || block.Id == 0 || block.ForFluidsLayer) continue;
            var local = RegionCapture.Unpack(schematic.Indices[index]);
            result[(local.X, local.Y, local.Z)] = block;
        }
        return result;
    }

    private static HashSet<(int X, int Y, int Z)> FindStoneSlabPartCells(BlockSchematic schematic)
    {
        var cells = new HashSet<(int X, int Y, int Z)>();
        for (var i = 0; i < schematic.Indices.Count && i < schematic.BlockIds.Count; i++)
        {
            if (!schematic.BlockCodes.TryGetValue(schematic.BlockIds[i], out var code)
                || !code.Domain.Equals("stonequarry", StringComparison.OrdinalIgnoreCase)
                || !code.Path.StartsWith("stoneslab-", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = code.Path.Split('-');
            if (parts.Length < 3) continue;
            var size = parts[1].ToLowerInvariant() switch
            {
                "medium" => (X: 2, Y: 1, Z: 1),
                "large" => (X: 2, Y: 1, Z: 2),
                "huge" => (X: 2, Y: 2, Z: 2),
                "giant" => (X: 2, Y: 3, Z: 2),
                _ => (X: 1, Y: 1, Z: 1)
            };
            var side = parts[^1].ToLowerInvariant();
            var encoded = schematic.Indices[i];
            var x = (int)(encoded & Mask);
            var y = (int)((encoded >> 20) & Mask);
            var z = (int)((encoded >> 10) & Mask);
            var anchorX = side is "south" or "west" ? x - 1 : x;
            var anchorZ = side is "north" or "west" ? z - 1 : z;
            for (var dx = 0; dx < size.X; dx++)
            for (var dy = 0; dy < size.Y; dy++)
            for (var dz = 0; dz < size.Z; dz++)
                cells.Add((anchorX + dx, y + dy, anchorZ + dz));

            // Some StoneQuarry versions serialize the child cells with a
            // different cposition than the current blocktype definition.
            // Include the full local footprint around the recorded owner so
            // stale synthetic children cannot leak into the ghost mesh.
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = 0; dy < size.Y; dy++)
            for (var dz = -1; dz <= 1; dz++)
                cells.Add((x + dx, y + dy, z + dz));
        }
        return cells;
    }

    private static string ChiselMeshKey(ChiselVoxelData data) =>
        string.Join(',', data.BlockIds) + "/" + string.Join(',', data.VoxelCuboids) + "/" +
        string.Join(',', data.OriginalVoxelCuboids) + "/" + string.Join(',', data.DecorIds ?? Array.Empty<int>()) + "/" + data.DecorRotations;

    private static void ApplyAlpha(MeshData mesh, byte alpha)
    {
        if (mesh.Rgba == null) return;
        for (var i = 3; i < mesh.Rgba.Length; i += 4)
            mesh.Rgba[i] = (byte)(mesh.Rgba[i] * alpha / 255);
    }

    private void ApplyEnvironmentTint(MeshData mesh, Block block, uint packed, BlockSchematic schematic,
        BlockPos? environmentOrigin)
    {
        if (mesh.Rgba == null || mesh.ColorMapIdsCount <= 0) return;
        var local = RegionCapture.Unpack(packed);
        var origin = environmentOrigin ?? capi.World.Player?.Entity?.Pos?.AsBlockPos
            ?? schematic.OriginalPos ?? new BlockPos(0);
        var position = new BlockPos(origin.X + local.X, origin.Y + local.Y, origin.Z + local.Z, origin.dimension);
        var game = capi.World as ClientMain;
        var cache = new Dictionary<(byte Climate, byte Season), byte[]?>();
        ApplyMappedFaceTints(mesh, (climateId, seasonId) =>
        {
            var key = (climateId, seasonId);
            if (cache.TryGetValue(key, out var cached)) return cached;
            ColorMap? climateMap = null;
            ColorMap? seasonMap = null;
            if (game != null)
            {
                climateMap = ResolveColorMap(game, climateId, season: false);
                seasonMap = ResolveColorMap(game, seasonId, season: true);
            }
            else
            {
                if (climateId != 0) climateMap = block.ClimateColorMapResolved;
                if ((seasonId & 0x3f) != 0) seasonMap = block.SeasonColorMapResolved;
            }

            try
            {
                var tint = capi.World.ApplyColorMapOnRgba(climateMap, seasonMap,
                    ColorUtil.ToRgba(255, 255, 255, 255), position.X, position.Y, position.Z, false);
                var channels = ColorUtil.ToBGRABytes(tint);
                cached = channels[3] == 0 || channels[0] + channels[1] + channels[2] == 0 ? null : channels;
            }
            catch { cached = null; }
            cache[key] = cached;
            return cached;
        });
    }

    private static ColorMap? ResolveColorMap(ClientMain game, byte encodedId, bool season)
    {
        var index = (encodedId & (season ? 0x3f : 0x0f)) - 1;
        return index >= 0 && index < game.ColorMaps.Count ? game.ColorMaps.GetValueAtIndex(index) : null;
    }

    private static void ApplyMappedFaceTints(MeshData mesh, System.Func<byte, byte, byte[]?> resolveTint)
    {
        if (mesh.Rgba == null) return;
        var verticesPerFace = Math.Max(1, mesh.VerticesPerFace);
        var faceCount = Math.Min(mesh.ColorMapIdsCount, mesh.VerticesCount / verticesPerFace);
        for (var face = 0; face < faceCount; face++)
        {
            var climateId = mesh.ClimateColorMapIds?[face] ?? 0;
            var seasonId = mesh.SeasonColorMapIds?[face] ?? 0;
            if (climateId == 0 && (seasonId & 0x3f) == 0) continue;
            var channels = resolveTint(climateId, seasonId);
            if (channels == null || channels.Length < 3) continue;
            var firstVertex = face * verticesPerFace;
            for (var vertex = firstVertex; vertex < firstVertex + verticesPerFace && vertex < mesh.VerticesCount; vertex++)
            {
                var color = vertex * 4;
                mesh.Rgba[color] = (byte)(mesh.Rgba[color] * channels[0] / 255);
                mesh.Rgba[color + 1] = (byte)(mesh.Rgba[color + 1] * channels[1] / 255);
                mesh.Rgba[color + 2] = (byte)(mesh.Rgba[color + 2] * channels[2] / 255);
            }
        }
    }

    private MeshData? BuildTopSoilOverlay(Block block, uint packed, BlockSchematic schematic,
        BlockPos? environmentOrigin, IReadOnlyDictionary<(int X, int Y, int Z), Block> solidBlocksByCell)
    {
        if (block.DrawType != EnumDrawType.TopSoil && block.RenderPass != EnumChunkRenderPass.TopSoil) return null;
        if (block.Textures == null
            || !block.Textures.TryGetValue("specialSecondTexture", out var texture)
            || texture?.Baked == null) return null;
        var subId = texture.Baked.TextureSubId;
        if (subId < 0 || subId >= capi.BlockTextureAtlas.Positions.Length) return null;
        var atlas = capi.BlockTextureAtlas.Positions[subId];
        if (atlas == null || atlas.atlasTextureId < 0) return null;

        var local = RegionCapture.Unpack(packed);
        var origin = environmentOrigin ?? capi.World.Player?.Entity?.Pos?.AsBlockPos
            ?? schematic.OriginalPos ?? new BlockPos(0);
        byte[] tint;
        try
        {
            tint = ColorUtil.ToBGRABytes(capi.World.ApplyColorMapOnRgba(
                block.ClimateColorMapResolved, block.SeasonColorMapResolved,
                ColorUtil.ToRgba(255, 255, 255, 255), origin.X + local.X, origin.Y + local.Y,
                origin.Z + local.Z, false));
        }
        catch { tint = new byte[] { 255, 255, 255, 255 }; }
        if (tint[3] == 0 || tint[0] + tint[1] + tint[2] == 0)
            tint = new byte[] { 255, 255, 255, 255 };

        var mesh = new MeshData(20, 30).WithXyzFaces().WithRenderpasses();
        var middleX = (atlas.x1 + atlas.x2) * 0.5f;
        foreach (var facing in BlockFacing.ALLFACES)
        {
            if (!ShouldBuildTopSoilFace(facing, local, solidBlocksByCell)) continue;
            var face = CubeMeshUtil.GetCubeFace(facing);
            NormalizeCubeFace(face);
            var u1 = facing == BlockFacing.UP ? middleX : atlas.x1;
            var u2 = facing == BlockFacing.UP ? atlas.x2 : middleX;
            for (var vertex = 0; vertex < face.VerticesCount; vertex++)
            {
                face.Uv[vertex * 2] = u1 + (u2 - u1) * face.Uv[vertex * 2];
                var v = MapTopSoilV(facing, face.Uv[vertex * 2 + 1]);
                face.Uv[vertex * 2 + 1] = atlas.y1 + (atlas.y2 - atlas.y1) * v;
                face.Rgba[vertex * 4] = tint[0];
                face.Rgba[vertex * 4 + 1] = tint[1];
                face.Rgba[vertex * 4 + 2] = tint[2];
                face.Rgba[vertex * 4 + 3] = byte.MaxValue;
            }
            face.Flags = Enumerable.Repeat(VertexFlags.PackNormal(facing.Normali), face.VerticesCount).ToArray();
            face.TextureIds = new[] { atlas.atlasTextureId };
            face.TextureIndices = new byte[] { 0 };
            face.TextureIndicesCount = 1;
            face.VerticesPerFace = 4;
            face.IndicesPerFace = 6;
            face.Translate(facing.Normali.X * 0.0005f, facing.Normali.Y * 0.0005f,
                facing.Normali.Z * 0.0005f);
            mesh.AddMeshData(face);
            face.Dispose();
        }
        return mesh.VerticesCount > 0 ? mesh : null;
    }

    private static bool ShouldBuildTopSoilFace(BlockFacing facing, Int3 local,
        IReadOnlyDictionary<(int X, int Y, int Z), Block> solidBlocksByCell)
    {
        if (facing == BlockFacing.DOWN) return false;
        var normal = facing.Normali;
        if (!solidBlocksByCell.TryGetValue((local.X + normal.X, local.Y + normal.Y, local.Z + normal.Z),
                out var neighbor)) return true;
        return !neighbor.SideOpaque[facing.Opposite.Index];
    }

    private static float MapTopSoilV(BlockFacing facing, float v) => facing.IsHorizontal ? 1f - v : v;

    private static void NormalizeCubeFace(MeshData face)
    {
        for (var vertex = 0; vertex < face.VerticesCount; vertex++)
        {
            var xyz = vertex * 3;
            face.xyz[xyz] = face.xyz[xyz] * 0.5f + 0.5f;
            face.xyz[xyz + 1] = face.xyz[xyz + 1] * 0.5f + 0.5f;
            face.xyz[xyz + 2] = face.xyz[xyz + 2] * 0.5f + 0.5f;
        }
    }

    public void Dispose()
    {
        foreach (var mesh in layers) mesh?.Dispose();
        layers = Array.Empty<MultiTextureMeshRef?>();
    }
}
