using Buildwright.Core;
using Buildwright.Api;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Buildwright.Client;

internal sealed class BuildwrightTracker : IDisposable
{
    private const int DiffHighlightSlot = 8616;
    private const uint Mask = 0x3ff;
    private readonly ICoreClientAPI capi;
    private readonly BlockSchematic schematic;
    private readonly Int3 anchorOffset;
    private readonly BlockPos origin;
    private readonly int rotation;
    private readonly MirrorAxis mirror;
    private readonly MatchingMode mode;
    private readonly List<(Int3 Position, string? ExpectedCode)> expected = new();
    private readonly List<(Int3 Position, string? ExpectedCode)> expectedFluids = new();
    private readonly Dictionary<(int X, int Y, int Z), string> expectedCodesByWorld = new();
    private readonly Dictionary<(int X, int Y, int Z), string> expectedEntities = new();
    private readonly Dictionary<(int X, int Y, int Z), ChiselVoxelData> expectedChisel = new();
    private readonly Dictionary<(int X, int Y, int Z), ChiselVoxelComparer.Snapshot> expectedChiselSnapshots = new();
    private readonly Dictionary<(int X, int Y, int Z), ChiselComparison> chiselComparisons = new();
    private readonly BuildwrightChiselDiffRenderer chiselDiffRenderer;
    private readonly BuildwrightDiffOutlineRenderer diffOutlineRenderer;
    private readonly List<ChiselVoxelHighlight> chiselHighlights = new();
    private readonly Dictionary<(int X, int Y, int Z), (Block Block, IReadOnlyList<BuildwrightCompatPayload> Payloads)> expectedCompat = new();
    private readonly Int3 dimensions;
    private readonly DiffEngine engine = new();
    private readonly IReadOnlyList<MaterialRequirement> requiredMaterials;
    private bool disposed;
    private IEnumerator<Int3>? scanEnumerator;
    private Dictionary<(int X, int Y, int Z), string?>? pendingActual;

    public DiffResult Current { get; private set; } = new();
    public IReadOnlyList<MaterialRequirement> Materials { get; private set; } = Array.Empty<MaterialRequirement>();
    public bool HasScanResult { get; private set; }
    public string BlueprintName { get; }
    public int RecommendedScanIntervalMs
    {
        get
        {
            var interval = expected.Count switch
            {
                <= 4096 => 250,
                <= 16384 => 350,
                <= 65536 => 700,
                <= 262144 => 1500,
                _ => 2500
            };
            if (mode != MatchingMode.Strict) return interval;
            var specialCount = expectedEntities.Count + expectedCompat.Count * 4;
            if (specialCount > 2048 || expectedChisel.Count > 256) return Math.Max(interval, 1200);
            if (specialCount > 512 || expectedChisel.Count > 64) return Math.Max(interval, 750);
            if (specialCount > 128 || expectedChisel.Count > 16) return Math.Max(interval, 500);
            return interval;
        }
    }

