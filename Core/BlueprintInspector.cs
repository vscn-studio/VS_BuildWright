using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Buildwright.Core;

public sealed class BlueprintInspection
{
    public string Name { get; init; } = string.Empty;
    public Int3 Dimensions { get; init; } = new();
    public int BlockCount { get; init; }
    public int MaterialCount { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public bool VersionCompatible { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingDependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnknownBlocks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MaterialRequirement> Materials { get; init; } = Array.Empty<MaterialRequirement>();
}

public static class BlueprintInspector
{
    public static BlueprintInspection Inspect(BlueprintDocument document, IWorldAccessor world, MatchingMode mode)
    {
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new List<string?>();
        if (document.Schematic["BlockCodes"] is Newtonsoft.Json.Linq.JObject blockCodes)
        {
            foreach (var property in blockCodes.Properties())
            {
                var code = property.Value.ToObject<string>();
                if (string.IsNullOrWhiteSpace(code)) continue;
                codes.Add(code);
                if (world.GetBlock(new AssetLocation(code)) == null) unknown.Add(code);
            }
        }

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game", "vssurvivalmod", "vsessentials", "buildwright"
        };
        try
        {
            foreach (var mod in world.Api.ModLoader.Mods)
                if (!string.IsNullOrWhiteSpace(mod.Info?.ModID)) enabled.Add(mod.Info.ModID);
        }
        catch { }

        var missingDeps = document.Metadata.Dependencies
            .Where(dep => !enabled.Contains(dep))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dep => dep, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var current = GameVersion.ShortGameVersion;
        var versionCompatible = string.IsNullOrWhiteSpace(document.Metadata.GameVersion)
            || document.Metadata.GameVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(2)
                .SequenceEqual(current.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(2));

        return new BlueprintInspection
        {
            Name = document.Metadata.Name,
            Dimensions = document.Metadata.Dimensions,
            BlockCount = document.Metadata.BlockCount > 0 ? document.Metadata.BlockCount : codes.Count,
            MaterialCount = document.Metadata.MaterialCount > 0 ? document.Metadata.MaterialCount : codes.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            GameVersion = document.Metadata.GameVersion,
            VersionCompatible = versionCompatible,
            Dependencies = document.Metadata.Dependencies.ToArray(),
            MissingDependencies = missingDeps,
            UnknownBlocks = unknown.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            Materials = MaterialCounter.CountRequired(codes, mode)
        };
    }
}
