using Newtonsoft.Json.Linq;

namespace Buildwright.Core;

public enum BlueprintSide
{
    West,
    East,
    Down,
    Up,
    North,
    South
}

public sealed class BlueprintDocumentDiff
{
    public Int3 LeftDimensions { get; init; } = new();
    public Int3 RightDimensions { get; init; } = new();
    public int SameCount { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ChangedCount { get; set; }
    public List<BlueprintDocumentDiffCell> Cells { get; } = new();
}

public sealed class BlueprintDocumentDiffCell
{
    public Int3 Position { get; init; } = new();
    public string? LeftCode { get; init; }
    public string? RightCode { get; init; }
    public BlueprintDocumentDiffKind Kind { get; init; }
}

public enum BlueprintDocumentDiffKind
{
    Added,
    Removed,
    Changed
}

/// <summary>
/// Pure document transformations used by the local library. Coordinate changes
/// update blocks, decors, BlockEntity records and chisel records together.
/// </summary>
public static class BlueprintEditor
{
    private const uint CoordinateMask = 0x3ff;

    public static void Crop(BlueprintDocument document, Int3 minInclusive, Int3 maxExclusive)
    {
        var dimensions = Dimensions(document);
        ValidateRange(minInclusive, maxExclusive, dimensions);
        var newDimensions = new Int3(maxExclusive.X - minInclusive.X, maxExclusive.Y - minInclusive.Y, maxExclusive.Z - minInclusive.Z);
        var blocks = ReadBlocks(document.Schematic)
            .Where(entry => InRange(entry.Position, minInclusive, maxExclusive))
            .Select(entry => entry with { Position = Subtract(entry.Position, minInclusive) })
            .ToList();
        var decors = ReadDecors(document.Schematic)
            .Where(entry => InRange(entry.Position, minInclusive, maxExclusive))
            .Select(entry => entry with { Position = Subtract(entry.Position, minInclusive) })
            .ToList();

        WriteBlocks(document.Schematic, blocks);
        WriteDecors(document.Schematic, decors);
        RewriteSchematicBlockEntities(document.Schematic, position =>
            InRange(position, minInclusive, maxExclusive) ? Subtract(position, minInclusive) : null);
        document.BlockEntities = document.BlockEntities
            .Where(record => InRange(record.Position, minInclusive, maxExclusive))
            .Select(record => Copy(record, Subtract(record.Position, minInclusive)))
            .ToList();
        document.ChiselData = document.ChiselData
            .Where(record => InRange(record.Position, minInclusive, maxExclusive))
            .Select(record => Copy(record, Subtract(record.Position, minInclusive)))
            .ToList();

        document.Metadata.AnchorOffset = Subtract(document.Metadata.AnchorOffset, minInclusive);
        SetDimensions(document, newDimensions);
        RefreshMetadata(document);
    }

    public static void Extend(BlueprintDocument document, BlueprintSide side, int amount)
    {
        if (amount < 1) throw new ArgumentOutOfRangeException(nameof(amount));
        var dimensions = Dimensions(document);
        var shift = new Int3();
        switch (side)
        {
            case BlueprintSide.West: dimensions.X += amount; shift.X = amount; break;
            case BlueprintSide.East: dimensions.X += amount; break;
            case BlueprintSide.Down: dimensions.Y += amount; shift.Y = amount; break;
            case BlueprintSide.Up: dimensions.Y += amount; break;
            case BlueprintSide.North: dimensions.Z += amount; shift.Z = amount; break;
            case BlueprintSide.South: dimensions.Z += amount; break;
            default: throw new ArgumentOutOfRangeException(nameof(side));
        }
        SetDimensions(document, dimensions);
        if (shift.X != 0 || shift.Y != 0 || shift.Z != 0)
        {
            TranslateAllCoordinates(document, shift);
            document.Metadata.AnchorOffset = Add(document.Metadata.AnchorOffset, shift);
        }
        RefreshMetadata(document);
    }

    public static void Shrink(BlueprintDocument document, BlueprintSide side, int amount)
    {
        if (amount < 1) throw new ArgumentOutOfRangeException(nameof(amount));
        var dimensions = Dimensions(document);
        var min = new Int3();
        var max = dimensions.Copy();
        switch (side)
        {
            case BlueprintSide.West: min.X = amount; break;
            case BlueprintSide.East: max.X -= amount; break;
            case BlueprintSide.Down: min.Y = amount; break;
            case BlueprintSide.Up: max.Y -= amount; break;
            case BlueprintSide.North: min.Z = amount; break;
            case BlueprintSide.South: max.Z -= amount; break;
            default: throw new ArgumentOutOfRangeException(nameof(side));
        }
        Crop(document, min, max);
    }

