using System.Numerics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Buildwright.Core;

/// <summary>Self-contained glTF 2.0 binary, with engine geometry/UVs and embedded base textures.</summary>
public static class BlueprintGlbExporter
{
    public static byte[] Build(JObject model, System.Func<string, byte[]> loadTexture, float blocksPerModelUnit = 1)
    {
        if (!float.IsFinite(blocksPerModelUnit) || blocksPerModelUnit <= 0) throw new ArgumentOutOfRangeException(nameof(blocksPerModelUnit));
        var paths = ((JObject)model["textures"]!).Properties().ToArray();
        var views = new JArray();
        var accessors = new JArray();
        var materials = new JArray();
        var images = new JArray();
        var textures = new JArray();
        var primitives = new JArray();
        using var binary = new MemoryStream();
        using var writer = new BinaryWriter(binary, Encoding.UTF8, true);
        int View(Action write, int? target = null)
        {
            while (binary.Position % 4 != 0) writer.Write((byte)0);
            var start = (int)binary.Position;
            write();
            var view = new JObject { ["buffer"] = 0, ["byteOffset"] = start, ["byteLength"] = (int)binary.Position - start };
            if (target != null) view["target"] = target;
            views.Add(view);
            return views.Count - 1;
        }
        int Attribute(float[] values, int width, string type, bool bounds = false)
        {
            var view = View(() => { foreach (var value in values) writer.Write(value); }, 34962);
            var accessor = new JObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = values.Length / width, ["type"] = type };
            if (bounds)
            {
                accessor["min"] = new JArray(Enumerable.Range(0, width).Select(axis => Enumerable.Range(0, values.Length / width).Min(i => values[i * width + axis])));
                accessor["max"] = new JArray(Enumerable.Range(0, width).Select(axis => Enumerable.Range(0, values.Length / width).Max(i => values[i * width + axis])));
            }
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        // MeshData stores texture indices as bytes. Batch material sets to avoid that engine limit.
        foreach (var batch in paths.Chunk(128))
        {
            var subset = (JObject)model.DeepClone();
            var source = new TextureSource(batch.Select(p => p.Name));
            foreach (var faces in subset.Descendants().OfType<JObject>().Where(o => o["faces"] is JObject).Select(o => (JObject)o["faces"]!))
                foreach (var face in faces.Properties().Select(p => p.Value).OfType<JObject>())
                    if (!source.Ids.ContainsKey(face["texture"]?.Value<string>()?.TrimStart('#') ?? "")) face["enabled"] = false;
            var shape = subset.ToObject<Shape>()!;
            new ShapeTesselator(null!, null!, null!, null!).TesselateShape("Buildwright GLB", shape, out var mesh, source);
            try
            {
                for (var material = 0; material < batch.Length; material++)
                {
                    var positions = new List<float>();
                    var normals = new List<float>();
                    var uv = new List<float>();
                    // Engine Shape tessellation emits independent quads, each with six triangle indices.
                    for (var triangle = 0; triangle < mesh.IndicesCount; triangle += 3)
                    {
                        if (mesh.TextureIds[mesh.TextureIndices[triangle / 6]] != material + 1) continue;
                        var vertices = new Vector3[3];
                        for (var v = 0; v < 3; v++)
                        {
                            var index = mesh.Indices[triangle + v];
                            vertices[v] = new Vector3(mesh.xyz[index * 3] - .5f, mesh.xyz[index * 3 + 1], mesh.xyz[index * 3 + 2] - .5f) * blocksPerModelUnit;
                        }
                        var cross = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]);
                        if (cross.LengthSquared() < 1e-20f) continue;
                        var normal = Vector3.Normalize(cross);
                        for (var v = 0; v < 3; v++)
                        {
                            var index = mesh.Indices[triangle + v];
                            positions.AddRange([vertices[v].X, vertices[v].Y, vertices[v].Z]);
                            normals.AddRange([normal.X, normal.Y, normal.Z]);
                            uv.AddRange([mesh.Uv[index * 2], mesh.Uv[index * 2 + 1]]);
                        }
                    }
                    if (positions.Count == 0) continue;
                    var png = loadTexture(batch[material].Value.Value<string>()!);
                    if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        throw new InvalidDataException("Model texture is not PNG: " + batch[material].Value);
                    var imageView = View(() => writer.Write(png));
                    images.Add(new JObject { ["bufferView"] = imageView, ["mimeType"] = "image/png", ["name"] = batch[material].Value.DeepClone() });
                    textures.Add(new JObject { ["sampler"] = 0, ["source"] = images.Count - 1 });
                    materials.Add(new JObject
                    {
                        ["name"] = batch[material].Value.DeepClone(), ["doubleSided"] = true, ["alphaMode"] = "MASK", ["alphaCutoff"] = .5,
                        ["pbrMetallicRoughness"] = new JObject
                        {
                            ["baseColorTexture"] = new JObject { ["index"] = textures.Count - 1 }, ["metallicFactor"] = 0, ["roughnessFactor"] = 1
                        }
                    });
                    primitives.Add(new JObject
                    {
                        ["attributes"] = new JObject { ["POSITION"] = Attribute(positions.ToArray(), 3, "VEC3", true), ["NORMAL"] = Attribute(normals.ToArray(), 3, "VEC3"), ["TEXCOORD_0"] = Attribute(uv.ToArray(), 2, "VEC2") },
                        ["material"] = materials.Count - 1, ["mode"] = 4
                    });
                }
            }
            finally { mesh.Dispose(); }
        }
        if (primitives.Count == 0) throw new InvalidDataException("Blueprint contains no exportable triangles.");
        var json = new JObject
        {
            ["asset"] = new JObject
            {
                ["version"] = "2.0",
                ["generator"] = "Buildwright 1.0.3",
                ["copyright"] = "Geometry and textures retain their respective owners' rights. Export grants no additional rights to third-party assets; obtain permissions and retain required credits before redistribution or commercial use."
            },
            ["scene"] = 0, ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
            ["nodes"] = new JArray(new JObject { ["mesh"] = 0, ["name"] = "Blueprint" }),
            ["meshes"] = new JArray(new JObject { ["primitives"] = primitives }),
            ["buffers"] = new JArray(new JObject { ["byteLength"] = (int)binary.Length }),
            ["bufferViews"] = views, ["accessors"] = accessors, ["materials"] = materials, ["textures"] = textures, ["images"] = images,
            ["samplers"] = new JArray(new JObject { ["magFilter"] = 9728, ["minFilter"] = 9728, ["wrapS"] = 10497, ["wrapT"] = 10497 })
        };
        var jsonBytes = Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
        using var output = new MemoryStream();
        using var glb = new BinaryWriter(output);
        var jsonLength = (jsonBytes.Length + 3) & ~3;
        var binLength = ((int)binary.Length + 3) & ~3;
        glb.Write(0x46546c67u); glb.Write(2u); glb.Write(12 + 8 + jsonLength + 8 + binLength);
        glb.Write(jsonLength); glb.Write(0x4e4f534au); glb.Write(jsonBytes);
        while (output.Position % 4 != 0) glb.Write((byte)32);
        glb.Write(binLength); glb.Write(0x004e4942u); glb.Write(binary.ToArray());
        while (output.Position % 4 != 0) glb.Write((byte)0);
        return output.ToArray();
    }

    private sealed class TextureSource : ITexPositionSource
    {
        public Dictionary<string, int> Ids { get; }
        public TextureSource(IEnumerable<string> keys) => Ids = keys.Select((key, index) => (key, index)).ToDictionary(p => p.key, p => p.index + 1);
        public Size2i AtlasSize => new(1024, 1024);
        public TextureAtlasPosition this[string code] => new() { atlasTextureId = Ids.GetValueOrDefault(code), x1 = 0, y1 = 0, x2 = 1, y2 = 1 };
    }
}
