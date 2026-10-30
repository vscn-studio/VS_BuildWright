using Buildwright.Core;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Buildwright.Client;

internal static class BlueprintItemModelExport
{
    public static string Export(ICoreClientAPI capi, string directory, BlueprintDocument document,
        BlueprintModelFormat format = BlueprintModelFormat.ItemShape)
    {
        var warnings = new HashSet<string>();
        var shapes = new Dictionary<string, JObject>();
        var chisels = document.ChiselData.GroupBy(c => (c.Position.X, c.Position.Y, c.Position.Z))
            .ToDictionary(g => g.Key, g => g.Last());
        var payloads = document.Schematic["BlockEntities"] as JObject;
        if (payloads != null)
        {
            foreach (var property in payloads.Properties())
            {
                if (!uint.TryParse(property.Name, out var packed)) continue;
                var position = ((int)(packed & 0x3ff), (int)((packed >> 20) & 0x3ff), (int)((packed >> 10) & 0x3ff));
                if (chisels.ContainsKey(position)) continue;
                // Native schematic JSON has no chisel sidecar; recover its saved voxel cuboids directly.
                var chisel = ReadChisel(property.Value.Value<string>() ?? "",
                    new Int3(position.Item1, position.Item2, position.Item3));
                if (chisel != null) chisels[position] = chisel;
                else warnings.Add(BuildwrightLang.Get("ui.model.static-entities"));
            }
        }
        foreach (var entity in document.BlockEntities)
            if (!chisels.ContainsKey((entity.Position.X, entity.Position.Y, entity.Position.Z)))
                warnings.Add(BuildwrightLang.Get("ui.model.static-entities"));
        if (chisels.Values.Any(c => c.DecorIds?.Any(id => id != 0) == true)
            || document.Schematic["DecorIds"] is JArray { Count: > 0 })
            warnings.Add(BuildwrightLang.Get("ui.model.decorations"));

        JObject LoadShape(AssetLocation location)
        {
            var path = location.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            if (shapes.TryGetValue(path.ToString(), out var cached)) return cached;
            var shape = capi.Assets.TryGet(path)?.ToObject<JObject>()
                ?? throw new InvalidDataException(BuildwrightLang.Get("ui.model.missing-shape", path));
            if (shape["textures"] is JObject textures)
                foreach (var texture in textures.Properties())
                {
                    var value = texture.Value.Value<string>();
                    if (value != null && !value.StartsWith('#') && !value.Contains(':'))
                        texture.Value = path.Domain + ":" + value;
                }
            shapes[path.ToString()] = shape;
            return shape;
        }

        BlueprintShapeBlock Resolve(string code)
        {
            var block = capi.World.GetBlock(new AssetLocation(code));
            if (block == null || block.Id == 0)
                throw new InvalidDataException(BuildwrightLang.Get("ui.model.missing-block", code));
            var textures = new Dictionary<string, string>(StringComparer.Ordinal);
            var textureRotations = new Dictionary<string, int>(StringComparer.Ordinal);
            if (block.Textures != null)
                foreach (var (key, texture) in block.Textures)
                {
                    if (texture.Base != null) textures[key] = texture.Base.ToString();
                    if (texture.Rotation != 0) textureRotations[key] = texture.Rotation;
                    if (texture.BlendedOverlays?.Length > 0 || texture.Rotation != 0 || texture.Alpha != 255)
                        warnings.Add(BuildwrightLang.Get("ui.model.base-textures"));
                }
            var parts = new List<BlueprintShapePart>();
            void AddPart(CompositeShape composite)
            {
                if (composite.Format != EnumShapeFormat.VintageStory || composite.VoxelizeTexture)
                    throw new InvalidDataException(BuildwrightLang.Get("ui.model.unsupported-shape", code));
                if (composite.Base != null) parts.Add(new BlueprintShapePart(LoadShape(composite.Base), composite));
                foreach (var overlay in composite.Overlays ?? []) AddPart(overlay);
            }
            if (block.DrawType == EnumDrawType.Empty)
                warnings.Add(BuildwrightLang.Get("ui.model.empty-block", code));
            else if (block.Shape?.Base != null)
            {
                AddPart(block.Shape);
                if (block.DrawType is not (EnumDrawType.JSON or EnumDrawType.Cube or EnumDrawType.Transparent))
                    warnings.Add(BuildwrightLang.Get("ui.model.special-rendering"));
            }
            else if (block.DrawType is EnumDrawType.Cube or EnumDrawType.Transparent or EnumDrawType.TopSoil)
            {
                AddPart(new CompositeShape { Base = new AssetLocation("game:block/basic/cube") });
                if (block.DrawType == EnumDrawType.TopSoil) warnings.Add(BuildwrightLang.Get("ui.model.special-rendering"));
            }
            else throw new InvalidDataException(BuildwrightLang.Get("ui.model.unsupported-shape", code));
            return new BlueprintShapeBlock(parts, textures, textureRotations);
        }

        var exportDocument = new BlueprintDocument
        {
            Metadata = document.Metadata, Schematic = document.Schematic,
            ChiselData = chisels.Values.ToList(), BlockEntities = document.BlockEntities
        };
        var exporter = new BlueprintShapeExporter(Resolve);
        string output;
        if (format is BlueprintModelFormat.BlenderGlb or BlueprintModelFormat.PrintStl)
        {
            var model = exporter.Build(exportDocument);
            byte[] LoadTexture(string path)
            {
                var location = new AssetLocation(path).WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
                return capi.Assets.TryGet(location)?.Data
                    ?? throw new InvalidDataException(BuildwrightLang.Get("ui.model.missing-texture", location));
            }
            var size = BlueprintEditor.Dimensions(exportDocument);
            var data = format == BlueprintModelFormat.BlenderGlb
                ? BlueprintGlbExporter.Build(model, LoadTexture, Math.Max(size.X, Math.Max(size.Y, size.Z)))
                : BlueprintStlExporter.Build(model, BlueprintPrintOptions.LongestEdgeMm, BlueprintPrintOptions.Resolution);
            output = BlueprintExporter.ExportBinary(directory, document.Metadata.Name
                + (format == BlueprintModelFormat.BlenderGlb ? "_blender" : "_print"),
                format == BlueprintModelFormat.BlenderGlb ? ".glb" : ".stl", data);
        }
        else output = exporter.Export(directory, exportDocument, format);
        var message = BuildwrightLang.Get("ui.library.exported", output);
        message += "\n" + BuildwrightLang.Get("ui.model.material-rights");
        if (format == BlueprintModelFormat.Blockbench) message += "\n" + BuildwrightLang.Get("ui.model.blockbench-help");
        if (format == BlueprintModelFormat.BlenderGlb) message += "\n" + BuildwrightLang.Get("ui.model.blender-help");
        if (format == BlueprintModelFormat.PrintStl) message += "\n" + BuildwrightLang.Get("ui.model.print-help", BlueprintPrintOptions.LongestEdgeMm, BlueprintPrintOptions.Resolution);
        if (warnings.Count > 0) message += "\n" + BuildwrightLang.Get("ui.model.notes", string.Join(" ", warnings));
        return message;
    }

    internal static ChiselVoxelData? ReadChisel(string payload, Int3 position)
    {
        var tree = BlockEntityCapture.Decode(payload)
            ?? throw new InvalidDataException("Cannot decode schematic block entity data.");
        var cuboids = tree["cuboids"] switch
        {
            IntArrayAttribute integers => integers.AsUint,
            LongArrayAttribute integers => integers.AsUint,
            _ => null
        };
        var materials = (tree["materials"] as IntArrayAttribute)?.value;
        return cuboids == null || materials == null ? null : new ChiselVoxelData
        {
            Position = position, BlockIds = materials, VoxelCuboids = cuboids,
            DecorIds = (tree["decorIds"] as IntArrayAttribute)?.value
        };
    }
}
