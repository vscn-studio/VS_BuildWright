using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace Buildwright.Core;

/// <summary>A resolved block's static geometry and texture asset paths, without atlas coordinates.</summary>
public sealed record BlueprintShapePart(JObject Shape, CompositeShape Transform);

public sealed record BlueprintShapeBlock(
    IReadOnlyList<BlueprintShapePart> Parts, IReadOnlyDictionary<string, string> Textures,
    IReadOnlyDictionary<string, int>? TextureRotations = null);

/// <summary>Converts schematic cells to a standalone, uniformly scaled Vintage Story Shape.</summary>
public sealed class BlueprintShapeExporter
{
    private static readonly string[] Facings = ["north", "east", "south", "west", "up", "down"];
    private readonly System.Func<string, BlueprintShapeBlock> resolveBlock;
    private readonly Dictionary<string, BlueprintShapeBlock> blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly JObject textures = new();
    private readonly JObject textureSizes = new();
    private int elementId;

    public BlueprintShapeExporter(System.Func<string, BlueprintShapeBlock> resolveBlock) => this.resolveBlock = resolveBlock;

    public JObject Build(BlueprintDocument document)
    {
        textures.RemoveAll();
        textureSizes.RemoveAll();
        blocks.Clear();
        elementId = 0;
        var schematic = document.Schematic;
        var indices = schematic["Indices"]?.ToObject<uint[]>() ?? [];
        var ids = schematic["BlockIds"]?.ToObject<int[]>() ?? [];
        if (indices.Length != ids.Length) throw new InvalidDataException("Schematic cell indices and block IDs differ in length.");
        var codes = schematic["BlockCodes"]?.ToObject<Dictionary<int, string>>() ?? new();
        var chisels = document.ChiselData.GroupBy(c => (c.Position.X, c.Position.Y, c.Position.Z))
            .ToDictionary(g => g.Key, g => g.Last());
        var size = BlueprintEditor.Dimensions(document);
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) throw new InvalidDataException("Blueprint dimensions must be positive.");
        var cells = new JArray();
        for (var i = 0; i < indices.Length; i++)
        {
            var x = (int)(indices[i] & 0x3ff);
            var y = (int)((indices[i] >> 20) & 0x3ff);
            var z = (int)((indices[i] >> 10) & 0x3ff);
            if (!codes.TryGetValue(ids[i], out var code)) throw new InvalidDataException($"Missing block code for ID {ids[i]}.");
            if (code is "game:air" or "air") continue;
            if (x >= size.X || y >= size.Y || z >= size.Z) throw new InvalidDataException("Cell lies outside blueprint dimensions.");
            var children = new JArray();
            if (chisels.TryGetValue((x, y, z), out var chisel))
                AddChisel(children, chisel, codes);
            else
            {
                var block = Resolve(code);
                foreach (var part in block.Parts) children.Add(ConvertPart(part, block));
            }
            if (children.Count > 0) cells.Add(Group($"cell_{x}_{y}_{z}_{i}", [x * 16d, y * 16d, z * 16d], children));
        }
        if (cells.Count == 0 || textures.Count == 0) throw new InvalidDataException("Blueprint contains no exportable model elements.");
        var scale = 1d / Math.Max(size.X, Math.Max(size.Y, size.Z));
        var scaled = Group("blueprint_scale", [-size.X * 8d, 0, -size.Z * 8d], cells);
        scaled["scaleX"] = scaled["scaleY"] = scaled["scaleZ"] = scale;
        return new JObject
        {
            ["textureWidth"] = 16, ["textureHeight"] = 16,
            ["textures"] = textures.DeepClone(), ["textureSizes"] = textureSizes.DeepClone(),
            ["elements"] = new JArray(Group("blueprint", [8, 0, 8], new JArray(scaled)))
        };
    }

    public string Export(string directory, BlueprintDocument document, BlueprintModelFormat format = BlueprintModelFormat.ItemShape)
    {
        if (format is not (BlueprintModelFormat.ItemShape or BlueprintModelFormat.Blockbench))
            throw new ArgumentException("Use the binary model exporter for GLB/STL.", nameof(format));
        // Resolve everything before creating the destination, so a failed conversion leaves no partial file.
        var model = Build(document);
        if (format == BlueprintModelFormat.Blockbench) model = BlueprintBlockbenchExporter.Build(model);
        var suffix = format == BlueprintModelFormat.Blockbench ? "_blockbench" : "_model";
        return BlueprintExporter.ExportJson(directory, document.Metadata.Name + suffix, model);
    }

    private BlueprintShapeBlock Resolve(string code)
    {
        if (!blocks.TryGetValue(code, out var block)) blocks[code] = block = resolveBlock(code);
        return block;
    }

    private JObject ConvertPart(BlueprintShapePart part, BlueprintShapeBlock block)
    {
        var elements = part.Shape["elements"] as JArray ?? throw new InvalidDataException("Shape has no elements.");
        var selected = part.Transform.SelectiveElements;
        var ignored = part.Transform.IgnoreElements;
        var count = part.Transform.QuantityElements is > 0 ? part.Transform.QuantityElements.Value : int.MaxValue;
        var children = new JArray();
        foreach (var element in elements.OfType<JObject>())
        {
            if (ConvertElement(element, part.Shape, block, selected, ignored, "", ref count) is { } converted)
                children.Add(converted);
        }
        // The engine scales around (8,0,8), rotates around (8,8,8), then translates.
        var transform = part.Transform;
        var scaled = Group("part_scale_" + elementId++, [-8, 0, -8], children);
        scaled["scaleX"] = scaled["scaleY"] = scaled["scaleZ"] = transform.Scale;
        var rotated = Group("part_" + elementId++, [0, -8, 0], new JArray(scaled));
        rotated["rotationX"] = transform.rotateX;
        rotated["rotationY"] = transform.rotateY;
        rotated["rotationZ"] = transform.rotateZ;
        return Group("part_origin_" + elementId++,
            [8 + transform.offsetX * 16, 8 + transform.offsetY * 16, 8 + transform.offsetZ * 16], new JArray(rotated));
    }

    private JObject? ConvertElement(JObject source, JObject shape, BlueprintShapeBlock block,
        string[]? selected, string[]? ignored, string parentPath, ref int remaining)
    {
        if (remaining-- <= 0) return null;
        var name = source["name"]?.Value<string>() ?? "";
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        static bool Match(string path, string pattern) => path == pattern || pattern == "*"
            || pattern.EndsWith('*') && path.StartsWith(pattern[..^1], StringComparison.Ordinal);
        if (ignored?.Any(pattern => Match(path, pattern)) == true) return null;
        if (selected != null && !selected.Any(pattern => Match(path, pattern)
            || pattern.StartsWith(path + "/", StringComparison.Ordinal))) return null;
        var result = (JObject)source.DeepClone();
        result["name"] = "element_" + elementId++;
        result.Remove("stepParentName");
        result.Remove("attachmentpoints");
        result.Remove("attachmentPoints");
        var faces = result["faces"] as JObject;
        if (faces != null)
        {
            foreach (var faceProperty in faces.Properties())
            {
                if (faceProperty.Value is not JObject face) continue;
                if (face["enabled"]?.Value<bool>() == false) continue;
                var facing = faceProperty.Name;
                var key = face["texture"]?.Value<string>()?.TrimStart('#') ?? "all";
                var texturePath = ResolveTexture(key, block, shape);
                var dimensions = ResolveTextureDimensions(key, shape);
                face["texture"] = "#" + RegisterTexture(texturePath, dimensions);
                if (face["uv"] == null && CreateAutoUv(result, facing) is { } autoUv)
                    face["uv"] = autoUv;
                var textureRotation = ResolveTextureRotation(key, block, shape);
                if (textureRotation != 0)
                {
                    var faceRotation = face["rotation"]?.Value<int>() ?? 0;
                    face["rotation"] = NormalizeRotation(faceRotation + textureRotation);
                }
                // Item models are static; terrain wind flags would deform them in the world.
                face.Remove("windMode");
                face.Remove("windData");
            }
        }
        if (source["children"] is JArray originalChildren)
        {
            var children = new JArray();
            foreach (var child in originalChildren.OfType<JObject>())
                if (ConvertElement(child, shape, block, selected, ignored, path, ref remaining) is { } converted) children.Add(converted);
            result["children"] = children;
        }
        return result;
    }

    private string RegisterTexture(string path, int[] dimensions)
    {
        foreach (var property in textures.Properties())
            if (property.Value.Value<string>() == path && JToken.DeepEquals(textureSizes[property.Name], new JArray(dimensions)))
                return property.Name;
        var key = "texture_" + textures.Count;
        textures[key] = path;
        textureSizes[key] = new JArray(dimensions);
        return key;
    }

    private static string ResolveTexture(string key, BlueprintShapeBlock block, JObject? shape)
    {
        if (block.Textures.TryGetValue(key, out var path) || block.Textures.TryGetValue("all", out path)) return path;
        var visited = new HashSet<string>();
        while (visited.Add(key))
        {
            path = shape?["textures"]?[key]?.Value<string>();
            if (path == null) break;
            if (!path.StartsWith('#')) return new AssetLocation(path).ToString();
            key = path[1..];
            if (block.Textures.TryGetValue(key, out path)) return path;
        }
        throw new InvalidDataException($"Cannot resolve model texture #{key}.");
    }

    private static int ResolveTextureRotation(string key, BlueprintShapeBlock block, JObject? shape)
    {
        if (block.TextureRotations == null) return 0;
        var visited = new HashSet<string>();
        while (visited.Add(key))
        {
            if (block.TextureRotations.TryGetValue(key, out var rotation)) return NormalizeRotation(rotation);
            if (block.TextureRotations.TryGetValue("all", out rotation)) return NormalizeRotation(rotation);
            var alias = shape?["textures"]?[key]?.Value<string>();
            if (string.IsNullOrEmpty(alias) || !alias.StartsWith('#')) break;
            key = alias[1..];
        }
        return 0;
    }

    private static int[] ResolveTextureDimensions(string key, JObject? shape)
    {
        var fallback = new[] { shape?["textureWidth"]?.Value<int>() ?? 16, shape?["textureHeight"]?.Value<int>() ?? 16 };
        if (shape == null) return fallback;
        var visited = new HashSet<string>();
        while (visited.Add(key))
        {
            if (shape["textureSizes"]?[key] is JArray size && size.Count >= 2)
                return [size[0]!.Value<int>(), size[1]!.Value<int>()];
            var alias = shape["textures"]?[key]?.Value<string>();
            if (string.IsNullOrEmpty(alias) || !alias.StartsWith('#')) break;
            key = alias[1..];
        }
        return fallback;
    }

    private static JArray? CreateAutoUv(JObject element, string facing)
    {
        if (element["from"] is not JArray from || element["to"] is not JArray to || from.Count < 3 || to.Count < 3)
            return null;
        var dx = to[0]!.Value<double>() - from[0]!.Value<double>();
        var dy = to[1]!.Value<double>() - from[1]!.Value<double>();
        var dz = to[2]!.Value<double>() - from[2]!.Value<double>();
        var (width, height) = facing.ToLowerInvariant() switch
        {
            "up" or "down" => (dx, dz),
            "east" or "west" => (dz, dy),
            "north" or "south" => (dx, dy),
            _ => (0d, 0d)
        };
        return width == 0d && height == 0d ? null : new JArray(0d, 0d, width, height);
    }

    private static int NormalizeRotation(int rotation) => ((rotation % 360) + 360) % 360;

    private void AddChisel(JArray children, ChiselVoxelData data, Dictionary<int, string> codes)
    {
        foreach (var packed in data.VoxelCuboids)
        {
            var material = (int)(packed >> 24);
            if (material >= data.BlockIds.Length || !codes.TryGetValue(data.BlockIds[material], out var code))
                throw new InvalidDataException("Cannot resolve a chiseled block material.");
            if (code is "game:air" or "air") continue;
            var block = Resolve(code);
            double x0 = packed & 15, y0 = (packed >> 4) & 15, z0 = (packed >> 8) & 15;
            double x1 = ((packed >> 12) & 15) + 1, y1 = ((packed >> 16) & 15) + 1, z1 = ((packed >> 20) & 15) + 1;
            if (x1 <= x0 || y1 <= y0 || z1 <= z0) throw new InvalidDataException("Invalid chisel cuboid bounds.");
            var faces = new JObject();
            foreach (var facing in Facings)
            {
                var shape = block.Parts.FirstOrDefault()?.Shape;
                var textureKey = ResolveChiselTextureKey(facing, block, shape);
                var path = ResolveTexture(textureKey, block, shape);
                var uv = facing switch
                {
                    "north" => new JArray(16 - x1, 16 - y1, 16 - x0, 16 - y0),
                    "east" => new JArray(16 - z1, 16 - y1, 16 - z0, 16 - y0),
                    "south" => new JArray(x0, 16 - y1, x1, 16 - y0),
                    "west" => new JArray(z0, 16 - y1, z1, 16 - y0),
                    "up" => new JArray(x0, z0, x1, z1),
                    _ => new JArray(x0, 16 - z1, x1, 16 - z0)
                };
                var textureRotation = ResolveTextureRotation(textureKey, block, shape);
                RotateChiselUv(uv, textureRotation);
                var face = new JObject { ["texture"] = "#" + RegisterTexture(path, [16, 16]), ["uv"] = uv };
                if (textureRotation != 0) face["rotation"] = textureRotation;
                faces[facing] = face;
            }
            children.Add(new JObject
            {
                ["name"] = "voxel_" + elementId++, ["from"] = new JArray(x0, y0, z0),
                ["to"] = new JArray(x1, y1, z1), ["faces"] = faces
            });
        }
    }

    private static string ResolveChiselTextureKey(string facing, BlueprintShapeBlock block, JObject? shape)
    {
        // TextureAtlasManager expands these aliases to face keys at runtime. Keep the
        // fallback here as well so exports remain correct for unexpanded test/assets.
        var candidates = facing switch
        {
            "up" or "down" => new[] { facing, "verticals", "all" },
            _ => new[] { facing, "horizontals", "sides", "all" }
        };
        foreach (var candidate in candidates)
        {
            if (block.Textures.ContainsKey(candidate) || shape?["textures"]?[candidate] != null)
                return candidate;
        }
        return facing;
    }

    private static void RotateChiselUv(JArray uv, int rotation)
    {
        rotation = NormalizeRotation(rotation);
        if (rotation == 0) return;
        var u0 = uv[0]!.Value<double>();
        var v0 = uv[1]!.Value<double>();
        var u1 = uv[2]!.Value<double>();
        var v1 = uv[3]!.Value<double>();
        switch (rotation)
        {
            case 90:
                // Rotate the selected rectangle in the 16x16 source image. Using
                // the opposite corners preserves the face orientation while swapping
                // width and height for Blockbench's face rotation.
                uv[0] = v0;
                uv[1] = 16d - u1;
                uv[2] = v1;
                uv[3] = 16d - u0;
                break;
            case 180:
                uv[0] = 16d - u1;
                uv[1] = 16d - v1;
                uv[2] = 16d - u0;
                uv[3] = 16d - v0;
                break;
            case 270:
                uv[0] = 16d - v1;
                uv[1] = u0;
                uv[2] = 16d - v0;
                uv[3] = u1;
                break;
        }
    }

    private static JObject Group(string name, double[] from, JArray children) => new()
    {
        ["name"] = name, ["from"] = new JArray(from), ["to"] = new JArray(from),
        ["children"] = children
    };
}