    /// <summary>Sets the placement anchor in current local blueprint coordinates.</summary>
    public static void SetAnchor(BlueprintDocument document, Int3 anchorOffset)
    {
        var dimensions = Dimensions(document);
        if (!InRange(anchorOffset, new Int3(), dimensions))
            throw new ArgumentOutOfRangeException(nameof(anchorOffset), "Anchor must remain inside the blueprint volume.");
        document.Metadata.AnchorOffset = anchorOffset.Copy();
    }

    /// <summary>
    /// Moves the local coordinate origin without changing the blueprint's world
    /// placement relative to its anchor. The caller must extend first if content
    /// would move outside its current dimensions.
    /// </summary>
    public static void TranslateInternalOrigin(BlueprintDocument document, Int3 offset)
    {
        var dimensions = Dimensions(document);
        var min = offset;
        var max = new Int3(dimensions.X + offset.X, dimensions.Y + offset.Y, dimensions.Z + offset.Z);
        if (!InRange(min, new Int3(), new Int3(1024, 1024, 1024)) || max.X > 1024 || max.Y > 1024 || max.Z > 1024)
            throw new ArgumentOutOfRangeException(nameof(offset), "Internal coordinates must stay in Vintage Story's schematic range.");
        TranslateAllCoordinates(document, offset);
        document.Metadata.AnchorOffset = Add(document.Metadata.AnchorOffset, offset);
        RefreshMetadata(document);
    }

