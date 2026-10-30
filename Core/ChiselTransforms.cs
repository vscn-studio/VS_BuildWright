using System.Buffers;

namespace Buildwright.Core;

public static class ChiselTransforms
{
    public static ChiselVoxelData Transform(ChiselVoxelData source, int quarterTurns, MirrorAxis mirror)
    {
        var result = new ChiselVoxelData
        {
            Position = source.Position.Copy(),
            BlockIds = source.BlockIds.ToArray(),
            DecorIds = source.DecorIds?.ToArray(),
            DecorRotations = source.DecorRotations,
            Rotation = source.Rotation
        };
        result.VoxelCuboids = source.VoxelCuboids.Select(value => TransformCuboid(value, quarterTurns, mirror)).ToArray();
        result.OriginalVoxelCuboids = source.OriginalVoxelCuboids.Select(value => TransformCuboid(value, quarterTurns, mirror)).ToArray();
        result.Rotation = ((source.Rotation + quarterTurns * 90) % 360 + 360) % 360;
        return result;
    }

    private static uint TransformCuboid(uint packed, int quarterTurns, MirrorAxis mirror)
    {
        Decode(packed, out var x0, out var y0, out var z0, out var x1, out var y1, out var z1, out var material);
        if (mirror == MirrorAxis.X) (x0, x1) = (16 - x1, 16 - x0);
        if (mirror == MirrorAxis.Y) (y0, y1) = (16 - y1, 16 - y0);
        if (mirror == MirrorAxis.Z) (z0, z1) = (16 - z1, 16 - z0);
        for (var i = 0; i < ((quarterTurns % 4) + 4) % 4; i++)
            (x0, x1, z0, z1) = (z0, z1, 16 - x1, 16 - x0);
        return Encode(x0, y0, z0, x1, y1, z1, material);
    }

    private static uint Encode(int x0, int y0, int z0, int x1, int y1, int z1, int material) =>
        (uint)(x0 | (y0 << 4) | (z0 << 8) | ((x1 - 1) << 12) | ((y1 - 1) << 16) | ((z1 - 1) << 20) | (material << 24));

    private static void Decode(uint value, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1, out int material)
    {
        x0 = (int)(value & 0xF);
        y0 = (int)((value >> 4) & 0xF);
        z0 = (int)((value >> 8) & 0xF);
        x1 = (int)((value >> 12) & 0xF) + 1;
        y1 = (int)((value >> 16) & 0xF) + 1;
        z1 = (int)((value >> 20) & 0xF) + 1;
        material = (int)((value >> 24) & 0xFF);
    }
}

public static class ChiselVoxelComparer
{
    private const int EmptyVoxel = int.MinValue;
    private const int VoxelSize = 16;
    private const int VoxelCount = VoxelSize * VoxelSize * VoxelSize;

    public static bool HasSameVoxelMaterials(ChiselVoxelData expected, ChiselVoxelData actual)
    {
        return GetDifferences(CreateSnapshot(expected), actual).Count == 0;
    }

    public static IReadOnlyList<ChiselVoxelDifference> GetDifferences(ChiselVoxelData expected, ChiselVoxelData actual)
    {
        return GetDifferences(CreateSnapshot(expected), actual);
    }

    public static Snapshot CreateSnapshot(ChiselVoxelData data)
    {
        var voxels = new int[VoxelCount];
        return new Snapshot(voxels, TryExpand(data, voxels));
    }

    public static IReadOnlyList<ChiselVoxelDifference> GetDifferences(Snapshot expected, ChiselVoxelData actual)
    {
        var actualVoxels = ArrayPool<int>.Shared.Rent(VoxelCount);
        try
        {
            if (!expected.IsValid || !TryExpand(actual, actualVoxels)) return InvalidDifferences();

            var differences = new List<ChiselVoxelDifference>();
            for (var x = 0; x < VoxelSize; x++)
            for (var y = 0; y < VoxelSize; y++)
            for (var z = 0; z < VoxelSize; z++)
            {
                var index = (x * VoxelSize + y) * VoxelSize + z;
                var expectedId = expected.Voxels[index];
                var actualId = actualVoxels[index];
                if (expectedId == actualId) continue;
                var kind = expectedId == EmptyVoxel ? ChiselVoxelDiffKind.Extra
                    : actualId == EmptyVoxel ? ChiselVoxelDiffKind.Missing
                    : ChiselVoxelDiffKind.Material;
                differences.Add(new ChiselVoxelDifference(x, y, z, kind));
            }
            return differences;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(actualVoxels);
        }
    }

    private static IReadOnlyList<ChiselVoxelDifference> InvalidDifferences()
    {
        var invalid = new List<ChiselVoxelDifference>(VoxelCount);
        for (var x = 0; x < VoxelSize; x++)
        for (var y = 0; y < VoxelSize; y++)
        for (var z = 0; z < VoxelSize; z++)
            invalid.Add(new ChiselVoxelDifference(x, y, z, ChiselVoxelDiffKind.Material));
        return invalid;
    }

    private static bool TryExpand(ChiselVoxelData data, int[] voxels)
    {
        Array.Fill(voxels, EmptyVoxel, 0, VoxelCount);

        foreach (var packed in data.VoxelCuboids)
        {
            var x0 = (int)(packed & 0xF);
            var y0 = (int)((packed >> 4) & 0xF);
            var z0 = (int)((packed >> 8) & 0xF);
            var x1 = (int)((packed >> 12) & 0xF) + 1;
            var y1 = (int)((packed >> 16) & 0xF) + 1;
            var z1 = (int)((packed >> 20) & 0xF) + 1;
            var materialIndex = (int)((packed >> 24) & 0xFF);
            if (materialIndex >= data.BlockIds.Length) return false;

            var blockId = data.BlockIds[materialIndex];
            for (var x = x0; x < x1; x++)
            for (var y = y0; y < y1; y++)
            for (var z = z0; z < z1; z++)
                voxels[(x * VoxelSize + y) * VoxelSize + z] = blockId;
        }

        return true;
    }

    public sealed class Snapshot
    {
        internal int[] Voxels { get; }
        internal bool IsValid { get; }

        internal Snapshot(int[] voxels, bool isValid)
        {
            Voxels = voxels;
            IsValid = isValid;
        }
    }
}

public enum ChiselVoxelDiffKind
{
    Missing,
    Extra,
    Material
}

public readonly record struct ChiselVoxelDifference(int X, int Y, int Z, ChiselVoxelDiffKind Kind);
