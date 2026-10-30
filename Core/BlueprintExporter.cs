namespace Buildwright.Core;

/// <summary>Exports a named blueprint without requiring a destination or overwriting earlier exports.</summary>
public static class BlueprintExporter
{
    public static string ExportBinary(string directory, string name, string extension, byte[] data)
    {
        var output = NextPath(directory, name, extension);
        var created = false;
        try
        {
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            created = true;
            stream.Write(data);
        }
        catch
        {
            if (created) File.Delete(output);
            throw;
        }
        return output;
    }

    public static string ExportJson(string directory, string name, Newtonsoft.Json.Linq.JObject json)
    {
        var output = NextPath(directory, name, ".json");
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(json.ToString(Newtonsoft.Json.Formatting.Indented));
        return output;
    }

    public static string Export(string directory, BlueprintDocument document, BlueprintFileKind kind)
    {
        var extension = kind == BlueprintFileKind.NativeJson ? ".json" : ".vsbp";
        var output = NextPath(directory, document.Metadata.Name, extension);
        new BlueprintCodec().Write(output, document, overwrite: false);
        return output;
    }

    private static string NextPath(string directory, string blueprintName, string extension)
    {
        Directory.CreateDirectory(directory);
        var name = BlueprintRepository.SanitizeName(blueprintName);
        var output = Path.Combine(directory, name + extension);
        for (var index = 2; File.Exists(output) || Directory.Exists(output); index++)
            output = Path.Combine(directory, $"{name} ({index}){extension}");
        return output;
    }
}
