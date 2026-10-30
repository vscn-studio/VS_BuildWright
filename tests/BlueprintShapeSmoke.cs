using Buildwright.Core;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;

internal static class BlueprintShapeSmoke
{
    public static void Run(string directory)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        var cube = JObject.Parse("""
            {"textureWidth":32,"textureHeight":32,"elements":[
              {"name":"body","from":[0,0,0],"to":[16,16,16],"faces":{
                "north":{"texture":"#all","uv":[0,0,32,32]},
                "east":{"texture":"#all","uv":[0,0,32,32]},
                "south":{"texture":"#all","uv":[0,0,32,32]},
                "west":{"texture":"#all","uv":[0,0,32,32]},
                "up":{"texture":"#all","uv":[0,0,32,32]},
                "down":{"texture":"#all","uv":[0,0,32,32]}}}]}
            """);
        var original = cube.ToString();
        var exporter = new BlueprintShapeExporter(code => new BlueprintShapeBlock(
            [new BlueprintShapePart(cube, new CompositeShape())],
            new Dictionary<string, string> { ["all"] = code == "game:stone" ? "game:block/stone/rock/granite1" : "game:block/wood/planks/oak1" }));
        var document = new BlueprintDocument
        {
            Metadata = new BlueprintMetadata { Name = "模型", Dimensions = new Int3(2, 1, 1) },
            Schematic = JObject.Parse("""
                {"SizeX":2,"SizeY":1,"SizeZ":1,"BlockCodes":{"1":"game:stone","2":"game:wood"},
                 "Indices":[0,1],"BlockIds":[1,2]}
                """)
        };
        var model = exporter.Build(document);
        BlueprintMeshSmoke.Run(directory, model);
        Check(model["elements"] is JArray && model["Indices"] == null, "Model must be a Shape, not a schematic");
        Check(model["textures"]!.Count() == 2, "Different block textures must not collide");
        Check(cube.ToString() == original, "Export must not modify shared block shape assets");
        {
            var mesh = Tessellate(model);
            Check(mesh.VerticesCount == 48, "Exported two-block Shape must tessellate through the game engine");
            Bounds(mesh, 0, 1, 0, .5f, .25f, .75f);
            Check(mesh.Uv.Take(mesh.VerticesCount * 2).All(v => v >= 0 && v <= 1), "Texture sizes must preserve normalized UVs");
            mesh.Dispose();
        }
        var first = exporter.Export(directory, document);
        var second = exporter.Export(directory, document);
        Check(Path.GetFileName(first) == "模型_model.json" && Path.GetFileName(second) == "模型_model (2).json",
            "Model export must append a suffix, support Unicode and preserve existing files");
        Check(JToken.DeepEquals(model, JObject.Parse(File.ReadAllText(first))), "Exported JSON must contain the model");
        CheckBlockbench(model, "blocks");
        var blockbenchFile = exporter.Export(directory, document, BlueprintModelFormat.Blockbench);
        var duplicateBlockbench = exporter.Export(directory, document, BlueprintModelFormat.Blockbench);
        Check(Path.GetFileName(blockbenchFile) == "模型_blockbench.json"
            && Path.GetFileName(duplicateBlockbench) == "模型_blockbench (2).json", "Blockbench exports must preserve earlier files");
        Check(JToken.DeepEquals(BlueprintBlockbenchExporter.Build(model), JObject.Parse(File.ReadAllText(blockbenchFile))),
            "Blockbench export must use the adapted Shape");

        // Composite texture rotation is applied to the atlas bitmap by the game. Standalone
        // exports use the unbaked base image, so preserve that rotation on every face.
        var rotatedTextureExporter = new BlueprintShapeExporter(_ => new BlueprintShapeBlock(
            [new BlueprintShapePart(cube, new CompositeShape())],
            new Dictionary<string, string> { ["all"] = "game:test" },
            new Dictionary<string, int> { ["all"] = 90 }));
        var rotatedTextureModel = rotatedTextureExporter.Build(document);
        var rotatedFaces = rotatedTextureModel.Descendants().OfType<JObject>()
            .Where(o => o["faces"] is JObject).SelectMany(o => ((JObject)o["faces"]!).Properties())
            .Select(p => (JObject)p.Value).ToArray();
        Check(rotatedFaces.Length == 12 && rotatedFaces.All(face => face.Value<int>("rotation") == 90),
            "Composite texture rotation must be preserved in exported faces");

