using Buildwright.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Buildwright.Client;

internal static class SupportBeamDataComparer
{
    private const float PositionTolerance = 0.002f;

    public static bool TryCompare(ICoreClientAPI capi, BlockSchematic schematic,
        ITreeAttribute expectedTree, ITreeAttribute actualTree, out bool equivalent)
    {
        equivalent = false;
        var expectedBytes = expectedTree.GetBytes("beams");
        var actualBytes = actualTree.GetBytes("beams");
        if (expectedBytes == null && actualBytes == null) return false;
        if (expectedBytes == null || actualBytes == null) return true;

        var expected = SerializerUtil.Deserialize<PlacedBeam[]>(expectedBytes) ?? Array.Empty<PlacedBeam>();
        var actual = SerializerUtil.Deserialize<PlacedBeam[]>(actualBytes) ?? Array.Empty<PlacedBeam>();
        equivalent = AreEquivalent(expected, actual,
            id => ResolveExpectedCode(capi, schematic, id),
            id => capi.World.GetBlock(id)?.Code?.ToString());
        return true;
    }

    internal static bool AreEquivalent(IReadOnlyList<PlacedBeam> expected, IReadOnlyList<PlacedBeam> actual,
        System.Func<int, string?> expectedCode, System.Func<int, string?> actualCode)
    {
        if (expected.Count != actual.Count) return false;
        var actualMatches = Enumerable.Repeat(-1, actual.Count).ToArray();
        for (var expectedIndex = 0; expectedIndex < expected.Count; expectedIndex++)
        {
            var visited = new bool[actual.Count];
            if (!TryMatch(expectedIndex, visited)) return false;
        }
        return true;

        bool TryMatch(int expectedIndex, bool[] visited)
        {
            for (var actualIndex = 0; actualIndex < actual.Count; actualIndex++)
            {
                if (visited[actualIndex] || !BeamEquals(expected[expectedIndex], actual[actualIndex],
                        expectedCode, actualCode)) continue;
                visited[actualIndex] = true;
                if (actualMatches[actualIndex] < 0 || TryMatch(actualMatches[actualIndex], visited))
                {
                    actualMatches[actualIndex] = expectedIndex;
                    return true;
                }
            }
            return false;
        }
    }

    public static bool TransformInPlace(ITreeAttribute tree, int rotation, MirrorAxis mirror)
    {
        var bytes = tree.GetBytes("beams");
        if (bytes == null) return false;
        var beams = SerializerUtil.Deserialize<PlacedBeam[]>(bytes) ?? Array.Empty<PlacedBeam>();
        foreach (var beam in beams)
        {
            if (beam?.Start == null || beam.End == null) continue;
            beam.Start = TransformPoint(beam.Start, rotation, mirror);
            beam.End = TransformPoint(beam.End, rotation, mirror);
            beam.FacingIndex = TransformFacingIndex(beam.FacingIndex, rotation, mirror);
        }
        tree.SetBytes("beams", SerializerUtil.Serialize(beams));
        return true;
    }

    internal static Vec3f TransformPoint(Vec3f point, int rotation, MirrorAxis mirror)
    {
        var turns = ((rotation % 4) + 4) % 4;
        var x = mirror == MirrorAxis.X ? 1f - point.X : point.X;
        var y = mirror == MirrorAxis.Y ? 1f - point.Y : point.Y;
        var z = mirror == MirrorAxis.Z ? 1f - point.Z : point.Z;
        return turns switch
        {
            1 => new Vec3f(z, y, 1f - x),
            2 => new Vec3f(1f - x, y, 1f - z),
            3 => new Vec3f(1f - z, y, x),
            _ => new Vec3f(x, y, z)
        };
    }

    internal static int TransformFacingIndex(int facingIndex, int rotation, MirrorAxis mirror)
    {
        if (facingIndex < 0 || facingIndex >= BlockFacing.ALLFACES.Length) return facingIndex;
        var normal = BlockFacing.ALLFACES[facingIndex].Normali;
        var x = normal.X;
        var y = normal.Y;
        var z = normal.Z;
        if (mirror == MirrorAxis.X) x = -x;
        else if (mirror == MirrorAxis.Y) y = -y;
        else if (mirror == MirrorAxis.Z) z = -z;
        var turns = ((rotation % 4) + 4) % 4;
        var (rx, rz) = turns switch
        {
            1 => (z, -x),
            2 => (-x, -z),
            3 => (-z, x),
            _ => (x, z)
        };
        return BlockFacing.FromVector(rx, y, rz).Index;
    }

    private static bool BeamEquals(PlacedBeam expected, PlacedBeam actual,
        System.Func<int, string?> expectedCode, System.Func<int, string?> actualCode)
    {
        if (expected?.Start == null || expected.End == null || actual?.Start == null || actual.End == null)
            return false;
        var expectedMaterial = expectedCode(expected.BlockId);
        var actualMaterial = actualCode(actual.BlockId);
        return !string.IsNullOrWhiteSpace(expectedMaterial)
               && string.Equals(expectedMaterial, actualMaterial, StringComparison.OrdinalIgnoreCase)
               && expected.FacingIndex == actual.FacingIndex
               && Close(expected.Start, actual.Start)
               && Close(expected.End, actual.End);
    }

    private static bool Close(Vec3f first, Vec3f second) =>
        MathF.Abs(first.X - second.X) <= PositionTolerance
        && MathF.Abs(first.Y - second.Y) <= PositionTolerance
        && MathF.Abs(first.Z - second.Z) <= PositionTolerance;

    private static string? ResolveExpectedCode(ICoreClientAPI capi, BlockSchematic schematic, int id) =>
        schematic.BlockCodes.TryGetValue(id, out var mapped)
            ? mapped.ToString()
            : capi.World.GetBlock(id)?.Code?.ToString();
}