    public BuildwrightTracker(ICoreClientAPI capi, BlockSchematic schematic, string blueprintName,
        Int3 anchorOffset, BlockPos origin, int rotation, MirrorAxis mirror, MatchingMode mode,
        IEnumerable<ChiselVoxelData>? chiselData = null, IEnumerable<BlockEntityRecord>? blockEntities = null)
    {
        this.capi = capi;
        this.schematic = schematic;
        BlueprintName = blueprintName;
        this.anchorOffset = anchorOffset;
        this.origin = origin.Copy();
        this.rotation = rotation;
        this.mirror = mirror;
        this.mode = mode;
        chiselDiffRenderer = new BuildwrightChiselDiffRenderer(capi);
        diffOutlineRenderer = new BuildwrightDiffOutlineRenderer(capi);
        dimensions = new Int3(schematic.SizeX, schematic.SizeY, schematic.SizeZ);
        BuildExpected();
        requiredMaterials = MaterialCounter.CountRequired(expected.Concat(expectedFluids).Select(item => item.ExpectedCode), mode);
        if (chiselData != null)
        {
            foreach (var data in chiselData)
            {
                var world = BlueprintTransforms.CellToWorld(data.Position, anchorOffset,
                    new Int3(origin.X, origin.Y, origin.Z), rotation, mirror);
                var resolved = BlockEntityCapture.ResolveChiselBlockIds(capi, schematic, data);
                var transformed = ChiselTransforms.Transform(resolved, rotation, mirror);
                var key = (world.X, world.Y, world.Z);
                expectedChisel[key] = transformed;
                expectedChiselSnapshots[key] = ChiselVoxelComparer.CreateSnapshot(transformed);
            }
        }
        if (blockEntities != null)
        {
            foreach (var record in blockEntities)
            {
                if (record.CompatData.Count == 0) continue;
                var local = record.Position;
                var world = BlueprintTransforms.CellToWorld(local, anchorOffset,
                    new Int3(origin.X, origin.Y, origin.Z), rotation, mirror);
                var packed = RegionCapture.Pack(local.X, local.Y, local.Z);
                if (!schematic.BlockEntities.TryGetValue(packed, out _) || !TryGetBlockAtPacked(packed, out var block)) continue;
                expectedCompat[(world.X, world.Y, world.Z)] = (block, record.CompatData);
            }
        }
        capi.Event.BlockChanged += OnBlockChanged;
        // The first scan is scheduled by the client tick after the HUD and
        // ghost are visible. Large blueprints must not block HUD composition.
    }

    public bool IsComplete => HasScanResult
        && Current.MissingCount == 0 && Current.WrongCount == 0
        && Current.ExtraCount == 0 && Current.UnknownCount == 0;
    public bool IsScanning => scanEnumerator != null;
    public int ScanSliceBudget => mode switch
    {
        MatchingMode.Strict when expectedChisel.Count > 0 || expectedEntities.Count > 0 || expectedCompat.Count > 0 => 512,
        MatchingMode.Strict => 1024,
        MatchingMode.Medium => 2048,
        _ => 4096
    };

    public bool Scan(int budget = 2048)
    {
        if (disposed) return true;
        // Scan only cells that actually exist in the blueprint. Walking the
        // full bounding volume makes sparse large blueprints take seconds to
        // report a broken block.
        scanEnumerator ??= expected.Select(item => item.Position)
            .Concat(expectedFluids.Select(item => item.Position))
            .DistinctBy(position => (position.X, position.Y, position.Z))
            .GetEnumerator();
        pendingActual ??= new Dictionary<(int X, int Y, int Z), string?>();
        var processed = 0;
        while (processed++ < Math.Max(256, budget) && scanEnumerator.MoveNext())
        {
            var local = scanEnumerator.Current;
            var world = local;
            var pos = new BlockPos(world.X, world.Y, world.Z, origin.dimension);
            var block = capi.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
            pendingActual[(world.X, world.Y, world.Z)] = block == null || block.Id == 0 || BlueprintBlockFilter.IsTransient(block)
                ? null
                : block.Code?.ToString();
        }

        if (scanEnumerator.MoveNext())
        {
            // We advanced once past the budget while checking completion. Keep
            // the current cell for the next slice by rebuilding a small wrapper
            // enumerator that yields it before continuing the original stream.
            var next = scanEnumerator.Current;
            scanEnumerator = Prepend(next, scanEnumerator);
            return false;
        }

        scanEnumerator.Dispose();
        scanEnumerator = null;
        var actual = pendingActual;
        pendingActual = null;
        Current = engine.Compare(expected, actual, mode);
        CompareFluids();
        CompareSpecialData();
        RefreshMaterials();
        RefreshHighlights();
        HasScanResult = true;
        return true;
    }

    private static IEnumerator<Int3> Prepend(Int3 first, IEnumerator<Int3> rest)
    {
        yield return first;
        while (rest.MoveNext()) yield return rest.Current;
        rest.Dispose();
    }

    public string ToPlainText()
    {
        var current = Current;
        return $"Buildwright: {BlueprintName}\n" +
            $"Progress: {current.Progress:P0} ({current.MatchedCount}/{current.ExpectedCount})\n" +
            $"Missing: {current.MissingCount}  Wrong: {current.WrongCount}  Extra: {current.ExtraCount}";
    }

