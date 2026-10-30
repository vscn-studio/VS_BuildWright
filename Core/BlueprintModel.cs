using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Buildwright.Api;

namespace Buildwright.Core;

public enum BlueprintFileKind
{
    NativeJson,
    BuildwrightZip
}

public enum MirrorAxis
{
    None,
    X,
    Y,
    Z
}

public enum MatchingMode
{
    Loose,
    Medium,
    Strict
}

public enum GhostDisplayMode
{
    Full,
    Missing,
    Wrong,
    Difference,
    Layer
}

public enum DiffKind
{
    Missing,
    Matched,
    Wrong,
    Extra,
    Unknown
}

public sealed class Int3
{
    [JsonProperty("x")] public int X { get; set; }
    [JsonProperty("y")] public int Y { get; set; }
    [JsonProperty("z")] public int Z { get; set; }

    public Int3() { }
    public Int3(int x, int y, int z) { X = x; Y = y; Z = z; }
    public Int3 Copy() => new(X, Y, Z);
    public override string ToString() => $"({X},{Y},{Z})";
}

public sealed class BlueprintMetadata
{
    [JsonProperty("formatVersion")] public int FormatVersion { get; set; } = 1;
    [JsonProperty("name")] public string Name { get; set; } = "Unnamed";
    [JsonProperty("author")] public string Author { get; set; } = "Unknown Author";
    [JsonProperty("description")] public string Description { get; set; } = string.Empty;
    [JsonProperty("gameVersion")] public string GameVersion { get; set; } = string.Empty;
    [JsonProperty("dimensions")] public Int3 Dimensions { get; set; } = new();
    [JsonProperty("anchorOffset")] public Int3 AnchorOffset { get; set; } = new();
    [JsonProperty("anchorFace", NullValueHandling = NullValueHandling.Ignore)] public string? AnchorFace { get; set; }
    [JsonProperty("dependencies")] public List<string> Dependencies { get; set; } = new();
    [JsonProperty("capabilities")] public List<string> Capabilities { get; set; } = new();
    [JsonProperty("blockCount")] public int BlockCount { get; set; }
    [JsonProperty("materialCount")] public int MaterialCount { get; set; }
    [JsonProperty("schematicSha256")] public string? SchematicSha256 { get; set; }
}

public sealed class BlockEntityRecord
{
    [JsonProperty("position")] public Int3 Position { get; set; } = new();
    [JsonProperty("class")] public string? EntityClass { get; set; }
    [JsonProperty("treeAscii85")] public string TreeAscii85 { get; set; } = string.Empty;
    [JsonProperty("orientation")] public string? Orientation { get; set; }
    [JsonProperty("compat", NullValueHandling = NullValueHandling.Ignore)] public List<BuildwrightCompatPayload> CompatData { get; set; } = new();
}

public sealed class ChiselVoxelData
{
    [JsonProperty("position")] public Int3 Position { get; set; } = new();
    [JsonProperty("blockIds")] public int[] BlockIds { get; set; } = Array.Empty<int>();
    [JsonProperty("voxelCuboids")] public uint[] VoxelCuboids { get; set; } = Array.Empty<uint>();
    [JsonProperty("originalVoxelCuboids")] public uint[] OriginalVoxelCuboids { get; set; } = Array.Empty<uint>();
    [JsonProperty("decorIds", NullValueHandling = NullValueHandling.Ignore)] public int[]? DecorIds { get; set; }
    [JsonProperty("decorRotations")] public int DecorRotations { get; set; }
    [JsonProperty("rotation")] public int Rotation { get; set; }
}

public sealed class BlueprintDocument
{
    public BlueprintMetadata Metadata { get; set; } = new();
    public JObject Schematic { get; set; } = new();
    public List<BlockEntityRecord> BlockEntities { get; set; } = new();
    public List<ChiselVoxelData> ChiselData { get; set; } = new();

    public string DisplayName => Metadata.Name;
}

public readonly record struct TransformState(Int3 Origin, int RotationQuarterTurns, MirrorAxis Mirror)
{
    public TransformState Normalize() => this with { RotationQuarterTurns = ((RotationQuarterTurns % 4) + 4) % 4 };
}

public sealed class BlueprintSummary
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public BlueprintFileKind Kind { get; init; }
    public Int3 Dimensions { get; init; } = new();
    public int BlockCount { get; init; }
    public int MaterialCount { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public bool HasBackup { get; init; }
    public string Author { get; init; } = string.Empty;
}

public sealed class DiffCell
{
    public Int3 Position { get; init; } = new();
    public string? ExpectedCode { get; init; }
    public string? ActualCode { get; init; }
    public DiffKind Kind { get; init; }
    public string? Detail { get; init; }
}

public sealed class DiffResult
{
    public List<DiffCell> Cells { get; } = new();
    public HashSet<(int X, int Y, int Z)> MatchedPositions { get; } = new();
    public Dictionary<string, int> MatchedByExpectedCode { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ExpectedCount { get; set; }
    public int MatchedCount { get; set; }
    public int MissingCount { get; set; }
    public int WrongCount { get; set; }
    public int ExtraCount { get; set; }
    public int UnknownCount { get; set; }
    public double Progress => ExpectedCount == 0 ? 1d : (double)MatchedCount / ExpectedCount;
}

public sealed class MaterialRequirement
{
    public string Code { get; init; } = string.Empty;
    /// <summary>Concrete collectible code used for display and icon lookup.</summary>
    public string DisplayCode { get; init; } = string.Empty;
    public int Required { get; set; }
    public int Available { get; set; }
    public int Missing => Math.Max(0, Required - Available);
    public int Completed { get; set; }
}
