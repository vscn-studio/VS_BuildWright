using System.Reflection;
using System.IO;
using Buildwright.Core;
using Buildwright.Api;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Buildwright.Client;

internal static class BlockEntityCapture
{
    private static readonly Vec3f BlockCenter = new(0.5f, 0.5f, 0.5f);
    private static readonly FieldInfo? MicroBlockRotationField = typeof(BlockEntityMicroBlock)
        .GetField("rotationY", BindingFlags.Instance | BindingFlags.NonPublic);

    public static BlockEntityRecord? CaptureRecord(BlockEntity entity, Int3 position, BlockSchematic schematic)
    {
        try
        {
            var tree = new TreeAttribute();
            entity.ToTreeAttributes(tree);
            var payload = Encode(tree);
            return new BlockEntityRecord
            {
                Position = position,
                EntityClass = entity.GetType().FullName,
                TreeAscii85 = payload,
                Orientation = tree.GetString("meshAngle", null)
            };
        }
        catch { return null; }
    }

    public static ChiselVoxelData? TryCaptureChisel(BlockEntity entity, Int3 position,
        bool includeOriginalVoxelCuboids = true)
    {
        if (entity is BlockEntityMicroBlock microBlock)
        {
            return new ChiselVoxelData
            {
                Position = position,
                BlockIds = microBlock.BlockIds?.ToArray() ?? Array.Empty<int>(),
                VoxelCuboids = microBlock.VoxelCuboids?.ToArray() ?? Array.Empty<uint>(),
                OriginalVoxelCuboids = includeOriginalVoxelCuboids
                    ? microBlock.OriginalVoxelCuboids?.ToArray() ?? Array.Empty<uint>()
                    : Array.Empty<uint>(),
                DecorIds = microBlock.DecorIds?.ToArray(),
                DecorRotations = microBlock.DecorRotations,
                Rotation = ReadRotation(entity)
            };
        }

        var type = entity.GetType();
        if (!type.Name.Contains("MicroBlock", StringComparison.OrdinalIgnoreCase)
            && !type.Name.Contains("Chisel", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var blockIds = ReadIntArray(type, entity, "BlockIds") ?? Array.Empty<int>();
            var cuboids = ReadUIntList(type, entity, "VoxelCuboids") ?? Array.Empty<uint>();
            if (blockIds.Length == 0 && cuboids.Length == 0) return null;
            return new ChiselVoxelData
            {
                Position = position,
                BlockIds = blockIds,
                VoxelCuboids = cuboids,
                OriginalVoxelCuboids = includeOriginalVoxelCuboids
                    ? ReadUIntArray(type, entity, "OriginalVoxelCuboids") ?? Array.Empty<uint>()
                    : Array.Empty<uint>(),
                DecorIds = ReadIntArray(type, entity, "DecorIds"),
                DecorRotations = ReadInt(type, entity, "DecorRotations"),
                Rotation = ReadRotation(entity)
            };
        }
        catch { return null; }
    }

    public static bool ChiselEquals(ChiselVoxelData expected, BlockEntity entity)
    {
        var actual = TryCaptureChisel(entity, expected.Position, includeOriginalVoxelCuboids: false);
        return actual != null && ChiselEquals(expected, actual);
    }

    public static bool ChiselEquals(ChiselVoxelData expected, ChiselVoxelData actual)
    {
        return ChiselMetadataEquals(expected, actual)
            && ChiselVoxelComparer.HasSameVoxelMaterials(expected, actual);
    }

    public static bool ChiselMetadataEquals(ChiselVoxelData expected, ChiselVoxelData actual) =>
        expected.Rotation == actual.Rotation
        && expected.DecorRotations == actual.DecorRotations
        && (expected.DecorIds ?? Array.Empty<int>()).SequenceEqual(actual.DecorIds ?? Array.Empty<int>());

    public static ITreeAttribute? Decode(string ascii85)
    {
        try
        {
            var data = Ascii85.Decode(ascii85);
            var tree = new TreeAttribute();
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);
            tree.FromBytes(reader);
            return tree;
        }
        catch { return null; }
    }

