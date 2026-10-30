using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Buildwright.Core;

public interface IBlueprintCodec
{
    BlueprintDocument Read(string path);
    void Write(string path, BlueprintDocument document, bool overwrite = false);
}

public sealed class BlueprintCodec : IBlueprintCodec
{
    private const string ManifestEntry = "manifest.json";
    private const string SchematicEntry = "schematic.json";
    private const string BlockEntitiesEntry = "blockentities.json";
    private const string ChiselEntry = "chisel.json";

    public BlueprintDocument Read(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return ReadJson(path);
        if (!extension.Equals(".vsbp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported blueprint extension: {extension}");

        using var archive = ZipFile.OpenRead(path);
        var schematic = ReadJsonEntry(archive, SchematicEntry);
        var manifest = TryReadJsonEntry(archive, ManifestEntry);
        var doc = new BlueprintDocument { Schematic = schematic };
        if (manifest != null) doc.Metadata = manifest.ToObject<BlueprintMetadata>() ?? new BlueprintMetadata();
        if (TryReadJsonEntry(archive, BlockEntitiesEntry) is { } be)
            doc.BlockEntities = be.ToObject<List<BlockEntityRecord>>() ?? new();
        if (TryReadJsonEntry(archive, ChiselEntry) is { } chisel)
            doc.ChiselData = chisel.ToObject<List<ChiselVoxelData>>() ?? new();
        NormalizeMetadata(doc);
        return doc;
    }

    public void Write(string path, BlueprintDocument document, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            if (!overwrite) throw new IOException($"Blueprint already exists: {path}");
            File.Copy(path, path + ".bak", true);
        }

        NormalizeMetadata(document);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, document.Schematic.ToString(Formatting.Indented));
            return;
        }

        var tempPath = path + ".tmp";
        if (File.Exists(tempPath)) File.Delete(tempPath);
        using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
        {
            WriteJsonEntry(archive, ManifestEntry, document.Metadata);
            WriteJsonEntry(archive, SchematicEntry, document.Schematic);
            if (document.BlockEntities.Count > 0) WriteJsonEntry(archive, BlockEntitiesEntry, document.BlockEntities);
            if (document.ChiselData.Count > 0) WriteJsonEntry(archive, ChiselEntry, document.ChiselData);
        }
        File.Move(tempPath, path, true);
    }

    private static BlueprintDocument ReadJson(string path)
    {
        var parsed = JObject.Parse(File.ReadAllText(path));
        if (parsed["schematic"] is JObject wrapped)
        {
            var doc = new BlueprintDocument { Schematic = wrapped };
            doc.Metadata.Name = Path.GetFileNameWithoutExtension(path);
            if (parsed["anchorOffset"]?.ToObject<Int3>() is { } offset) doc.Metadata.AnchorOffset = offset;
            doc.Metadata.AnchorFace = parsed["anchorFace"]?.Value<string>();
            NormalizeMetadata(doc);
            return doc;
        }

        var bare = new BlueprintDocument
        {
            Schematic = parsed,
            Metadata = new BlueprintMetadata { Name = Path.GetFileNameWithoutExtension(path) }
        };
        NormalizeMetadata(bare);
        return bare;
    }

    private static JObject ReadJsonEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Missing {name} in .vsbp");
        using var reader = new StreamReader(entry.Open());
        return JObject.Parse(reader.ReadToEnd());
    }

    private static JToken? TryReadJsonEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        return JToken.Parse(reader.ReadToEnd());
    }

    private static void WriteJsonEntry(ZipArchive archive, string name, object value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
    }

    private static void NormalizeMetadata(BlueprintDocument document)
    {
        document.Metadata ??= new BlueprintMetadata();
        document.Metadata.Dimensions ??= new Int3();
        document.Metadata.AnchorOffset ??= new Int3();
        document.Metadata.Dependencies ??= new();
        document.Metadata.Capabilities ??= new();
        document.BlockEntities ??= new();
        document.ChiselData ??= new();
        var schematic = document.Schematic;
        if (document.Metadata.Dimensions.X <= 0) document.Metadata.Dimensions.X = schematic["SizeX"]?.Value<int>() ?? 0;
        if (document.Metadata.Dimensions.Y <= 0) document.Metadata.Dimensions.Y = schematic["SizeY"]?.Value<int>() ?? 0;
        if (document.Metadata.Dimensions.Z <= 0) document.Metadata.Dimensions.Z = schematic["SizeZ"]?.Value<int>() ?? 0;
        if (string.IsNullOrWhiteSpace(document.Metadata.GameVersion)) document.Metadata.GameVersion = schematic["GameVersion"]?.Value<string>() ?? string.Empty;
        if (document.Metadata.BlockCount <= 0) document.Metadata.BlockCount = schematic["Indices"]?.Count() ?? 0;
        if (document.Metadata.MaterialCount <= 0) document.Metadata.MaterialCount = schematic["BlockCodes"]?.Count() ?? 0;
        if (schematic["BlockEntities"] is JObject { Count: > 0 } && !document.Metadata.Capabilities.Contains("blockentities"))
            document.Metadata.Capabilities.Add("blockentities");
        if (document.ChiselData.Count > 0 && !document.Metadata.Capabilities.Contains("chisel-voxels"))
            document.Metadata.Capabilities.Add("chisel-voxels");
        if (schematic["BlockCodes"] is JObject blockCodes)
        {
            foreach (var value in blockCodes.Properties().Select(property => property.Value.ToObject<string>()))
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var domain = value.Contains(':') ? value[..value.IndexOf(':')] : "game";
                if (!domain.Equals("game", StringComparison.OrdinalIgnoreCase)
                    && !document.Metadata.Dependencies.Contains(domain, StringComparer.OrdinalIgnoreCase))
                    document.Metadata.Dependencies.Add(domain);
            }
        }
        if (string.IsNullOrWhiteSpace(document.Metadata.SchematicSha256))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(document.Schematic.ToString(Formatting.None));
            document.Metadata.SchematicSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }
}