        var aliasedCube = (JObject)cube.DeepClone();
        aliasedCube["textures"] = new JObject { ["all"] = "#side" };
        var aliasedTextureExporter = new BlueprintShapeExporter(_ => new BlueprintShapeBlock(
            [new BlueprintShapePart(aliasedCube, new CompositeShape())],
            new Dictionary<string, string> { ["side"] = "game:test" },
            new Dictionary<string, int> { ["side"] = 270 }));
        var aliasedTextureModel = aliasedTextureExporter.Build(document);
        var aliasedFaces = aliasedTextureModel.Descendants().OfType<JObject>()
            .Where(o => o["faces"] is JObject).SelectMany(o => ((JObject)o["faces"]!).Properties())
            .Select(p => (JObject)p.Value).ToArray();
        Check(aliasedFaces.Length == 12 && aliasedFaces.All(face => face.Value<int>("rotation") == 270),
            "Texture rotation aliases must be resolved in exported faces");

        // Faces without explicit UVs use the engine's geometry-sized automatic UVs.
        var autoUvShape = (JObject)cube.DeepClone();
        autoUvShape["textureWidth"] = 64;
        autoUvShape["textureHeight"] = 8;
        autoUvShape["textures"] = new JObject { ["all"] = "#side" };
        autoUvShape["textureSizes"] = new JObject { ["side"] = new JArray(64, 8) };
        autoUvShape["elements"]![0]!["to"] = new JArray(4, 8, 16);
        foreach (var face in ((JObject)autoUvShape["elements"]![0]!["faces"]!).Properties().ToArray())
            ((JObject)face.Value).Remove("uv");
        var autoUvExporter = new BlueprintShapeExporter(_ => new BlueprintShapeBlock(
            [new BlueprintShapePart(autoUvShape, new CompositeShape())],
            new Dictionary<string, string> { ["side"] = "game:test" }));
        var autoUvDocument = new BlueprintDocument
        {
            Metadata = new BlueprintMetadata { Dimensions = new Int3(1, 1, 1) },
            Schematic = JObject.Parse("""{"SizeX":1,"SizeY":1,"SizeZ":1,"BlockCodes":{"1":"game:stone"},"Indices":[0],"BlockIds":[1]}""")
        };
        var autoUvModel = autoUvExporter.Build(autoUvDocument);
        var autoUvFaces = autoUvModel.Descendants().OfType<JObject>()
            .Where(o => o["faces"] is JObject).SelectMany(o => ((JObject)o["faces"]!).Properties())
            .ToDictionary(p => p.Name, p => (JObject)p.Value);
        Check(autoUvFaces["north"].Value<JArray>("uv")!.Values<double>().SequenceEqual([0, 0, 4, 8])
            && autoUvFaces["east"].Value<JArray>("uv")!.Values<double>().SequenceEqual([0, 0, 16, 8])
            && autoUvFaces["up"].Value<JArray>("uv")!.Values<double>().SequenceEqual([0, 0, 4, 16]),
            "Missing UVs must follow the engine's face dimensions");
        Check(((JObject)autoUvModel["textureSizes"]!)["texture_0"]!.Values<int>().SequenceEqual([64, 8]),
            "Texture dimensions must resolve through Shape aliases");
        var autoUvMesh = Tessellate(autoUvModel);
        var sourceAutoUvMesh = Tessellate(autoUvShape);
        Check(autoUvMesh.Uv.Take(autoUvMesh.VerticesCount * 2).SequenceEqual(
            sourceAutoUvMesh.Uv.Take(sourceAutoUvMesh.VerticesCount * 2)),
            "Generated UVs must match the game tessellator");
        autoUvMesh.Dispose();
        sourceAutoUvMesh.Dispose();

