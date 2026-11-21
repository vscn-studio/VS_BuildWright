using Buildwright.Core;
using Buildwright.Protocol;
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

    public static bool TransformInPlace(ITreeAttribute tree, int rotation, MirrorAxis mirror) =>
        BlueprintOrientation.TransformBeams(tree, rotation, MirrorToAxis(mirror));

    internal static Vec3f TransformPoint(Vec3f point, int rotation, MirrorAxis mirror) =>
        BlueprintOrientation.TransformPoint(point, rotation, MirrorToAxis(mirror));

    internal static int TransformFacingIndex(int facingIndex, int rotation, MirrorAxis mirror) =>
        BlueprintOrientation.TransformFacing(facingIndex, rotation, MirrorToAxis(mirror));

    internal static EnumAxis? MirrorToAxis(MirrorAxis mirror) => mirror switch
    {
        MirrorAxis.X => EnumAxis.X,
        MirrorAxis.Y => EnumAxis.Y,
        MirrorAxis.Z => EnumAxis.Z,
        _ => null
    };

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
