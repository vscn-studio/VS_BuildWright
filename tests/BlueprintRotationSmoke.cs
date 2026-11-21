extern alias Server;

using System.Reflection;
using Buildwright.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ServerSystem = Server::Buildwright.Server.BuildwrightServerModSystem;
using ServerRequest = Server::Buildwright.Protocol.WorldOperationRequest;

internal static class BlueprintRotationSmoke
{
    private static readonly Assembly Client = typeof(BlueprintCodec).Assembly;
    private static readonly Type Capture = Client.GetType("Buildwright.Client.BlockEntityCapture")!;
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    public static void Run()
    {
        CheckNativeBlocks(() => new Block(), () => new BlockEntityGenericTypedContainer(), null);
        var path = Environment.GetEnvironmentVariable("BUILDWRIGHT_VSROOFING_ASSEMBLY");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var roof = Assembly.LoadFrom(path);
            CheckNativeBlocks(() => (Block)Activator.CreateInstance(roof.GetType("VSRoofing.RoofBlock")!)!,
                () => (BlockEntity)Activator.CreateInstance(roof.GetType("VSRoofing.AutoRoofEntity")!)!, roof);
        }
        CheckBeams();
        Console.WriteLine("Rotation smoke passed: rendered geometry, native roof/block directions, entity payloads, beam endpoints/faces and server placement agree.");
    }

    private static void CheckNativeBlocks(Func<Block> createBlock, Func<BlockEntity> createEntity, Assembly? roof)
    {
        var blocks = BlockFacing.HORIZONTALS.Select(face =>
        {
            var block = createBlock();
            block.Code = new AssetLocation("test:roof-" + face.Code);
            block.EntityClass = "test";
            block.VariantStrict["side"] = face.Code;
            if (roof == null)
            {
                block.BlockBehaviors = [new BlockBehaviorHorizontalOrientable(block)];
                block.CollectibleBehaviors = block.BlockBehaviors;
            }
            return block;
        }).ToDictionary(block => block.Code.ToString());
        var registry = Proxy<IClassRegistryAPI>((method, _) => method.Name == "CreateBlockEntity" ? createEntity() : null);
        var logger = Proxy<ILogger>((method, args) => throw new InvalidOperationException(method.Name + ": " + string.Join(",", args)));
        var world = Proxy<IClientWorldAccessor>((method, args) => method.Name switch
        {
            "GetBlock" => blocks[((AssetLocation)args[0]!).ToString()],
            "get_ClassRegistry" => registry,
            _ => null
        });
        var api = Proxy<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_World" => world,
            "get_Logger" => logger,
            _ => null
        });
        var serverWorld = Proxy<IServerWorldAccessor>((method, args) => method.Name == "GetBlock"
            ? blocks[((AssetLocation)args[0]!).ToString()] : null);
        var serverApi = Proxy<ICoreServerAPI>((method, _) => method.Name == "get_World" ? serverWorld : null);
        var server = new ServerSystem();
        typeof(ServerSystem).GetField("sapi", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, serverApi);
        var resolveServerBlock = typeof(ServerSystem).GetMethod("ResolveTransformedBlock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var face in BlockFacing.HORIZONTALS)
        for (var turns = -4; turns <= 4; turns++)
        foreach (var ridge in roof == null ? new[] { false } : new[] { false, true })
        {
            var block = blocks["test:roof-" + face.Code];
            var tree = new TreeAttribute();
            tree.SetString("blockCode", block.Code.ToShortString());
            tree.SetFloat("meshAngle", .3f);
            Type? cornerType = null;
            Type? ridgeType = null;
            if (roof != null)
            {
                if (ridge)
                {
                    var slopeType = roof.GetType("VSRoofing.AutoSlopeInfo")!;
                    ridgeType = typeof(Dictionary<,>).MakeGenericType(typeof(string), slopeType);
                    var slopes = (System.Collections.IDictionary)Activator.CreateInstance(ridgeType)!;
                    slopes.Add(face.Code, Activator.CreateInstance(slopeType));
                    tree.SetInt("Type", 2);
                    tree.SetBytes("RidgeInfo", Serialize(ridgeType, slopes));
                }
                else
                {
                    cornerType = roof.GetType("VSRoofing.AutoSlopeCornerInfo")!;
                    var corner = Activator.CreateInstance(cornerType)!;
                    cornerType.GetField("Direction")!.SetValue(corner, face.GetCW().Code);
                    tree.SetInt("CornerType", Convert.ToInt32(Enum.Parse(roof.GetType("VSRoofing.AutoSlopeCornerType")!, "Outer")));
                    tree.SetBytes("CornerInfo", Serialize(cornerType, corner));
                }
            }
            var schematic = new BlockSchematic();
            schematic.Indices.Add(0);
            schematic.BlockIds.Add(1);
            schematic.BlockCodes[1] = block.Code;
            var payload = Encode(tree);
            var result = Decode((string)Capture.GetMethod("TransformPayload")!.Invoke(null,
                [api, schematic, 0u, payload, turns, MirrorAxis.None])!);
            var matrix = Projection(new Int3(), turns, MirrorAxis.None);
            var expected = RotatedFace(matrix, face);
            Check(result.GetString("blockCode") == "test:roof-" + expected.Code,
                $"{block.GetType().Name}/{face}/{turns}: payload direction differs from rendered geometry");
            var placedBlock = (Block)resolveServerBlock.Invoke(server, [block.Code, new ServerRequest { RotationQuarterTurns = turns }])!;
            Check(placedBlock.Code.ToString() == result.GetString("blockCode"), "Server roof/block variant must match the preview");
            if (cornerType != null)
            {
                var corner = typeof(SerializerUtil).GetMethods().Single(m => m.Name == "Deserialize" && m.IsGenericMethod
                    && m.GetParameters().Length == 1).MakeGenericMethod(cornerType)
                    .Invoke(null, [result.GetBytes("CornerInfo")])!;
                Check((string)cornerType.GetField("Direction")!.GetValue(corner)! == RotatedFace(matrix, face.GetCW()).Code,
                    "Roof corner must rotate with the roof block and its rendered mesh");
            }
            else if (ridgeType != null)
            {
                var slopes = (System.Collections.IDictionary)typeof(SerializerUtil).GetMethods().Single(m => m.Name == "Deserialize"
                    && m.IsGenericMethod && m.GetParameters().Length == 1).MakeGenericMethod(ridgeType)
                    .Invoke(null, [result.GetBytes("RidgeInfo")])!;
                Check(slopes.Count == 1 && slopes.Contains(expected.Code), "Roof ridge directions must match the rendered rotation");
            }
            else
            {
                var expectedAngle = .3f + turns * GameMath.PIHALF;
                Check(Math.Abs(Math.Sin(result.GetFloat("meshAngle")) - Math.Sin(expectedAngle)) < .0001
                    && Math.Abs(Math.Cos(result.GetFloat("meshAngle")) - Math.Cos(expectedAngle)) < .0001,
                    "Native entity angle must match the render matrix");
            }
        }
    }

    private static void CheckBeams()
    {
        var server = new ServerSystem();
        var restored = new RestoredEntity();
        var block = new Block { Code = new AssetLocation("game:beam"), EntityClass = "beam" };
        var accessor = Proxy<IBlockAccessor>((method, _) => method.Name == "GetBlockEntity" ? restored : null);
        var world = Proxy<IServerWorldAccessor>((method, _) => method.Name == "get_BlockAccessor" ? accessor : null);
        var logger = Proxy<ILogger>((_, args) => throw new InvalidOperationException(string.Join(",", args)));
        var api = Proxy<ICoreServerAPI>((method, _) => method.Name switch
        {
            "get_World" => world,
            "get_Logger" => logger,
            _ => null
        });
        typeof(ServerSystem).GetField("sapi", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, api);
        var placementType = typeof(ServerSystem).GetNestedType("BlockEntityPlacement", BindingFlags.NonPublic)!;
        var restore = typeof(ServerSystem).GetMethod("RestoreBlockEntity", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var exact = typeof(ServerSystem).GetMethod("TransformExactBeamTree", HiddenStatic)!;
        var clientTransform = Client.GetType("Buildwright.Client.SupportBeamDataComparer")!.GetMethod("TransformInPlace")!;
        var inverseMethod = Client.GetType("Buildwright.Client.BuildwrightSupportBeamGuideMesh")!.GetMethod("CreateInverseTransform", HiddenStatic)!;
        var cell = new Int3(2, 3, 4);
        var anchor = new Int3(1, 2, 1);
        foreach (var mirror in Enum.GetValues<MirrorAxis>())
        for (var turns = -4; turns <= 4; turns++)
        foreach (var face in BlockFacing.ALLFACES)
        {
            var original = new PlacedBeam { Start = new Vec3f(.125f, .25f, .5f), End = new Vec3f(3.25f, 1.5f, -.75f),
                BlockId = 17, FacingIndex = face.Index };
            var tree = new TreeAttribute();
            tree.SetBytes("beams", SerializerUtil.Serialize(new[] { original }));
            var payload = Encode(tree);
            var request = new ServerRequest { RotationQuarterTurns = turns, MirrorAxis = (int)mirror };
            var worldCell = BlueprintTransforms.CellToWorld(cell, anchor, new Int3(), turns, mirror);
            var position = new BlockPos(worldCell.X, worldCell.Y, worldCell.Z);
            var placement = Activator.CreateInstance(placementType, [position, block, payload])!;
            restored.Tree = null;
            restore.Invoke(server, [new BlockSchematic(), placement, request]);
            Check(restored.Tree != null, "Ordinary beam placement must restore its transformed payload");
            var exactTree = Decode(payload);
            exact.Invoke(null, [exactTree, request]);
            clientTransform.Invoke(null, [tree, turns, mirror]);
            var matrix = Projection(anchor, turns, mirror);
            var inverse = (Matrixf)inverseMethod.Invoke(null, [anchor, turns, mirror])!;
            foreach (var transformed in new[] { tree, exactTree, restored.Tree! })
            {
                var beam = SerializerUtil.Deserialize<PlacedBeam[]>(transformed.GetBytes("beams"))[0];
                foreach (var pair in new[] { (original.Start, beam.Start), (original.End, beam.End) })
                {
                    var local = new Vec4f(cell.X + pair.Item1.X, cell.Y + pair.Item1.Y, cell.Z + pair.Item1.Z, 1);
                    var rendered = matrix.TransformVector(local);
                    Close(rendered.XYZ, new Vec3f(worldCell.X + pair.Item2.X, worldCell.Y + pair.Item2.Y,
                        worldCell.Z + pair.Item2.Z), "Beam endpoints must match rendering, including cross-block beams");
                    Close(inverse.TransformVector(rendered).XYZ, local.XYZ, "Beam guide picking must invert the rendered transform");
                }
                Check(beam.FacingIndex == RotatedFace(matrix, face).Index, "Beam starting face must rotate with its endpoints");
                Check(beam.BlockId == 17, "Rotation must preserve beam material");
            }
        }
    }

    private static Matrixf Projection(Int3 anchor, int turns, MirrorAxis mirror) =>
        (Matrixf)Client.GetType("Buildwright.Client.BuildwrightGhostRenderer")!.GetMethod("ApplyProjectionTransform", HiddenStatic)!
            .Invoke(null, [new Matrixf(), anchor, turns, mirror])!;
    private static BlockFacing RotatedFace(Matrixf matrix, BlockFacing face)
    {
        var n = face.Normalf;
        var rotated = matrix.TransformVector(new Vec4f(n.X, n.Y, n.Z, 0));
        return BlockFacing.FromVector(rotated.X, rotated.Y, rotated.Z);
    }
    private static string Encode(ITreeAttribute tree) => (string)Capture.GetMethod("EncodeTree")!.Invoke(null, [tree])!;
    private static byte[] Serialize(Type type, object value) => (byte[])typeof(SerializerUtil).GetMethods()
        .Single(m => m.Name == "Serialize" && m.IsGenericMethod && m.GetParameters().Length == 1)
        .MakeGenericMethod(type).Invoke(null, [value])!;
    private static ITreeAttribute Decode(string payload) => (ITreeAttribute)Capture.GetMethod("Decode")!.Invoke(null, [payload])!;
    private static T Proxy<T>(System.Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, SpecialBlockMeshSmoke.ApiProxy>();
        ((SpecialBlockMeshSmoke.ApiProxy)(object)proxy).Handler = handler;
        return proxy;
    }
    private static void Close(Vec3f a, Vec3f b, string message) =>
        Check(Math.Abs(a.X - b.X) < .0001 && Math.Abs(a.Y - b.Y) < .0001 && Math.Abs(a.Z - b.Z) < .0001, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class RestoredEntity : BlockEntity
    {
        public ITreeAttribute? Tree;
        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world) => Tree = tree;
    }
}