        // Chiseled faces must rotate the selected UV rectangle with the texture.
        // A 5x2x16 cuboid exposes the non-square cases that otherwise stretch in Blockbench.
        var chiselShape = (JObject)cube.DeepClone();
        var chiselDocument = new BlueprintDocument
        {
            Metadata = new BlueprintMetadata { Dimensions = new Int3(1, 1, 1) },
            Schematic = JObject.Parse("{\"SizeX\":1,\"SizeY\":1,\"SizeZ\":1,\"BlockCodes\":{\"1\":\"game:chiseledblock\",\"2\":\"game:planks\"},\"Indices\":[0],\"BlockIds\":[1]}"),
            ChiselData = [new ChiselVoxelData
            {
                Position = new Int3(0, 0, 0), BlockIds = [2],
                VoxelCuboids = [Pack(0, 0, 0, 5, 2, 16, 0)]
            }]
        };
        var allRotatedChiselExporter = new BlueprintShapeExporter(_ => new BlueprintShapeBlock(
            [new BlueprintShapePart(chiselShape, new CompositeShape())],
            new Dictionary<string, string> { ["all"] = "game:test" },
            new Dictionary<string, int> { ["all"] = 90 }));
        var allRotatedFaces = ChiselFaces(allRotatedChiselExporter.Build(chiselDocument));
        Check(allRotatedFaces["north"].Value<JArray>("uv")!.Values<double>().SequenceEqual([14, 0, 16, 5])
            && allRotatedFaces["east"].Value<JArray>("uv")!.Values<double>().SequenceEqual([14, 0, 16, 16])
            && allRotatedFaces["up"].Value<JArray>("uv")!.Values<double>().SequenceEqual([0, 11, 16, 16])
            && allRotatedFaces.Values.All(face => face.Value<int>("rotation") == 90),
            "90-degree chisel texture rotation must swap non-square UV dimensions");

        var aliasedChiselExporter = new BlueprintShapeExporter(_ => new BlueprintShapeBlock(
            [new BlueprintShapePart(chiselShape, new CompositeShape())],
            new Dictionary<string, string> { ["all"] = "game:test", ["verticals"] = "game:test" },
            new Dictionary<string, int> { ["all"] = 0, ["verticals"] = 90 }));
        var aliasedChiselFaces = ChiselFaces(aliasedChiselExporter.Build(chiselDocument));
        Check(aliasedChiselFaces["north"].Property("rotation") == null
            && aliasedChiselFaces["north"].Value<JArray>("uv")!.Values<double>().SequenceEqual([11, 14, 16, 16])
            && aliasedChiselFaces["up"].Value<int>("rotation") == 90
            && aliasedChiselFaces["up"].Value<JArray>("uv")!.Values<double>().SequenceEqual([0, 11, 16, 16]),
            "Chisel vertical texture aliases must use their own rotation and UV orientation");

        // A multi-material chiseled block must use saved material mappings and voxel coordinates.
        document.ChiselData = [new ChiselVoxelData
        {
            Position = new Int3(0, 0, 0), BlockIds = [1, 2], Rotation = 90,
            VoxelCuboids = [Pack(0, 0, 0, 8, 16, 16, 0), Pack(8, 0, 0, 16, 8, 16, 1)]
        }];
        {
            CheckBlockbench(exporter.Build(document), "chisel");
            BlueprintMeshSmoke.CheckGlb(exporter.Build(document));
            BlueprintMeshSmoke.CheckStl(BlueprintStlExporter.Build(exporter.Build(document), 100, 16), new System.Numerics.Vector3(100, 50, 50), 218750);
            var mesh = Tessellate(exporter.Build(document));
            Check(mesh.VerticesCount == 72, "Chisel cuboids must replace the source block geometry");
            Bounds(mesh, 0, 1, 0, .5f, .25f, .75f);
            mesh.Dispose();
        }