    private void BuildExpected()
    {
        for (var i = 0; i < schematic.Indices.Count && i < schematic.BlockIds.Count; i++)
        {
            if (!schematic.BlockCodes.TryGetValue(schematic.BlockIds[i], out var code)) continue;
            var packed = schematic.Indices[i];
            var local = new Int3((int)(packed & Mask), (int)((packed >> 20) & Mask), (int)((packed >> 10) & Mask));
            var block = capi.World.GetBlock(code);
            if (BlueprintBlockFilter.IsTransient(block)) continue;
            var world = BlueprintTransforms.CellToWorld(local, anchorOffset,
                new Int3(origin.X, origin.Y, origin.Z), rotation, mirror);
            var expectedCode = ResolveExpectedCode(code);
            if (block?.ForFluidsLayer == true) expectedFluids.Add((world, expectedCode));
            else
            {
                expected.Add((world, expectedCode));
                expectedCodesByWorld[(world.X, world.Y, world.Z)] = expectedCode;
            }
        }
        foreach (var entity in schematic.BlockEntities)
        {
            var local = new Int3((int)(entity.Key & Mask), (int)((entity.Key >> 20) & Mask), (int)((entity.Key >> 10) & Mask));
            var world = BlueprintTransforms.CellToWorld(local, anchorOffset, new Int3(origin.X, origin.Y, origin.Z), rotation, mirror);
            expectedEntities[(world.X, world.Y, world.Z)] = BlockEntityCapture.TransformPayload(capi, schematic, entity.Key,
                entity.Value, rotation, mirror);
        }
    }

    private void CompareSpecialData()
    {
        chiselHighlights.Clear();
        if (mode != MatchingMode.Strict) return;
        foreach (var pair in expectedEntities)
        {
            // Chiseled entities use the cached semantic voxel comparison below.
            if (expectedChisel.ContainsKey(pair.Key)) continue;
            var pos = new BlockPos(pair.Key.X, pair.Key.Y, pair.Key.Z, origin.dimension);
            var entity = capi.World.BlockAccessor.GetBlockEntity(pos);
            if (entity == null)
            {
                MarkSpecialWrong(new Int3(pair.Key.X, pair.Key.Y, pair.Key.Z), "Missing BlockEntity data");
                continue;
            }
            var tree = new Vintagestory.API.Datastructures.TreeAttribute();
            try
            {
                entity.ToTreeAttributes(tree);
                var expectedTree = BlockEntityCapture.Decode(pair.Value);
                if (expectedTree == null)
                {
                    MarkUnknown(new Int3(pair.Key.X, pair.Key.Y, pair.Key.Z), "BlockEntity comparison unavailable");
                    continue;
                }
                var hasSupportBeams = SupportBeamDataComparer.TryCompare(capi, schematic,
                    expectedTree, tree, out var supportBeamsMatch);
                var ignoredEntityMetadata = hasSupportBeams
                    ? new[] { "beams", "posx", "posy", "posz" }
                    : new[] { "posx", "posy", "posz" };
                if ((hasSupportBeams && !supportBeamsMatch)
                    || !expectedTree.Equals(capi.World, tree, ignoredEntityMetadata))
                {
                    MarkSpecialWrong(new Int3(pair.Key.X, pair.Key.Y, pair.Key.Z), "BlockEntity data differs");
                }
            }
            catch
            {
                MarkUnknown(new Int3(pair.Key.X, pair.Key.Y, pair.Key.Z), "BlockEntity comparison unavailable");
            }
        }
        foreach (var pair in expectedCompat)
        {
            var pos = new BlockPos(pair.Key.X, pair.Key.Y, pair.Key.Z, origin.dimension);
            var actual = capi.World.BlockAccessor.GetBlockEntity(pos);
            foreach (var payload in pair.Value.Payloads)
            {
                var comparison = BuildwrightCompatApi.Compare(capi, pair.Value.Block, actual, payload,
                    new TransformState(new Int3(origin.X, origin.Y, origin.Z), rotation, mirror));
                if (comparison == BuildwrightCompatComparison.Match || comparison == BuildwrightCompatComparison.NotHandled) continue;
                if (comparison == BuildwrightCompatComparison.Unknown)
                    MarkUnknown(new Int3(pair.Key.X, pair.Key.Y, pair.Key.Z), "Compatibility comparison unavailable");
                else MarkSpecialWrong(new Int3(pair.Key.X, pair.Key.Y, pair.Key.Z), "Compatibility data differs");
                break;
            }
        }
        foreach (var pair in expectedChisel)
        {
            if (!chiselComparisons.TryGetValue(pair.Key, out var comparison))
            {
                var pos = new BlockPos(pair.Key.X, pair.Key.Y, pair.Key.Z, origin.dimension);
                var entity = capi.World.BlockAccessor.GetBlockEntity(pos);
                var actualChisel = entity == null ? null : BlockEntityCapture.TryCaptureChisel(entity,
                    pair.Value.Position, includeOriginalVoxelCuboids: false);
                var actualForVoxels = actualChisel ?? CaptureWholeBlock(pos);
                var differences = ChiselVoxelComparer.GetDifferences(expectedChiselSnapshots[pair.Key], actualForVoxels);
                comparison = new ChiselComparison(differences, actualChisel != null && differences.Count == 0
                    && BlockEntityCapture.ChiselMetadataEquals(pair.Value, actualChisel));
                chiselComparisons[pair.Key] = comparison;
            }

            foreach (var difference in comparison.Differences)
                chiselHighlights.Add(new ChiselVoxelHighlight(pair.Key.X, pair.Key.Y, pair.Key.Z,
                    difference.X, difference.Y, difference.Z, difference.Kind));

            if (!comparison.Matches)
            {
                MarkSpecialWrong(new Int3(pair.Key.X, pair.Key.Y, pair.Key.Z), "Chisel voxel data differs");
                if (comparison.Differences.Count == 0) AddExpectedChiselAsMaterialError(pair.Key, expectedChiselSnapshots[pair.Key]);
            }
        }
    }

