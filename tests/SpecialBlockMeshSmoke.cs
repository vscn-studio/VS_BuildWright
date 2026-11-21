using System.Reflection;
using Buildwright.Core;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

internal static class SpecialBlockMeshSmoke
{
    private static readonly Type Capture = typeof(BlueprintCodec).Assembly.GetType("Buildwright.Client.BlockEntityCapture")!;

    public static void Run()
    {
        Roofing();
        var install = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        if (string.IsNullOrEmpty(install))
        {
            Console.WriteLine("Special-block asset checks skipped: set VINTAGE_STORY to the game installation.");
            return;
        }
        var harness = new Harness(Path.Combine(install, "assets", "survival"));
        Containers(harness);
        Trapdoors(harness);
        Console.WriteLine("Special-block meshes passed: roofing interfaces, container types/angles, 16 trapdoor shapes and 24 mount orientations.");
    }

    private static void Containers(Harness harness)
    {
        var blockJson = JObject.Parse(File.ReadAllText(Path.Combine(harness.AssetsRoot, "blocktypes/wood/chest.json")));
        var block = new BlockGenericTypedContainer
        {
            Code = new AssetLocation("chest-east"),
            Attributes = new JsonObject(blockJson["attributes"]!),
            Shape = new CompositeShape { rotateY = 270 }
        };
        foreach (var type in new[] { "normal-generic", "owl", "golden-aged", "collapsed2" })
        foreach (var degrees in new[] { 0, 90, 180, 270, 22.5f, 112.5f, -67.5f })
        {
            var tree = new TreeAttribute();
            tree.SetString("type", type);
            tree.SetFloat("meshAngle", degrees * GameMath.DEG2RAD);
            harness.Textures.Keys.Clear();
            var actual = Invoke<MeshData>("TryBuildContainerMesh", harness.Api, block, tree);
            var path = block.Attributes["shape"][type].AsString();
            var shape = harness.Load(new AssetLocation("shapes/" + path + ".json"));
            var expected = Tessellate(shape, new Textures()).Rotate(new Vec3f(.5f, .5f, .5f), 0, degrees * GameMath.DEG2RAD, 0);
            SameVertices(actual, expected, $"Container {type} at {degrees} degrees must apply the saved full angle once");
            Check(harness.Textures.Keys.Count > 0 && harness.Textures.Keys.All(key => key.StartsWith(type + "-")),
                $"Container {type} must use its own material prefix");
            actual.Dispose();
            expected.Dispose();
        }
        var defaultMesh = Invoke<MeshData>("TryBuildContainerMesh", harness.Api, block, new TreeAttribute());
        Check(defaultMesh.VerticesCount > 0, "Older container trees without a type must use the block default");
        defaultMesh.Dispose();
    }

    private static void Trapdoors(Harness harness)
    {
        var generic = new ModernRoof();
        typeof(BlockEntity).GetProperty("Block")!.SetValue(generic, new Block { CollisionBoxes = [] });
        var vanilla = new BEBehaviorTrapDoor(generic);
        var vanillaTransform = typeof(BEBehaviorTrapDoor).GetMethod("getTfMatrix", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var face in BlockFacing.ALLFACES)
        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            var tree = new TreeAttribute();
            tree.SetInt("attachedFace", face.Index);
            tree.SetInt("rotDeg", degrees);
            vanilla.AttachedFace = face.Index;
            vanilla.RotDeg = degrees;
            var expected = (Matrixf)vanillaTransform.Invoke(vanilla, [0f])!;
            var actual = Invoke<Matrixf>("TrapdoorTransform", tree);
            Check(actual.Values.Zip(expected.Values).All(pair => Math.Abs(pair.First - pair.Second) < .00001),
                $"Trapdoor mount {face.Code}/{degrees} must match vanilla");
        }