    public static string EncodeTree(ITreeAttribute tree)
    {
        return Encode(tree);
    }

    public static string TransformPayload(ICoreClientAPI capi, BlockSchematic schematic, uint packed,
        string payload, int rotation, MirrorAxis mirror)
    {
        if (rotation == 0 && mirror == MirrorAxis.None) return payload;
        try
        {
            var index = schematic.Indices.IndexOf(packed);
            if (index < 0 || index >= schematic.BlockIds.Count || !schematic.BlockCodes.TryGetValue(schematic.BlockIds[index], out var code)) return payload;
            var block = capi.World.GetBlock(code);
            if (block == null || string.IsNullOrWhiteSpace(block.EntityClass)) return payload;
            var entity = capi.World.ClassRegistry.CreateBlockEntity(block.EntityClass);
            var tree = Decode(payload);
            if (tree == null) return payload;
            var transformedBlock = block;
            if (mirror == MirrorAxis.Y) transformedBlock = capi.World.GetBlock(transformedBlock.GetVerticallyFlippedBlockCode()) ?? transformedBlock;
            if (mirror == MirrorAxis.X) transformedBlock = capi.World.GetBlock(transformedBlock.GetHorizontallyFlippedBlockCode(EnumAxis.X)) ?? transformedBlock;
            if (mirror == MirrorAxis.Z) transformedBlock = capi.World.GetBlock(transformedBlock.GetHorizontallyFlippedBlockCode(EnumAxis.Z)) ?? transformedBlock;
            if (rotation != 0) transformedBlock = capi.World.GetBlock(transformedBlock.GetRotatedBlockCode(((rotation % 4) + 4) % 4 * 90)) ?? transformedBlock;
            var transformedSupportBeams = SupportBeamDataComparer.TransformInPlace(tree, rotation, mirror);
            if (!transformedSupportBeams)
            {
                if (entity is not IRotatable rotatable) return payload;
                entity.Pos = new BlockPos(0, 1, 0);
                entity.CreateBehaviors(transformedBlock, capi.World);
                var axis = mirror switch
                {
                    MirrorAxis.X => EnumAxis.X,
                    MirrorAxis.Y => EnumAxis.Y,
                    MirrorAxis.Z => EnumAxis.Z,
                    _ => (EnumAxis?)null
                };
                rotatable.OnTransformed(capi.World, tree, ((rotation % 4) + 4) % 4 * 90,
                    schematic.BlockCodes, schematic.ItemCodes ?? new Dictionary<int, AssetLocation>(), axis);
            }
            tree.SetString("blockCode", transformedBlock.Code.ToShortString());
            return Encode(tree);
        }
        catch (Exception ex)
        {
            capi.Logger.Debug("[buildwright] BlockEntity transform failed: {0}", ex.Message);
            return payload;
        }
    }