    private void OnBlockChanged(BlockPos position, Block oldBlock)
    {
        chiselComparisons.Remove((position.X, position.Y, position.Z));
    }

    private ChiselVoxelData CaptureWholeBlock(BlockPos pos)
    {
        var block = capi.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
        if (block == null || block.Id == 0 || block is BlockMicroBlock || BlueprintBlockFilter.IsTransient(block))
            return new ChiselVoxelData();
        return new ChiselVoxelData
        {
            BlockIds = new[] { block.Id },
            VoxelCuboids = new[] { 0x00fff000u }
        };
    }

    private void AddExpectedChiselAsMaterialError((int X, int Y, int Z) world, ChiselVoxelComparer.Snapshot expectedSnapshot)
    {
        var empty = new ChiselVoxelData();
        foreach (var voxel in ChiselVoxelComparer.GetDifferences(expectedSnapshot, empty))
            chiselHighlights.Add(new ChiselVoxelHighlight(world.X, world.Y, world.Z,
                voxel.X, voxel.Y, voxel.Z, ChiselVoxelDiffKind.Material));
    }

    private void CompareFluids()
    {
        if (expectedFluids.Count == 0) return;
        foreach (var item in expectedFluids)
        {
            var world = item.Position;
            var pos = new BlockPos(world.X, world.Y, world.Z, origin.dimension);
            var actual = capi.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            var result = engine.Compare(new[] { (new Int3(world.X, world.Y, world.Z), item.ExpectedCode) },
                new Dictionary<(int X, int Y, int Z), string?>
                {
                    [(world.X, world.Y, world.Z)] = actual == null || actual.Id == 0 ? null : actual.Code?.ToString()
                }, mode);
            Current.ExpectedCount += result.ExpectedCount;
            Current.MatchedCount += result.MatchedCount;
            Current.MissingCount += result.MissingCount;
            Current.WrongCount += result.WrongCount;
            Current.ExtraCount += result.ExtraCount;
            Current.UnknownCount += result.UnknownCount;
            Current.Cells.AddRange(result.Cells);
            Current.MatchedPositions.UnionWith(result.MatchedPositions);
            foreach (var pair in result.MatchedByExpectedCode)
                Current.MatchedByExpectedCode[pair.Key] = Current.MatchedByExpectedCode.GetValueOrDefault(pair.Key) + pair.Value;
        }

        if ((long)dimensions.X * dimensions.Y * dimensions.Z > 32768)
        {
            // Extra-fluid detection over a huge sparse volume is deferred;
            // expected fluid cells above are still checked immediately.
            return;
        }

        var expectedFluidPositions = expectedFluids
            .Select(item => (item.Position.X, item.Position.Y, item.Position.Z))
            .ToHashSet();
        foreach (var local in BlueprintTransforms.EnumerateBox(dimensions))
        {
            var world = BlueprintTransforms.CellToWorld(local, anchorOffset,
                new Int3(origin.X, origin.Y, origin.Z), rotation, mirror);
            if (expectedFluidPositions.Contains((world.X, world.Y, world.Z))) continue;
            var fluid = capi.World.BlockAccessor.GetBlock(new BlockPos(world.X, world.Y, world.Z, origin.dimension), BlockLayersAccess.Fluid);
            if (fluid == null || fluid.Id == 0) continue;
            Current.ExtraCount++;
            Current.Cells.Add(new DiffCell
            {
                Position = new Int3(world.X, world.Y, world.Z),
                ActualCode = fluid.Code?.ToString(),
                Kind = DiffKind.Extra,
                Detail = "Extra fluid"
            });
        }
    }