        foreach (var material in new[] { "wood", "metal" })
        foreach (var file in Directory.GetFiles(Path.Combine(harness.AssetsRoot, "shapes/block", material, "trapdoor"), "*.json"))
        {
            var location = new AssetLocation(Path.GetRelativePath(harness.AssetsRoot, file).Replace('\\', '/'));
            var source = harness.Load(location);
            var before = JObject.FromObject(source).ToString();
            var hinge = source.GetElementByName("N frame")!;
            var origin = source.GetElementByName("origin")!;
            var pivot = new Vec3f((float)(origin.From![0] + hinge.RotationOrigin![0]) / 16,
                (float)(origin.From[1] + hinge.RotationOrigin[1]) / 16,
                (float)(origin.From[2] + hinge.RotationOrigin[2]) / 16);
            var block = new Block
            {
                Code = new AssetLocation("trapdoor-" + material),
                Attributes = new JsonObject(new JObject()),
                Shape = new CompositeShape { Base = location }
            };
            block.BlockBehaviors = [new BlockBehaviorTrapDoor(block)];
            block.CollectibleBehaviors = block.BlockBehaviors;
            foreach (var opened in new[] { false, true })
            {
                var tree = new TreeAttribute();
                tree.SetInt("attachedFace", BlockFacing.UP.Index);
                tree.SetBool("opened", opened);
                harness.Textures.Keys.Clear();
                var actual = Invoke<MeshData>("TryBuildTrapdoorMesh", harness.Api, block, tree);
                var expected = Tessellate(source, new Textures());
                if (opened) expected.Rotate(pivot, -GameMath.PIHALF, 0, 0);
                SameVertices(actual, expected, $"{location}/{opened}: trapdoors must open around their hinge");
                Check(ReferenceEquals(harness.LastTextureBlock, block) && harness.Textures.Keys.Count > 0,
                    "Trapdoor textures must come from the selected block rather than a shared style cache");
                actual.Dispose();
                expected.Dispose();
            }
            Check(JObject.FromObject(source).ToString() == before, "Opening a preview must not mutate the shared shape");
        }
    }

    private static void Roofing()
    {
        RoofPreviewInitialization();
        var tesselator = Proxy<ITesselatorAPI>((_, _) => throw new InvalidOperationException("Unexpected tessellator use"));
        var capi = Proxy<ICoreClientAPI>((method, _) => method.Name == "get_Tesselator" ? tesselator : throw new NotSupportedException(method.Name));
        var block = new Block { Code = new AssetLocation("vsroofing:autoroof-north") };
        foreach (var entity in new BlockEntity[] { new LegacyRoof(), new ModernRoof() })
        {
            var actual = Invoke<MeshData>("TryBuildDynamicCompositeMesh", capi, block, entity);
            Check(actual.VerticesCount == 12 && actual.IndicesCount == 18,
                "Roof preview must combine roof, fence connection and plant on both API generations");
            actual.Translate(100, 0, 0);
            var again = Invoke<MeshData>("TryBuildDynamicCompositeMesh", capi, block, entity);
            Check(again.xyz.Take(again.VerticesCount * 3).Max() < 10, "Roof preview must own its mesh data");
            actual.Dispose();
            again.Dispose();
        }

        var container = new CachedContainer { MeshAngle = GameMath.PIHALF };
        var cachedVertices = container.Cached.xyz.ToArray();
        var first = Invoke<MeshData>("TryBuildEntityMesh", capi, container);
        var second = Invoke<MeshData>("TryBuildEntityMesh", capi, container);
        SameVertices(first, second, "Repeated previews must not accumulate rotation in a cached entity mesh");
        Check(container.Cached.xyz.SequenceEqual(cachedVertices), "Preview rotation must not mutate the entity mesh cache");
        first.Dispose();
        second.Dispose();
        container.Cached.Dispose();

        // Optional external fixture: inspect the user's mod without shipping its DLL.
        var modPath = Environment.GetEnvironmentVariable("BUILDWRIGHT_VSROOFING_ASSEMBLY");
        if (!string.IsNullOrWhiteSpace(modPath))
        {
            var assembly = Assembly.LoadFrom(modPath);
            RoofMigrationContract(assembly);
            foreach (var name in new[] { "AutoRoofEntity", "AutoRoofEntityChiseled" })
            {
                var dataType = assembly.GetType("VSRoofing." + name)!.GetProperty("RoofData")!.PropertyType;
                foreach (var method in new[] { "GenerateRoofMesh", "GenerateFenceConnectMesh", "GeneratePlantMesh" })
                    Check(dataType.GetMethod(method, [typeof(ITesselatorAPI)])?.ReturnType == typeof(MeshData),
                        $"{name}.RoofData.{method} must match the actual VS Roofing assembly");
            }
            Console.WriteLine("VS Roofing assembly contract passed: " + modPath);
        }
    }

    private static void RoofPreviewInitialization()
    {
        foreach (var face in BlockFacing.HORIZONTALS)
        foreach (var ridge in new[] { false, true })
        {
            var block = new Block { Code = new AssetLocation("vsroofing:roof-bare-" + face.Code), EntityClass = "preview-roof" };
            block.VariantStrict["side"] = face.Code;
            var tree = new TreeAttribute();
            tree.SetString("direction", face.Code);
            tree.SetInt("BuildNumber", 0);
            tree.SetInt("Type", ridge ? 2 : 0);
            tree.SetBool("RidgeShapeResolved", false);
            var payload = (string)Capture.GetMethod("EncodeTree")!.Invoke(null, [tree])!;
            var registry = Proxy<IClassRegistryAPI>((method, _) => method.Name == "CreateBlockEntity"
                ? new MigratingRoof() : throw new NotSupportedException(method.Name));
            var world = Proxy<IClientWorldAccessor>((method, _) => method.Name switch
            {
                "get_ClassRegistry" => registry,
                "get_Side" => EnumAppSide.Client,
                _ => throw new NotSupportedException(method.Name)
            });
            var logger = Proxy<ILogger>((_, _) => null);
            var tesselator = Proxy<ITesselatorAPI>((_, _) => throw new InvalidOperationException("Unexpected tessellator use"));
            var capi = Proxy<ICoreClientAPI>((method, _) => method.Name switch
            {
                "get_World" => world,
                "get_Logger" => logger,
                "get_Tesselator" => tesselator,
                _ => throw new NotSupportedException(method.Name)
            });
            var actual = (MeshData?)Capture.GetMethod("TryBuildMesh")!.Invoke(null,
                [capi, new BlockSchematic(), block, payload, null, null]);
            Check(actual != null, "Roof preview must survive entity initialization");
            var expected = CubeMeshUtil.GetCubeFace(face);
            SameVertices(actual!, expected, $"{face.Code}/{ridge}: initialization must preserve saved roof orientation");
            Check(payload == (string)Capture.GetMethod("EncodeTree")!.Invoke(null, [tree])!,
                "Roof preview must not rewrite the stored blueprint");
            actual!.Dispose();
            expected.Dispose();
        }
        var other = new TreeAttribute();
        other.SetInt("BuildNumber", 7);
        Capture.GetMethod("PrepareRoofPreviewTree", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new Block { Code = new AssetLocation("game:stone") }, other]);
        Check(other.GetInt("BuildNumber") == 7 && !other.HasAttribute("RidgeShapeResolved"),
            "Roof migration suppression must not affect other mods");
    }

    private static void RoofMigrationContract(Assembly assembly)
    {
        var dataType = assembly.GetType("VSRoofing.AutoRoofData")!;
        var configField = assembly.GetType("VSRoofing.VSRoofingModSystem")!.GetField("Config")!;
        configField.SetValue(null, Activator.CreateInstance(configField.FieldType));
        var callbacks = 0;
        var events = Proxy<IClientEventAPI>((method, _) => method.Name == "RegisterCallback"
            ? (long)++callbacks : throw new NotSupportedException(method.Name));
        var api = Proxy<ICoreClientAPI>((method, _) => method.Name == "get_Event"
            ? events : throw new NotSupportedException(method.Name));
        foreach (var ridge in new[] { false, true })
        {
            var owner = new ModernRoof { Api = api, Pos = new BlockPos(0, 1, 0) };
            var block = new Block { Code = new AssetLocation("vsroofing:roof-bare-east") };
            block.VariantStrict["side"] = "east";
            owner.Block = block;
            var data = Activator.CreateInstance(dataType)!;
            dataType.GetField("Owner")!.SetValue(data, owner);
            var ridgeRoofInfo = dataType.GetField("RidgeRoofInfo")!;
            ridgeRoofInfo.SetValue(data, Array.CreateInstance(ridgeRoofInfo.FieldType.GetElementType()!, 4));
            var roofType = dataType.GetProperty("RoofType")!;
            roofType.SetValue(data, Enum.ToObject(roofType.PropertyType, ridge ? 2 : 0));
            var build = dataType.GetField("BuildNumber", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var resolved = dataType.GetField("RidgeShapeResolved", BindingFlags.Instance | BindingFlags.NonPublic)!;
            build.SetValue(data, 0);
            resolved.SetValue(data, false);
            var initialize = dataType.GetMethod("Initialize")!;
            var before = callbacks;
            initialize.Invoke(data, null);
            Check(callbacks == before + 1, "Unprepared VS Roofing data must request world-dependent migration");
            var tree = new TreeAttribute();
            Capture.GetMethod("PrepareRoofPreviewTree", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [block, tree]);
            build.SetValue(data, tree.GetInt("BuildNumber"));
            resolved.SetValue(data, tree.GetBool("RidgeShapeResolved"));
            before = callbacks;
            initialize.Invoke(data, null);
            Check(callbacks == before, "Preview initialization must not schedule VS Roofing world migration");
        }
    }

    private static T Invoke<T>(string name, params object[] args) =>
        (T)(Capture.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)
            ?? throw new InvalidOperationException(name + " returned no mesh"));

    private static MeshData Tessellate(Shape shape, ITexPositionSource textures)
    {
        new ShapeTesselator(null!, null!, null!, null!).TesselateShape("special-block smoke", shape, out var mesh, textures);
        return mesh;
    }

    private static void SameVertices(MeshData actual, MeshData expected, string message)
    {
        Check(actual.VerticesCount == expected.VerticesCount && actual.VerticesCount > 0, message);
        Check(actual.xyz.Take(actual.VerticesCount * 3).Zip(expected.xyz).All(pair => Math.Abs(pair.First - pair.Second) < .00001), message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Proxy<T>(System.Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, ApiProxy>();
        ((ApiProxy)(object)result).Handler = handler;
        return result;
    }

    public class ApiProxy : DispatchProxy
    {
        public System.Func<MethodInfo, object?[], object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args!);
    }

    private sealed class Textures : ITexPositionSource
    {
        public readonly HashSet<string> Keys = [];
        public Size2i AtlasSize => new(32, 32);
        public TextureAtlasPosition this[string code]
        {
            get { Keys.Add(code); return new() { x1 = 0, y1 = 0, x2 = 1, y2 = 1 }; }
        }
    }

    private sealed class Harness
    {
        public string AssetsRoot { get; }
        public ICoreClientAPI Api { get; }
        public Textures Textures { get; } = new();
        public Block? LastTextureBlock;
        private readonly Dictionary<string, Shape> shapes = [];

        public Harness(string assetsRoot)
        {
            AssetsRoot = assetsRoot;
            var logger = Proxy<ILogger>((method, args) =>
            {
                Console.WriteLine("Asset loader: " + string.Join(" ", args.Select(arg => arg?.ToString())));
                return null;
            });
            var world = Proxy<IClientWorldAccessor>((method, _) => method.Name == "get_Logger" ? logger : throw new NotSupportedException(method.Name));
            var assets = Proxy<IAssetManager>((method, args) =>
            {
                if (method.Name != "TryGet") throw new NotSupportedException(method.Name);
                var location = args[0] as AssetLocation ?? new AssetLocation((string)args[0]!);
                if (!File.Exists(Path.Combine(AssetsRoot, location.Path))) return null;
                return Proxy<IAsset>((assetMethod, _) => assetMethod.Name == "ToObject" ? Load(location) : throw new NotSupportedException(assetMethod.Name));
            });
            var tesselator = Proxy<ITesselatorAPI>((method, args) =>
            {
                if (method.Name == "GetTextureSource") { LastTextureBlock = (Block)args[0]!; return Textures; }
                if (method.Name != "TesselateShape") throw new NotSupportedException(method.Name);
                var shape = (Shape)args[1]!;
                ITexPositionSource source;
                Vec3f? rotation;
                if (args[0] is TesselationMetaData meta) { source = meta.TexSource; rotation = meta.Rotation; }
                else { LastTextureBlock = (Block)args[0]!; source = Textures; rotation = (Vec3f?)args[3]; }
                var mesh = Tessellate(shape, source);
                if (rotation != null) mesh.Rotate(new Vec3f(.5f, .5f, .5f), rotation.X * GameMath.DEG2RAD, rotation.Y * GameMath.DEG2RAD, rotation.Z * GameMath.DEG2RAD);
                args[2] = mesh;
                return null;
            });
            Api = Proxy<ICoreClientAPI>((method, _) => method.Name switch
            {
                "get_Assets" => assets,
                "get_Tesselator" => tesselator,
                "get_World" => world,
                "get_Logger" => logger,
                _ => throw new NotSupportedException(method.Name)
            });
        }

        public Shape Load(AssetLocation location)
        {
            if (!shapes.TryGetValue(location.Path, out var shape))
                shapes[location.Path] = shape = JObject.Parse(File.ReadAllText(Path.Combine(AssetsRoot, location.Path))).ToObject<Shape>()!;
            return shape;
        }
    }

    private class LegacyRoof : BlockEntity
    {
        private readonly MeshData roof = CubeMeshUtil.GetCubeFace(BlockFacing.UP);
        private MeshData GenerateRoofMesh(ITesselatorAPI _) => roof;
        private MeshData GenerateFenceConnectMesh(ITesselatorAPI _) => CubeMeshUtil.GetCubeFace(BlockFacing.NORTH);
        private MeshData GeneratePlantMesh(ITesselatorAPI _) => CubeMeshUtil.GetCubeFace(BlockFacing.EAST);
    }

    private sealed class MigratingRoof : BlockEntity
    {
        private string direction = "north";
        private bool migrate;
        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world)
        {
            base.FromTreeAttributes(tree, world);
            direction = tree.GetString("direction");
            var build = tree.GetInt("BuildNumber");
            migrate = (build >= 0 && build != 1)
                || (tree.GetInt("Type") == 2 && !tree.GetBool("RidgeShapeResolved"));
        }
        public override void Initialize(ICoreAPI api)
        {
            Api = api;
            // Legacy roofs recalculate immediately against unrelated neighbors.
            if (migrate) direction = BlockFacing.FromCode(direction).Opposite.Code;
        }
        public MeshData GenerateRoofMesh(ITesselatorAPI _) => CubeMeshUtil.GetCubeFace(BlockFacing.FromCode(direction));
    }

    private sealed class CachedContainer : BlockEntityGenericTypedContainer
    {
        public readonly MeshData Cached = CubeMeshUtil.GetCubeFace(BlockFacing.NORTH);
        private MeshData GenMesh() => Cached;
    }

    private sealed class ModernRoof : BlockEntity
    {
        public RoofData RoofData { get; } = new();
    }

    private sealed class RoofData
    {
        public MeshData GenerateRoofMesh(ITesselatorAPI _) => CubeMeshUtil.GetCubeFace(BlockFacing.UP);
        public MeshData GenerateFenceConnectMesh(ITesselatorAPI _) => CubeMeshUtil.GetCubeFace(BlockFacing.NORTH);
        public MeshData GeneratePlantMesh(ITesselatorAPI _) => CubeMeshUtil.GetCubeFace(BlockFacing.EAST);
    }
}