    public static MeshData? TryBuildMesh(ICoreClientAPI capi, BlockSchematic schematic, Block block, string? ascii85,
        string? entityClass = null, IEnumerable<BuildwrightCompatPayload>? compatData = null)
    {
        try
        {
            var compatMesh = BuildwrightCompatApi.BuildGhostMesh(capi, block, compatData);
            if (compatMesh != null) return compatMesh;
            if (string.IsNullOrWhiteSpace(block.EntityClass) || string.IsNullOrWhiteSpace(ascii85)) return null;
            var tree = Decode(ascii85);
            if (tree == null) return null;
            // Chest variants encode their cardinal direction in the block code.
            // A temporary GenericTypedContainer only builds its neutral cached mesh,
            // so using it here loses that direction even though the block comparison
            // remains correct. The normal block mesh already contains the variant;
            // meshAngle is the entity-only fine rotation applied by vanilla later.
            if (UsesBlockOrientedMesh(block))
            {
                var orientedMesh = BuildTessellatedMesh(capi, block, tree);
                if (orientedMesh != null) return orientedMesh;
            }

            var shapeMesh = TryBuildShapeAttributeMesh(capi, block, tree);
            if (shapeMesh != null) return shapeMesh;
            var windmillMesh = TryBuildWindmillMesh(capi, block, tree);
            if (windmillMesh != null) return windmillMesh;

            var entity = CreateBlockEntity(capi, block.EntityClass, entityClass);
            if (entity == null) return null;
            entity.Pos = new BlockPos(0, 1, 0);
            typeof(BlockEntity).GetProperty("Block", BindingFlags.Instance | BindingFlags.Public)?.SetValue(entity, block);
            entity.CreateBehaviors(block, capi.World);

            InvokeFromTree(entity, tree, capi.World);
            // Microblock entity trees retain numeric material IDs. Resolve them
            // through the blueprint's Block Code table before mesh generation.
            entity.OnLoadCollectibleMappings(capi.World, schematic.BlockCodes,
                schematic.ItemCodes ?? new Dictionary<int, AssetLocation>(), 0, true);
            entity.Initialize(capi);

            var stoneSlabMesh = TryBuildStoneSlabMesh(capi, block, entity);
            if (stoneSlabMesh != null) return stoneSlabMesh;
            var dynamicCompositeMesh = TryBuildDynamicCompositeMesh(capi, block, entity);
            if (dynamicCompositeMesh != null) return dynamicCompositeMesh;
            var supportBeamMesh = TryBuildSupportBeamMesh(entity);
            if (supportBeamMesh != null) return supportBeamMesh;
            var direct = TryBuildEntityMesh(capi, entity);
            if (direct != null) return direct.Clone();
            var mesh = FindMesh(entity);
            return mesh?.VerticesCount > 0 ? mesh.Clone() : null;
        }
        catch (Exception ex)
        {
            capi.Logger.Debug("[buildwright] special mesh failed for {0}: {1}", block.Code, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// StoneQuarry stores the slab's constituent rocks in the block entity and
    /// generates its complete multi-block mesh through StoneSlabMeshCache. The
    /// ordinary block mesh only contains the placeholder shape, so delegate to
    /// the loaded mod when it is available.
    /// </summary>
    private static MeshData? TryBuildStoneSlabMesh(ICoreClientAPI capi, Block block, BlockEntity entity)
    {
        if (!IsStoneQuarrySlab(block)) return null;
        try
        {
            var cache = capi.ModLoader.GetModSystem("StoneQuarry.StoneSlabMeshCache");
            var method = cache?.GetType().GetMethod("GetMesh", BindingFlags.Instance | BindingFlags.Public,
                binder: null, types: new[] { entity.GetType(), typeof(ITesselatorAPI) }, modifiers: null);
            if (method?.Invoke(cache, new object[] { entity, capi.Tesselator }) is MeshData mesh && mesh.VerticesCount > 0)
                return mesh.Clone();
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("[buildwright] StoneQuarry slab mesh failed for {0}: {1}", block.Code, exception.Message);
        }
        return null;
    }

    /// <summary>
    /// Some modded block entities assemble several meshes only from their terrain
    /// tesselation callback. VS Roofing is one such case; its ordinary block model
    /// is deliberately just a question-mark placeholder.
    /// </summary>
    private static MeshData? TryBuildDynamicCompositeMesh(ICoreClientAPI capi, Block block, BlockEntity entity)
    {
        if (!string.Equals(block.Code?.Domain, "vsroofing", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.GetType().Name, "AutoRoofEntity", StringComparison.Ordinal)) return null;

        MeshData? combined = null;
        foreach (var methodName in new[] { "GenerateRoofMesh", "GenerateFenceConnectMesh", "GeneratePlantMesh" })
        {
            var method = entity.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(ITesselatorAPI) }, modifiers: null);
            if (method?.Invoke(entity, new object[] { capi.Tesselator }) is not MeshData { VerticesCount: > 0 } part)
                continue;

            if (combined == null) combined = part.Clone();
            else combined.AddMeshData(part);
        }
        return combined;
    }

    private static MeshData? TryBuildSupportBeamMesh(BlockEntity entity)
    {
        var behavior = entity.GetBehavior<BEBehaviorSupportBeam>();
        if (behavior?.Beams is not { Length: > 0 }) return null;
        MeshData? combined = null;
        for (var index = 0; index < behavior.Beams.Length; index++)
        {
            MeshData? part = null;
            try { part = behavior.genMesh(index, null, null); }
            catch { }
            if (part == null || part.VerticesCount == 0)
            {
                part?.Dispose();
                continue;
            }
            if (combined == null) combined = part;
            else
            {
                combined.AddMeshData(part);
                part.Dispose();
            }
        }
        return combined;
    }

    public static ItemStack CreateDisplayStack(ICoreClientAPI capi, Block block)
    {
        var compatStack = BuildwrightCompatApi.CreateDisplayStack(capi, block);
        if (compatStack != null) return compatStack;
        var stack = new ItemStack(block);
        if (!IsStoneQuarrySlab(block)) return stack;

        // StoneQuarry's item renderer expects the dynamic rock preset that is
        // normally persisted by its block entity. Material lists have only a
        // block code, so use a stable rock preset solely for the icon.
        var count = StoneSlabRockCount(block.Code?.Path);
        if (count <= 0) return stack;
        var rock = capi.World.GetBlock(new AssetLocation("game:rock-granite"))
            ?? capi.World.Blocks.FirstOrDefault(candidate => candidate?.Code?.Path.StartsWith("rock-", StringComparison.OrdinalIgnoreCase) == true);
        if (rock?.Code == null) return stack;
        var preset = stack.Attributes.GetOrAddTreeAttribute("preset");
        preset.SetInt("size", count);
        for (var index = 0; index < count; index++) preset.SetString(index.ToString(), rock.Code.ToString());
        return stack;
    }

    private static bool IsStoneQuarrySlab(Block block) => block.Code?.Path.StartsWith("stoneslab-", StringComparison.OrdinalIgnoreCase) == true
        && (string.Equals(block.Code.Domain, "stonequarry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(block.EntityClass, "StoneSlab", StringComparison.OrdinalIgnoreCase));

    private static int StoneSlabRockCount(string? path) => path?.Split('-').Skip(1).FirstOrDefault()?.ToLowerInvariant() switch
    {
        "small" => 1,
        "medium" => 2,
        "large" => 4,
        "huge" => 8,
        "giant" => 12,
        _ => 0
    };

    private static bool UsesBlockOrientedMesh(Block block)
    {
        var path = block.Code?.Path;
        return path != null && (path.StartsWith("chest", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("labeledchest", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("trunk-", StringComparison.OrdinalIgnoreCase));
    }

    private static MeshData? BuildTessellatedMesh(ICoreClientAPI capi, Block block, ITreeAttribute tree)
    {
        try
        {
            capi.Tesselator.TesselateBlock(block, out var mesh);
            if (mesh == null || mesh.VerticesCount == 0) return null;
            var angle = tree.GetFloat("meshAngle");
            if (angle != 0) mesh.Rotate(BlockCenter, 0, angle, 0);
            return mesh;
        }
        catch
        {
            return null;
        }
    }

    private static BlockEntity CreateBlockEntity(ICoreClientAPI capi, string fallbackClass, string? capturedClass)
    {
        var candidates = new[]
        {
            capturedClass,
            string.IsNullOrWhiteSpace(capturedClass) ? null : capturedClass.Split('.').Last(),
            fallbackClass
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try { return capi.World.ClassRegistry.CreateBlockEntity(candidate!); }
            catch (Exception exception) { last = exception; }
        }
        throw last ?? new InvalidOperationException("No block entity class was available.");
    }

    private static MeshData? TryBuildShapeAttributeMesh(ICoreClientAPI capi, Block block, ITreeAttribute tree)
    {
        if (block is not BlockShapeFromAttributes shapeBlock) return null;
        var type = tree.GetString("type");
        if (string.IsNullOrWhiteSpace(type)) return null;
        var props = shapeBlock.GetTypeProps(type, null, null);
        if (props == null) return null;

        var mesh = shapeBlock.GetOrCreateMesh(props, null, tree.GetString("overrideTextureCode", null)).Clone();
        var rotateX = tree.GetFloat("rotateX");
        var rotateY = tree.GetFloat("meshAngle") + props.Rotation.Y * GameMath.DEG2RAD;
        var rotateZ = tree.GetFloat("rotateZ");
        mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), rotateX, rotateY, rotateZ);
        var offsetX = tree.GetFloat("offsetX");
        var offsetY = tree.GetFloat("offsetY");
        var offsetZ = tree.GetFloat("offsetZ");
        if (offsetX != 0 || offsetY != 0 || offsetZ != 0) mesh.Translate(offsetX, offsetY, offsetZ);
        return mesh.VerticesCount > 0 ? mesh : null;
    }

    private static MeshData? TryBuildWindmillMesh(ICoreClientAPI capi, Block block, ITreeAttribute tree)
    {
        if (!string.Equals(block.Code?.Path, "windmillrotor", StringComparison.OrdinalIgnoreCase)) return null;
        var sailLength = tree.GetInt("sailLength");
        if (sailLength <= 0) return null;
        var domain = block.Code?.Domain;
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var wildcardPath = block.Attributes?["sailedShapes"]["wildcardPath"].AsString(null);
        if (string.IsNullOrWhiteSpace(wildcardPath)) return null;
        var location = AssetLocation.Create(wildcardPath.Replace("{length}", sailLength.ToString()), domain)
            .WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        var shape = Vintagestory.API.Common.Shape.TryGet(capi, location);
        if (shape == null) return null;
        capi.Tesselator.TesselateShape(block, shape, out var mesh, new Vec3f(0, block.Shape.rotateY, 0));
        return mesh?.VerticesCount > 0 ? mesh : null;
    }

    public static MeshData? TryBuildChiselMesh(ICoreClientAPI capi, BlockSchematic schematic, ChiselVoxelData source)
    {
        var data = ResolveChiselBlockIds(capi, schematic, source);
        if (data.BlockIds.Length == 0 || data.VoxelCuboids.Length == 0 || data.OriginalVoxelCuboids.Length == 0)
            return null;
        try
        {
            var mesh = BlockEntityMicroBlock.CreateMesh(capi, data.VoxelCuboids.ToList(), data.BlockIds,
                data.DecorIds, data.DecorRotations, data.OriginalVoxelCuboids, null);
            if (mesh.VerticesCount <= 0) return null;
            PrepareChiselGhostMesh(mesh);
            return mesh;
        }
        catch (Exception exception)
        {
            capi.Logger.Debug("[buildwright] chisel mesh failed: {0}", exception.Message);
            return null;
        }
    }

    public static void RefreshSpecialMesh(ICoreClientAPI capi, BlockEntity entity)
    {
        if (entity is BlockEntityMicroBlock microBlock)
        {
            microBlock.RegenMesh(capi);
        }
    }

    /// <summary>
    /// The native microblock mesh leaves vertex colours for the terrain mesh pool to
    /// populate. Ghosts bypass that pool, so restore opaque white before applying the
    /// selected ghost opacity.
    /// </summary>
    public static void PrepareChiselGhostMesh(MeshData mesh)
    {
        if (mesh.Rgba == null || mesh.Rgba.Length == 0) return;
        Array.Fill(mesh.Rgba, byte.MaxValue);
    }

    private static void InvokeFromTree(BlockEntity entity, ITreeAttribute tree, IWorldAccessor world)
    {
        var methods = entity.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var method = methods.FirstOrDefault(m => m.Name == "FromTreeAttributes" && m.GetParameters().Length == 2);
        method?.Invoke(entity, new object?[] { tree, world });
    }

    private static MeshData? TryBuildEntityMesh(ICoreClientAPI capi, BlockEntity entity)
    {
        for (var type = entity.GetType(); type != null; type = type.BaseType)
        {
            var parameterless = type.GetMethod("GenMesh", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (parameterless?.Invoke(entity, null) is MeshData direct && direct.VerticesCount > 0)
                return ApplyEntityRenderTransform(entity, direct);

            var withTesselator = type.GetMethod("GenMesh", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(ITesselatorAPI) }, modifiers: null);
            if (withTesselator?.Invoke(entity, new object[] { capi.Tesselator }) is MeshData generated && generated.VerticesCount > 0)
                return ApplyEntityRenderTransform(entity, generated);

            var cacheBuilder = type.GetMethod("loadOrCreateMesh", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (cacheBuilder == null) continue;
            cacheBuilder.Invoke(entity, null);
            var cached = FindMesh(entity);
            if (cached?.VerticesCount > 0) return cached;
        }
        return null;
    }

    private static MeshData ApplyEntityRenderTransform(BlockEntity entity, MeshData mesh)
    {
        // Generic typed containers, including the two-block trunk, keep their mesh
        // unrotated in the cache. Vanilla applies this angle from OnTesselation.
        if (entity is BlockEntityGenericTypedContainer typedContainer)
            mesh.Rotate(0, typedContainer.MeshAngle, 0);
        return mesh;
    }

    private static MeshData? FindMesh(BlockEntity entity)
    {
        for (var type = entity.GetType(); type != null; type = type.BaseType)
        foreach (var name in new[] { "Mesh", "mesh", "ownMesh" })
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(entity) is MeshData mesh) return mesh;
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop?.GetValue(entity) is MeshData propertyMesh) return propertyMesh;
        }
        return null;
    }

    public static ChiselVoxelData ResolveChiselBlockIds(ICoreClientAPI capi, BlockSchematic schematic, ChiselVoxelData source)
    {
        return new ChiselVoxelData
        {
            Position = source.Position.Copy(),
            BlockIds = ResolveBlockIds(source.BlockIds, capi, schematic),
            VoxelCuboids = source.VoxelCuboids.ToArray(),
            OriginalVoxelCuboids = source.OriginalVoxelCuboids.ToArray(),
            DecorIds = source.DecorIds == null ? null : ResolveBlockIds(source.DecorIds, capi, schematic),
            DecorRotations = source.DecorRotations,
            Rotation = source.Rotation
        };
    }

    private static int[] ResolveBlockIds(IEnumerable<int> ids, ICoreClientAPI capi, BlockSchematic schematic)
    {
        return ids.Select(id =>
        {
            if (!schematic.BlockCodes.TryGetValue(id, out var code)) return id;
            var block = capi.World.GetBlock(code);
            return block is { Id: > 0 } ? block.Id : id;
        }).ToArray();
    }

    private static string Encode(ITreeAttribute tree)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) tree.ToBytes(writer);
        return Ascii85.Encode(stream.ToArray());
    }

    private static int[]? ReadIntArray(Type type, object instance, string name)
    {
        var value = ReadMember(type, instance, name);
        return value switch { int[] ints => ints.ToArray(), IEnumerable<int> list => list.ToArray(), _ => null };
    }

    private static uint[]? ReadUIntArray(Type type, object instance, string name)
    {
        var value = ReadMember(type, instance, name);
        return value switch { uint[] values => values.ToArray(), IEnumerable<uint> list => list.ToArray(), _ => null };
    }

    private static uint[]? ReadUIntList(Type type, object instance, string name) => ReadUIntArray(type, instance, name);
    private static int ReadInt(Type type, object instance, string name) => ReadMember(type, instance, name) is int value ? value : 0;

    private static int ReadRotation(BlockEntity entity)
    {
        if (entity is BlockEntityMicroBlock && MicroBlockRotationField?.GetValue(entity) is short rotation)
            return rotation;
        try
        {
            var tree = new TreeAttribute();
            entity.ToTreeAttributes(tree);
            var packed = tree.GetInt("rotation", 0);
            return packed == 0 ? 0 : ((packed >> 10) & 0x3ff) - 360;
        }
        catch { return 0; }
    }

    private static object? ReadMember(Type type, object instance, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var prop = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null) return prop.GetValue(instance);
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(instance);
        }
        return null;
    }
}