    private string ResolveExpectedCode(AssetLocation code)
    {
        var block = capi.World.GetBlock(code);
        if (block == null) return code.ToString();
        try
        {
            if (mirror == MirrorAxis.Y) block = capi.World.GetBlock(block.GetVerticallyFlippedBlockCode()) ?? block;
            if (mirror == MirrorAxis.X) block = capi.World.GetBlock(block.GetHorizontallyFlippedBlockCode(EnumAxis.X)) ?? block;
            if (mirror == MirrorAxis.Z) block = capi.World.GetBlock(block.GetHorizontallyFlippedBlockCode(EnumAxis.Z)) ?? block;
            if (rotation != 0) block = capi.World.GetBlock(block.GetRotatedBlockCode(((rotation % 4) + 4) % 4 * 90)) ?? block;
            return block.Code?.ToString() ?? code.ToString();
        }
        catch
        {
            return code.ToString();
        }
    }

    private bool TryGetBlockAtPacked(uint packed, out Block block)
    {
        block = null!;
        var index = schematic.Indices.IndexOf(packed);
        if (index < 0 || index >= schematic.BlockIds.Count
            || !schematic.BlockCodes.TryGetValue(schematic.BlockIds[index], out var code)) return false;
        block = capi.World.GetBlock(code)!;
        return block != null;
    }

    private void MarkSpecialWrong(Int3 position, string detail)
    {
        var alreadyWrong = Current.Cells.Any(cell => cell.Kind == DiffKind.Wrong
            && cell.Position.X == position.X && cell.Position.Y == position.Y && cell.Position.Z == position.Z);
        if (alreadyWrong) return;
        RemoveMatched(position);
        Current.WrongCount++;
        Current.Cells.Add(new DiffCell { Position = position, Kind = DiffKind.Wrong, Detail = detail });
    }

    private void MarkUnknown(Int3 position, string detail)
    {
        var alreadyUnknown = Current.Cells.Any(cell => cell.Kind == DiffKind.Unknown
            && cell.Position.X == position.X && cell.Position.Y == position.Y && cell.Position.Z == position.Z);
        if (alreadyUnknown) return;
        RemoveMatched(position);
        Current.UnknownCount++;
        Current.Cells.Add(new DiffCell { Position = position, Kind = DiffKind.Unknown, Detail = detail });
    }

