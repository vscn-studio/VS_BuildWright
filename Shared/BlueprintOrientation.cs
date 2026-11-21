using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Buildwright.Protocol;

internal static class BlueprintOrientation
{
    // Projection matrices rotate (x, z) to (z, -x). Native schematic hooks
    // use the opposite sign. Normalize for mods that expect positive angles.
    public static int NativeDegrees(int turns) => (4 - (turns % 4 + 4) % 4) % 4 * 90;

    public static Block ResolveBlock(IWorldAccessor world, Block block, int turns, EnumAxis? mirror)
    {
        block = mirror switch
        {
            EnumAxis.Y => world.GetBlock(block.GetVerticallyFlippedBlockCode()) ?? block,
            EnumAxis.X => world.GetBlock(block.GetHorizontallyFlippedBlockCode(EnumAxis.X)) ?? block,
            EnumAxis.Z => world.GetBlock(block.GetHorizontallyFlippedBlockCode(EnumAxis.Z)) ?? block,
            _ => block
        };
        var angle = NativeDegrees(turns);
        return angle == 0 ? block : world.GetBlock(block.GetRotatedBlockCode(angle)) ?? block;
    }

    public static Vec3f TransformPoint(Vec3f point, int turns, EnumAxis? mirror)
    {
        var x = mirror == EnumAxis.X ? 1f - point.X : point.X;
        var y = mirror == EnumAxis.Y ? 1f - point.Y : point.Y;
        var z = mirror == EnumAxis.Z ? 1f - point.Z : point.Z;
        return (((turns % 4) + 4) % 4) switch
        {
            1 => new Vec3f(z, y, 1f - x),
            2 => new Vec3f(1f - x, y, 1f - z),
            3 => new Vec3f(1f - z, y, x),
            _ => new Vec3f(x, y, z)
        };
    }

    public static int TransformFacing(int facingIndex, int turns, EnumAxis? mirror)
    {
        if (facingIndex < 0 || facingIndex >= BlockFacing.ALLFACES.Length) return facingIndex;
        var face = BlockFacing.ALLFACES[facingIndex];
        if (face.Axis == mirror) face = face.Opposite;
        return face.IsHorizontal ? face.GetHorizontalRotated(NativeDegrees(turns)).Index : face.Index;
    }

    public static bool TransformBeams(ITreeAttribute tree, int turns, EnumAxis? mirror)
    {
        var bytes = tree.GetBytes("beams");
        if (bytes == null) return false;
        var beams = bytes.Length == 0 ? Array.Empty<PlacedBeam>()
            : SerializerUtil.Deserialize<PlacedBeam[]>(bytes) ?? Array.Empty<PlacedBeam>();
        foreach (var beam in beams)
        {
            if (beam?.Start == null || beam.End == null) continue;
            beam.Start = TransformPoint(beam.Start, turns, mirror);
            beam.End = TransformPoint(beam.End, turns, mirror);
            beam.FacingIndex = TransformFacing(beam.FacingIndex, turns, mirror);
        }
        tree.SetBytes("beams", SerializerUtil.Serialize(beams));
        return true;
    }
}