        // Native schematic payloads must recover the same geometry without a VSBP sidecar.
        var saved = document.ChiselData[0];
        var tree = new TreeAttribute();
        tree["cuboids"] = new IntArrayAttribute(saved.VoxelCuboids);
        tree["materials"] = new IntArrayAttribute(saved.BlockIds);
        tree["decorIds"] = new IntArrayAttribute([0, 1]);
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) tree.ToBytes(writer);
            var reader = typeof(BlueprintCodec).Assembly.GetType("Buildwright.Client.BlueprintItemModelExport")!
                .GetMethod("ReadChisel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var recovered = (ChiselVoxelData?)reader.Invoke(null, [Ascii85.Encode(bytes.ToArray()), saved.Position]);
            Check(recovered != null && recovered.VoxelCuboids.SequenceEqual(saved.VoxelCuboids)
                && recovered.BlockIds.SequenceEqual(saved.BlockIds) && recovered.DecorIds!.SequenceEqual([0, 1]),
                "Native schematic chisel payload must preserve voxel and material data");
            var before = exporter.Build(document);
            document.ChiselData = [recovered!];
            Check(JToken.DeepEquals(before, exporter.Build(document)), "Native chisels must export the same model as VSBP chisels");
        }

        // Compare asymmetric shapes with the engine's own composite transform, including its Y=0 scale origin.
        var asymmetric = (JObject)cube.DeepClone();
        asymmetric["elements"]![0]!["to"] = new JArray(4, 8, 16);
        var transform = new CompositeShape { Scale = .5f, rotateX = 90, rotateY = 90, rotateZ = 90, offsetX = .125f, offsetY = .25f };
        var rotatedExporter = new BlueprintShapeExporter(_ => new BlueprintShapeBlock(
            [new BlueprintShapePart(asymmetric, transform)], new Dictionary<string, string> { ["all"] = "game:test" }));
        var single = new BlueprintDocument
        {
            Metadata = new BlueprintMetadata { Dimensions = new Int3(1, 1, 1) },
            Schematic = JObject.Parse("""{"SizeX":1,"SizeY":1,"SizeZ":1,"BlockCodes":{"1":"game:stone"},"Indices":[0],"BlockIds":[1]}""")
        };
        {
            CheckBlockbench(rotatedExporter.Build(single), "composite");
            BlueprintMeshSmoke.CheckGlb(rotatedExporter.Build(single));
            var actual = Tessellate(rotatedExporter.Build(single));
            var expected = Tessellate(asymmetric);
            new ShapeTesselator(null!, null!, null!, null!).ApplyCompositeShapeModifiers(ref expected, transform);
            Check(SameVertices(actual, expected), "Composite rotation, scale and offset must match the game engine");
            actual.Dispose();
            expected.Dispose();
        }

        // Source shapes can also contain scale, explicit pivots and children, independently of composite transforms.
        var nested = (JObject)asymmetric.DeepClone();
        var parent = (JObject)nested["elements"]![0]!;
        parent["rotationOrigin"] = new JArray(2, 3, 4);
        parent["rotationY"] = 37;
        parent["scaleX"] = parent["scaleY"] = parent["scaleZ"] = .7;
        var detail = (JObject)asymmetric["elements"]![0]!.DeepClone();
        detail["from"] = new JArray(2, 1, 3);
        detail["rotationZ"] = 25;
        detail["scaleX"] = .5;
        parent["children"] = new JArray(detail);
        var nestedExporter = new BlueprintShapeExporter(_ => new BlueprintShapeBlock(
            [new BlueprintShapePart(nested, transform)], new Dictionary<string, string> { ["all"] = "game:test" }));
        CheckBlockbench(nestedExporter.Build(single), "nested");
        BlueprintMeshSmoke.CheckGlb(nestedExporter.Build(single));
        parent["scaleY"] = .9;
        BlueprintMeshSmoke.CheckGlb(nestedExporter.Build(single));
        var unsupportedDirectory = Path.Combine(directory, "unsupported");
        var unsupported = false;
        try { nestedExporter.Export(unsupportedDirectory, single, BlueprintModelFormat.Blockbench); }
        catch (InvalidDataException) { unsupported = true; }
        Check(unsupported && !Directory.Exists(unsupportedDirectory), "Unrepresentable shear must fail without creating partial exports");

        var selectedShape = (JObject)cube.DeepClone();
        var child = (JObject)selectedShape["elements"]![0]!.DeepClone();
        child["name"] = "detail";
        selectedShape["elements"]![0]!["children"] = new JArray(child);
        var selection = new CompositeShape { SelectiveElements = ["body/*"] };
        var selectedExporter = new BlueprintShapeExporter(_ => new BlueprintShapeBlock(
            [new BlueprintShapePart(selectedShape, selection)], new Dictionary<string, string> { ["all"] = "game:test" }));
        var selectedMesh = Tessellate(selectedExporter.Build(single));
        Check(selectedMesh.VerticesCount == 48, "Wildcard child selection must retain geometry");
        selectedMesh.Dispose();
        selection.SelectiveElements = ["body"];
        selectedMesh = Tessellate(selectedExporter.Build(single));
        Check(selectedMesh.VerticesCount == 24, "Exact selection must exclude unselected children");
        selectedMesh.Dispose();

        var failed = false;
        try { new BlueprintShapeExporter(_ => throw new InvalidDataException("Missing mod")).Export(directory, single); }
        catch (InvalidDataException) { failed = true; }
        Check(failed, "Missing assets must fail instead of producing an incorrect model");
        single.Schematic["Indices"] = new JArray();
        single.Schematic["BlockIds"] = new JArray();
        failed = false;
        try { exporter.Build(single); } catch (InvalidDataException) { failed = true; }
        Check(failed, "Empty blueprints must not produce empty model files");
        Console.WriteLine("Blueprint Shape export smoke tests passed (game tessellator).");
    }

    private static void CheckBlockbench(JObject model, string name)
    {
        var original = model.ToString();
        var adapted = BlueprintBlockbenchExporter.Build(model);
        if (model.ToString() != original) throw new InvalidOperationException("Blockbench adaptation mutated the item model");
        var elements = adapted.Descendants().OfType<JObject>().Where(o => o["from"] != null).ToArray();
        if (elements.Any(e => e["scaleX"] != null || e["scaleY"] != null || e["scaleZ"] != null || e["rotationOrigin"] == null))
            throw new InvalidOperationException("Blockbench model contains unsupported scale or implicit pivots");
        var expected = Tessellate(model);
        var actual = Tessellate(adapted);
        if (!SameVertices(actual, expected)) throw new InvalidOperationException("Baked Blockbench geometry differs from item Shape: " + name);
        if (!actual.Uv.Take(actual.VerticesCount * 2).SequenceEqual(expected.Uv.Take(expected.VerticesCount * 2)))
            throw new InvalidOperationException("Baking changed UV coordinates");
        if (((JObject)adapted["textures"]!).Properties().Any(p => p.Value.Value<string>()!.StartsWith("game:")))
            throw new InvalidOperationException("Plugin expects vanilla texture paths without a domain");
        var fixtures = Environment.GetEnvironmentVariable("BUILDWRIGHT_BLOCKBENCH_FIXTURES");
        if (!string.IsNullOrEmpty(fixtures))
        {
            Directory.CreateDirectory(fixtures);
            File.WriteAllText(Path.Combine(fixtures, name + ".json"), new JObject
            {
                ["model"] = adapted,
                ["vertices"] = new JArray(expected.xyz.Take(expected.VerticesCount * 3))
            }.ToString());
        }
        expected.Dispose();
        actual.Dispose();
    }

    private static uint Pack(int x0, int y0, int z0, int x1, int y1, int z1, int material) =>
        (uint)(x0 | y0 << 4 | z0 << 8 | (x1 - 1) << 12 | (y1 - 1) << 16 | (z1 - 1) << 20 | material << 24);

    private static Dictionary<string, JObject> ChiselFaces(JObject model)
    {
        var element = model.Descendants().OfType<JObject>()
            .Single(o => o["name"]?.Value<string>() == "voxel_0");
        return ((JObject)element["faces"]!).Properties()
            .ToDictionary(p => p.Name, p => (JObject)p.Value);
    }

    private static MeshData Tessellate(JObject json)
    {
        var shape = json.ToObject<Shape>()!;
        new ShapeTesselator(null!, null!, null!, null!).TesselateShape("model smoke test", shape, out var mesh, new Textures());
        return mesh;
    }

    private static bool SameVertices(MeshData actual, MeshData expected)
    {
        if (actual.VerticesCount != expected.VerticesCount) return false;
        for (var i = 0; i < actual.VerticesCount * 3; i++)
            if (Math.Abs(actual.xyz[i] - expected.xyz[i]) > .00001f) return false;
        return true;
    }

    private static void Bounds(MeshData mesh, float x0, float x1, float y0, float y1, float z0, float z1)
    {
        var xs = Enumerable.Range(0, mesh.VerticesCount).Select(i => mesh.xyz[i * 3]).ToArray();
        var ys = Enumerable.Range(0, mesh.VerticesCount).Select(i => mesh.xyz[i * 3 + 1]).ToArray();
        var zs = Enumerable.Range(0, mesh.VerticesCount).Select(i => mesh.xyz[i * 3 + 2]).ToArray();
        if (Math.Abs(xs.Min() - x0) > .00001 || Math.Abs(xs.Max() - x1) > .00001
            || Math.Abs(ys.Min() - y0) > .00001 || Math.Abs(ys.Max() - y1) > .00001
            || Math.Abs(zs.Min() - z0) > .00001 || Math.Abs(zs.Max() - z1) > .00001)
            throw new InvalidOperationException("Model scale or placement is incorrect");
    }

    private sealed class Textures : ITexPositionSource
    {
        public Size2i AtlasSize => new(32, 32);
        public TextureAtlasPosition this[string textureCode] => new() { x1 = 0, y1 = 0, x2 = 1, y2 = 1 };
    }
}