    /// <summary>
    /// Replaces every matching code mapping, including mappings referenced by
    /// chisel voxels. Runtime ID arrays intentionally remain unchanged because
    /// Vintage Story resolves them through BlockCodes when the schematic loads.
    /// </summary>
    public static int ReplaceMaterial(BlueprintDocument document, string sourceCode, string targetCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode) || string.IsNullOrWhiteSpace(targetCode))
            throw new ArgumentException("Both block codes are required.");
        var codes = document.Schematic["BlockCodes"] as JObject ?? new JObject();
        document.Schematic["BlockCodes"] = codes;
        var replacements = 0;
        foreach (var property in codes.Properties().ToArray())
        {
            if (!string.Equals(property.Value.Value<string>(), sourceCode, StringComparison.OrdinalIgnoreCase)) continue;
            property.Value = targetCode;
            replacements++;
        }
        RefreshMetadata(document);
        return replacements;
    }

    public static BlueprintDocumentDiff Diff(BlueprintDocument left, BlueprintDocument right, int maximumCells = 20000)
    {
        if (maximumCells < 1) throw new ArgumentOutOfRangeException(nameof(maximumCells));
        var result = new BlueprintDocumentDiff { LeftDimensions = Dimensions(left), RightDimensions = Dimensions(right) };
        var leftCells = CellMap(left);
        var rightCells = CellMap(right);
        var positions = leftCells.Keys.Concat(rightCells.Keys).Distinct().OrderBy(key => key.X).ThenBy(key => key.Y).ThenBy(key => key.Z);
        foreach (var key in positions)
        {
            leftCells.TryGetValue(key, out var leftValue);
            rightCells.TryGetValue(key, out var rightValue);
            if (leftValue == rightValue)
            {
                result.SameCount++;
                continue;
            }
            var kind = leftValue == null ? BlueprintDocumentDiffKind.Added
                : rightValue == null ? BlueprintDocumentDiffKind.Removed
                : BlueprintDocumentDiffKind.Changed;
            switch (kind)
            {
                case BlueprintDocumentDiffKind.Added: result.AddedCount++; break;
                case BlueprintDocumentDiffKind.Removed: result.RemovedCount++; break;
                default: result.ChangedCount++; break;
            }
            if (result.Cells.Count < maximumCells)
            {
                result.Cells.Add(new BlueprintDocumentDiffCell
                {
                    Position = new Int3(key.X, key.Y, key.Z),
                    LeftCode = leftValue,
                    RightCode = rightValue,
                    Kind = kind
                });
            }
        }
        return result;
    }

    public static Int3 Dimensions(BlueprintDocument document)
    {
        var metadata = document.Metadata.Dimensions;
        return new Int3(
            metadata.X > 0 ? metadata.X : document.Schematic["SizeX"]?.Value<int>() ?? 0,
            metadata.Y > 0 ? metadata.Y : document.Schematic["SizeY"]?.Value<int>() ?? 0,
            metadata.Z > 0 ? metadata.Z : document.Schematic["SizeZ"]?.Value<int>() ?? 0);
    }

    private static Dictionary<(int X, int Y, int Z), string> CellMap(BlueprintDocument document)
    {
        var codes = document.Schematic["BlockCodes"] as JObject ?? new JObject();
        var map = new Dictionary<(int X, int Y, int Z), List<string>>();
        foreach (var block in ReadBlocks(document.Schematic))
        {
            var key = (block.Position.X, block.Position.Y, block.Position.Z);
            if (!map.TryGetValue(key, out var values)) map[key] = values = new List<string>();
            values.Add(codes[block.BlockId.ToString()]?.Value<string>() ?? $"#{block.BlockId}");
        }

        var chisel = document.ChiselData.ToDictionary(record => (record.Position.X, record.Position.Y, record.Position.Z));
        foreach (var pair in chisel)
            if (!map.ContainsKey(pair.Key)) map[pair.Key] = new List<string>();
        return map.ToDictionary(pair => pair.Key, pair =>
        {
            pair.Value.Sort(StringComparer.OrdinalIgnoreCase);
            var value = string.Join("|", pair.Value);
            if (chisel.TryGetValue(pair.Key, out var record))
                value += "#chisel:" + string.Join(",", record.BlockIds) + ":" + string.Join(",", record.VoxelCuboids);
            return value;
        });
    }

    private static void TranslateAllCoordinates(BlueprintDocument document, Int3 offset)
    {
        var dimensions = Dimensions(document);
        var shiftedBlocks = ReadBlocks(document.Schematic).Select(entry => entry with { Position = Add(entry.Position, offset) }).ToList();
        var shiftedDecors = ReadDecors(document.Schematic).Select(entry => entry with { Position = Add(entry.Position, offset) }).ToList();
        if (shiftedBlocks.Any(entry => !InRange(entry.Position, new Int3(), dimensions))
            || shiftedDecors.Any(entry => !InRange(entry.Position, new Int3(), dimensions))
            || document.BlockEntities.Any(record => !InRange(Add(record.Position, offset), new Int3(), dimensions))
            || document.ChiselData.Any(record => !InRange(Add(record.Position, offset), new Int3(), dimensions)))
            throw new ArgumentOutOfRangeException(nameof(offset), "Move would place content outside the blueprint volume.");
        WriteBlocks(document.Schematic, shiftedBlocks);
        WriteDecors(document.Schematic, shiftedDecors);
        RewriteSchematicBlockEntities(document.Schematic, position => Add(position, offset));
        document.BlockEntities = document.BlockEntities.Select(record => Copy(record, Add(record.Position, offset))).ToList();
        document.ChiselData = document.ChiselData.Select(record => Copy(record, Add(record.Position, offset))).ToList();
    }

    private static List<BlockEntry> ReadBlocks(JObject schematic)
    {
        var indices = schematic["Indices"] as JArray ?? new JArray();
        var ids = schematic["BlockIds"] as JArray ?? new JArray();
        var count = Math.Min(indices.Count, ids.Count);
        var entries = new List<BlockEntry>(count);
        for (var index = 0; index < count; index++)
            entries.Add(new BlockEntry(Unpack(indices[index]!.Value<uint>()), ids[index]!.Value<int>()));
        return entries;
    }

    private static List<DecorEntry> ReadDecors(JObject schematic)
    {
        var indices = schematic["DecorIndices"] as JArray ?? new JArray();
        var ids = schematic["DecorIds"] as JArray ?? new JArray();
        var count = Math.Min(indices.Count, ids.Count);
        var entries = new List<DecorEntry>(count);
        for (var index = 0; index < count; index++)
            entries.Add(new DecorEntry(Unpack(indices[index]!.Value<uint>()), ids[index]!.DeepClone()));
        return entries;
    }

    private static void WriteBlocks(JObject schematic, IEnumerable<BlockEntry> entries)
    {
        var indices = new JArray();
        var ids = new JArray();
        foreach (var entry in entries)
        {
            indices.Add(Pack(entry.Position));
            ids.Add(entry.BlockId);
        }
        schematic["Indices"] = indices;
        schematic["BlockIds"] = ids;
    }

    private static void WriteDecors(JObject schematic, IEnumerable<DecorEntry> entries)
    {
        var indices = new JArray();
        var ids = new JArray();
        foreach (var entry in entries)
        {
            indices.Add(Pack(entry.Position));
            ids.Add(entry.DecorId);
        }
        schematic["DecorIndices"] = indices;
        schematic["DecorIds"] = ids;
    }

    private static void RewriteSchematicBlockEntities(JObject schematic, Func<Int3, Int3?> map)
    {
        var source = schematic["BlockEntities"] as JObject;
        if (source == null) return;
        var replacement = new JObject();
        foreach (var property in source.Properties())
        {
            if (!uint.TryParse(property.Name, out var packed)) continue;
            var mapped = map(Unpack(packed));
            if (mapped != null) replacement[Pack(mapped).ToString()] = property.Value.DeepClone();
        }
        schematic["BlockEntities"] = replacement;
    }

    private static void SetDimensions(BlueprintDocument document, Int3 dimensions)
    {
        if (dimensions.X < 1 || dimensions.Y < 1 || dimensions.Z < 1 || dimensions.X > 1024 || dimensions.Y > 1024 || dimensions.Z > 1024)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Blueprint dimensions must be 1 through 1024.");
        document.Metadata.Dimensions = dimensions.Copy();
        document.Schematic["SizeX"] = dimensions.X;
        document.Schematic["SizeY"] = dimensions.Y;
        document.Schematic["SizeZ"] = dimensions.Z;
    }

    private static void RefreshMetadata(BlueprintDocument document)
    {
        document.Metadata.BlockCount = (document.Schematic["Indices"] as JArray)?.Count ?? 0;
        var codes = document.Schematic["BlockCodes"] as JObject;
        document.Metadata.MaterialCount = codes?.Properties().Select(property => property.Value.Value<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        document.Metadata.SchematicSha256 = null;
    }

    private static void ValidateRange(Int3 min, Int3 max, Int3 dimensions)
    {
        if (min.X < 0 || min.Y < 0 || min.Z < 0 || max.X > dimensions.X || max.Y > dimensions.Y || max.Z > dimensions.Z
            || min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
            throw new ArgumentOutOfRangeException(nameof(max), "Crop range must be inside the blueprint and have positive dimensions.");
    }

    private static bool InRange(Int3 position, Int3 min, Int3 max) => position.X >= min.X && position.X < max.X && position.Y >= min.Y && position.Y < max.Y && position.Z >= min.Z && position.Z < max.Z;
    private static Int3 Add(Int3 left, Int3 right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    private static Int3 Subtract(Int3 left, Int3 right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    private static uint Pack(Int3 position) => ((uint)position.X & CoordinateMask) | (((uint)position.Z & CoordinateMask) << 10) | (((uint)position.Y & CoordinateMask) << 20);
    private static Int3 Unpack(uint packed) => new((int)(packed & CoordinateMask), (int)((packed >> 20) & CoordinateMask), (int)((packed >> 10) & CoordinateMask));
    private static BlockEntityRecord Copy(BlockEntityRecord source, Int3 position) => new()
    {
        Position = position,
        EntityClass = source.EntityClass,
        TreeAscii85 = source.TreeAscii85,
        Orientation = source.Orientation,
        CompatData = source.CompatData.Select(payload => new Buildwright.Api.BuildwrightCompatPayload
        {
            AdapterId = payload.AdapterId,
            Data = payload.Data.DeepClone()
        }).ToList()
    };
    private static ChiselVoxelData Copy(ChiselVoxelData source, Int3 position) => new() { Position = position, BlockIds = source.BlockIds.ToArray(), VoxelCuboids = source.VoxelCuboids.ToArray(), OriginalVoxelCuboids = source.OriginalVoxelCuboids.ToArray(), DecorIds = source.DecorIds?.ToArray(), DecorRotations = source.DecorRotations, Rotation = source.Rotation };

    private readonly record struct BlockEntry(Int3 Position, int BlockId);
    private readonly record struct DecorEntry(Int3 Position, JToken DecorId);
}
