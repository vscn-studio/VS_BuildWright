using System.Numerics;
using System.Text;
using Buildwright.Core;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

internal static class BlueprintMeshSmoke
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWQkAAAAASUVORK5CYII=");
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static void Run(string directory, JObject model)
    {
        var glb = CheckGlb(model);
        var first = BlueprintExporter.ExportBinary(directory, "模型_blender", ".glb", glb);
        var second = BlueprintExporter.ExportBinary(directory, "模型_blender", ".glb", glb);
        Check(Path.GetFileName(first) == "模型_blender.glb" && Path.GetFileName(second) == "模型_blender (2).glb", "GLB naming/collision handling failed");
        var stl = BlueprintStlExporter.Build(model, 100, 16);
        CheckStl(stl, new Vector3(100, 50, 50), 250000);
        Check(BinaryPrimitives(stl) == 1280, "Adjacent cuboids must have no shared internal faces");
        var scaled = BlueprintStlExporter.Build(model, 200, 16);
        CheckStl(scaled, new Vector3(200, 100, 100), 2000000);
        var print = BlueprintExporter.ExportBinary(directory, "模型_print", ".stl", stl);
        Check(Path.GetFileName(print) == "模型_print.stl", "Print suffix failed");
        var overlap = (JObject)model.DeepClone();
        ((JArray)overlap["elements"]!).Add(overlap["elements"]![0]!.DeepClone());
        Check(stl.SequenceEqual(BlueprintStlExporter.Build(overlap, 100, 16)), "Overlapping solids must be unioned");
        var openFaces = (JObject)model.DeepClone();
        foreach (var faces in openFaces.Descendants().OfType<JObject>().Where(o => o["faces"] != null).Select(o => (JObject)o["faces"]!)) faces.Property("down")?.Remove();
        Check(stl.SequenceEqual(BlueprintStlExporter.Build(openFaces, 100, 16)), "Printable cuboids must be closed even when a render face is culled");
        foreach (var bad in new[] { float.NaN, 0, -100, 2001 })
        {
            var failed = false;
            try { BlueprintStlExporter.Build(model, bad); } catch (ArgumentOutOfRangeException) { failed = true; }
            Check(failed, "Invalid print size was accepted");
        }
        var empty = new JObject { ["textures"] = new JObject(), ["elements"] = new JArray() };
        var noSolid = false;
        try { BlueprintStlExporter.Build(empty); } catch (InvalidDataException) { noSolid = true; }
        Check(noSolid, "An empty STL must not be produced");
        var many = Cubes(Enumerable.Range(0, 130).Select(i => (new Vector3(i * 16, 0, 0), new Vector3((i + 1) * 16, 16, 16))).ToArray());
        CheckGlb(many);
        var flat = Cubes((Vector3.Zero, new Vector3(16, 16, 0)));
        noSolid = false;
        try { BlueprintStlExporter.Build(flat); } catch (InvalidDataException) { noSolid = true; }
        Check(noSolid, "Flat planes must not be presented as printable solids");
        foreach (var contact in new[] { new Vector3(16, 16, 0), new Vector3(16, 16, 16) })
        {
            var touching = Cubes((Vector3.Zero, new Vector3(16)), (contact, contact + new Vector3(16)));
            var rejected = false;
            try { BlueprintStlExporter.Build(touching, 100, 16); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Non-manifold point/edge contacts must be rejected");
        }
        var rotated = Cubes((Vector3.Zero, new Vector3(16, 8, 4)));
        rotated["elements"]![0]!["rotationY"] = 90;
        CheckStl(BlueprintStlExporter.Build(rotated, 100, 16), new Vector3(25, 100, 50), 125000);
        var fixtures = Environment.GetEnvironmentVariable("BUILDWRIGHT_MESH_FIXTURES");
        if (!string.IsNullOrEmpty(fixtures))
        {
            Directory.CreateDirectory(fixtures);
            File.WriteAllBytes(Path.Combine(fixtures, "blocks_blender.glb"), glb);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var defaultPrint = BlueprintStlExporter.Build(model);
            File.WriteAllBytes(Path.Combine(fixtures, "blocks_print.stl"), defaultPrint);
            CheckStl(defaultPrint, new Vector3(100, 50, 50), 250000);
            Console.WriteLine($"Default 128-grid STL: {BinaryPrimitives(defaultPrint):N0} triangles, {defaultPrint.Length:N0} bytes, {timer.ElapsedMilliseconds} ms including verification.");
        }
        Console.WriteLine("GLB/STL smoke tests passed (engine geometry, UVs, embedded textures, solid union, manifold edges, normals, volume and millimetres).");
    }

    public static byte[] CheckGlb(JObject model)
    {
        var original = model.ToString();
        var bytes = BlueprintGlbExporter.Build(model, _ => Png, 2);
        Check(model.ToString() == original, "Mesh export mutated its source shape");
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        Check(reader.ReadUInt32() == 0x46546c67 && reader.ReadUInt32() == 2 && reader.ReadInt32() == bytes.Length, "Invalid GLB header");
        var length = reader.ReadInt32();
        Check(length % 4 == 0 && reader.ReadUInt32() == 0x4e4f534a, "Invalid GLB JSON chunk");
        var json = JObject.Parse(Encoding.UTF8.GetString(reader.ReadBytes(length)));
        var binLength = reader.ReadInt32();
        Check(binLength % 4 == 0 && reader.ReadUInt32() == 0x004e4942, "Invalid GLB BIN chunk");
        var binary = reader.ReadBytes(binLength);
        Check(stream.Position == bytes.Length && binary.Length == binLength, "Invalid GLB chunk lengths");
        var views = (JArray)json["bufferViews"]!;
        foreach (var view in views.OfType<JObject>()) Check(view.Value<int>("byteOffset") % 4 == 0 && view.Value<int>("byteOffset") + view.Value<int>("byteLength") <= binary.Length, "Invalid GLB buffer view");
        foreach (var image in json["images"]!)
        {
            var view = views[image.Value<int>("bufferView")]!;
            Check(binary.AsSpan(view.Value<int>("byteOffset"), view.Value<int>("byteLength")).SequenceEqual(Png), "Texture was not embedded intact");
        }
        float[] Attribute(JToken primitive, string key, int width)
        {
            var accessor = json["accessors"]![primitive["attributes"]!.Value<int>(key)]!;
            var view = views[accessor.Value<int>("bufferView")]!;
            var count = accessor.Value<int>("count") * width;
            var offset = view.Value<int>("byteOffset");
            return Enumerable.Range(0, count).Select(i => BitConverter.ToSingle(binary, offset + i * 4)).ToArray();
        }
        var paths = ((JObject)model["textures"]!).Properties().ToArray();
        new ShapeTesselator(null!, null!, null!, null!).TesselateShape("GLB oracle", model.ToObject<Shape>()!, out var expected, new Textures(paths.Select(p => p.Name)));
        try
        {
            static string Point(float x, float y, float z, float u, float v, string texture) => $"{MathF.Round(x, 5):F5}/{MathF.Round(y, 5):F5}/{MathF.Round(z, 5):F5}/{MathF.Round(u, 5):F5}/{MathF.Round(v, 5):F5}/{texture}";
            var actualVertices = new List<string>();
            var expectedVertices = new List<string>();
            foreach (var primitive in json["meshes"]![0]!["primitives"]!)
            {
                var positions = Attribute(primitive, "POSITION", 3);
                var uv = Attribute(primitive, "TEXCOORD_0", 2);
                var normals = Attribute(primitive, "NORMAL", 3);
                var texture = json["materials"]![primitive.Value<int>("material")]!.Value<string>("name")!;
                Check(uv.Length / 2 == positions.Length / 3 && normals.Length == positions.Length, "GLB attribute lengths differ");
                for (var i = 0; i < positions.Length / 3; i++) actualVertices.Add(Point(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2], uv[i * 2], uv[i * 2 + 1], texture));
                for (var i = 0; i < normals.Length; i += 3) Check(Math.Abs(new Vector3(normals[i], normals[i + 1], normals[i + 2]).Length() - 1) < 1e-5, "Invalid GLB normal");
                Check(uv.All(float.IsFinite), "Invalid UVs");
            }
            for (var i = 0; i < expected.IndicesCount; i++)
            {
                var index = expected.Indices[i] * 3;
                var uvIndex = expected.Indices[i] * 2;
                var texture = paths[expected.TextureIds[expected.TextureIndices[i / 6]] - 1].Value.Value<string>()!;
                expectedVertices.Add(Point((expected.xyz[index] - .5f) * 2, expected.xyz[index + 1] * 2, (expected.xyz[index + 2] - .5f) * 2, expected.Uv[uvIndex], expected.Uv[uvIndex + 1], texture));
            }
            Check(actualVertices.Order().SequenceEqual(expectedVertices.Order()), "GLB geometry, material assignment or UVs differ from game tessellation");
        }
        finally { expected.Dispose(); }
        return bytes;
    }

    private static uint BinaryPrimitives(byte[] bytes) => BitConverter.ToUInt32(bytes, 80);
    public static void CheckStl(byte[] bytes, Vector3 size, double expectedVolume)
    {
        var count = BinaryPrimitives(bytes);
        Check(bytes.Length == 84 + 50 * count, "Invalid binary STL length");
        var edges = new Dictionary<(Vector3, Vector3), int>();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        double volume = 0;
        Vector3 Read(int offset) => new(BitConverter.ToSingle(bytes, offset), BitConverter.ToSingle(bytes, offset + 4), BitConverter.ToSingle(bytes, offset + 8));
        void Edge(Vector3 a, Vector3 b)
        {
            var pair = (a, b);
            edges[pair] = edges.GetValueOrDefault(pair) + 1;
        }
        for (var i = 0; i < count; i++)
        {
            var offset = 84 + i * 50;
            var normal = Read(offset);
            var a = Read(offset + 12); var b = Read(offset + 24); var c = Read(offset + 36);
            var cross = Vector3.Cross(b - a, c - a);
            Check(cross.LengthSquared() > 0 && Vector3.Dot(Vector3.Normalize(cross), normal) > .9999f, "Degenerate/inverted STL normal");
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6d;
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        Check(edges.All(e => e.Value == 1 && edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) == 1), "STL boundary is not a closed oriented manifold");
        Check(min.Length() < 1e-5 && (max - size).Length() < .001, "STL millimetres/Z-up/build plate placement failed");
        Check(Math.Abs(volume - expectedVolume) < expectedVolume * .0001, "STL volume/winding/union failed");
    }
    private static JObject Cubes(params (Vector3 From, Vector3 To)[] cubes)
    {
        var textures = new JObject();
        var elements = new JArray();
        for (var i = 0; i < cubes.Length; i++)
        {
            textures["t" + i] = "test:texture" + i;
            var faces = new JObject();
            foreach (var facing in new[] { "north", "east", "south", "west", "up", "down" })
                faces[facing] = new JObject { ["texture"] = "#t" + i, ["uv"] = new JArray(0, 0, 16, 16), ["rotation"] = facing == "up" ? 90 : 0 };
            elements.Add(new JObject { ["name"] = "cube" + i, ["from"] = new JArray(cubes[i].From.X, cubes[i].From.Y, cubes[i].From.Z), ["to"] = new JArray(cubes[i].To.X, cubes[i].To.Y, cubes[i].To.Z), ["faces"] = faces });
        }
        return new JObject { ["textureWidth"] = 16, ["textureHeight"] = 16, ["textures"] = textures, ["elements"] = elements };
    }
    private sealed class Textures : ITexPositionSource
    {
        private readonly Dictionary<string, int> ids;
        public Textures(IEnumerable<string> keys) => ids = keys.Select((key, index) => (key, index)).ToDictionary(p => p.key, p => p.index + 1);
        public Size2i AtlasSize => new(1024, 1024);
        public TextureAtlasPosition this[string code] => new() { atlasTextureId = ids[code], x2 = 1, y2 = 1 };
    }
}