    private void RefreshMaterials()
    {
        var rows = requiredMaterials.Select(row => new MaterialRequirement
        {
            Code = row.Code,
            DisplayCode = row.DisplayCode,
            Required = row.Required
        }).ToList();
        var completedByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in Current.MatchedByExpectedCode)
        {
            var key = MaterialCounter.Key(match.Key, mode);
            completedByCode[key] = completedByCode.GetValueOrDefault(key) + match.Value;
        }
        foreach (var row in rows)
        {
            row.Completed = Math.Min(row.Required, completedByCode.GetValueOrDefault(row.Code));
        }
        var available = new List<(string Code, int Count)>();
        var manager = capi.World.Player?.InventoryManager;
        if (manager != null)
        {
            var inventories = new[]
            {
                manager.GetHotbarInventory(),
                manager.GetOwnInventory(Vintagestory.API.Config.GlobalConstants.backpackInvClassName)
            }.Where(inventory => inventory != null).Distinct();
            foreach (var inventory in inventories)
            foreach (var slot in inventory)
            {
                var code = slot.Itemstack?.Collectible?.Code?.ToString();
                if (!string.IsNullOrWhiteSpace(code)) available.Add((code, slot.StackSize));
            }
        }
        MaterialCounter.MergeInventory(available, rows, mode);
        foreach (var row in rows) row.Completed = Math.Min(row.Required, completedByCode.GetValueOrDefault(row.Code));
        Materials = rows;
    }

    private void RemoveMatched(Int3 position)
    {
        var key = (position.X, position.Y, position.Z);
        if (!Current.MatchedPositions.Remove(key)) return;
        Current.MatchedCount = Math.Max(0, Current.MatchedCount - 1);
        if (!expectedCodesByWorld.TryGetValue(key, out var expectedCode)) return;
        var matches = Current.MatchedByExpectedCode.GetValueOrDefault(expectedCode);
        if (matches <= 1) Current.MatchedByExpectedCode.Remove(expectedCode);
        else Current.MatchedByExpectedCode[expectedCode] = matches - 1;
    }

    private void RefreshHighlights()
    {
        var entityOutlines = Current.Cells
            .Where(IsSpecialDataWrong)
            .Where(cell => !expectedChisel.ContainsKey((cell.Position.X, cell.Position.Y, cell.Position.Z)))
            .Select(cell => new BlockPos(cell.Position.X, cell.Position.Y, cell.Position.Z, origin.dimension))
            .ToList();
        var cells = Current.Cells
            .Where(cell => cell.Kind is DiffKind.Wrong or DiffKind.Extra)
            .Where(cell => !IsSpecialDataWrong(cell))
            .Where(cell => !expectedChisel.ContainsKey((cell.Position.X, cell.Position.Y, cell.Position.Z)))
            .ToList();
        var positions = cells.Select(cell => new BlockPos(cell.Position.X, cell.Position.Y, cell.Position.Z, origin.dimension)).ToList();
        var colors = cells.Select(cell => cell.Kind == DiffKind.Extra
            ? ColorUtil.ColorFromRgba(255, 145, 30, 170)
            : ColorUtil.ColorFromRgba(235, 45, 45, 160)).ToList();
        capi.World.HighlightBlocks(capi.World.Player, DiffHighlightSlot, positions, colors,
            Vintagestory.API.Client.EnumHighlightBlocksMode.Absolute,
            Vintagestory.API.Common.EnumHighlightShape.Arbitrary);
        diffOutlineRenderer.SetBlocks(entityOutlines);
        chiselDiffRenderer.SetHighlights(chiselHighlights);
    }

    private static bool IsSpecialDataWrong(DiffCell cell) =>
        cell.Kind == DiffKind.Wrong
        && string.IsNullOrWhiteSpace(cell.ExpectedCode)
        && string.IsNullOrWhiteSpace(cell.ActualCode);

    public void Dispose()
    {
        disposed = true;
        scanEnumerator?.Dispose();
        scanEnumerator = null;
        pendingActual = null;
        chiselDiffRenderer.Dispose();
        diffOutlineRenderer.Dispose();
        capi.Event.BlockChanged -= OnBlockChanged;
        capi.World.HighlightBlocks(capi.World.Player, DiffHighlightSlot, new List<BlockPos>(),
            Vintagestory.API.Client.EnumHighlightBlocksMode.Absolute,
            Vintagestory.API.Common.EnumHighlightShape.Arbitrary);
    }

    private sealed record ChiselComparison(IReadOnlyList<ChiselVoxelDifference> Differences, bool Matches);
}
