using Buildwright.Core;
using Vintagestory.API.MathTools;

namespace Buildwright.Client;

public sealed class SelectionState
{
    public int SelectedCorner { get; private set; } = 1;
    public BlockPos? Anchor { get; private set; }
    public BlockFacing? AnchorFace { get; private set; }
    public BlockPos? Min { get; private set; }
    public BlockPos? Max { get; private set; }
    public bool HasSelection => Anchor != null && Min != null && Max != null;

    public void SetCorner1(BlockPos pos, BlockFacing? face)
    {
        SelectedCorner = 1;
        Anchor = pos.Copy();
        AnchorFace = face;
        Min = pos.Copy();
        Max = pos.Copy();
    }

    public void SetCorner2(BlockPos pos)
    {
        SelectedCorner = 2;
        if (Anchor == null) { SetCorner1(pos, null); return; }
        Min = new BlockPos(Math.Min(Anchor.X, pos.X), Math.Min(Anchor.Y, pos.Y), Math.Min(Anchor.Z, pos.Z), Anchor.dimension);
        Max = new BlockPos(Math.Max(Anchor.X, pos.X), Math.Max(Anchor.Y, pos.Y), Math.Max(Anchor.Z, pos.Z), Anchor.dimension);
    }

    public void Grow(BlockFacing face, int amount)
    {
        if (Min == null || Max == null || amount <= 0) return;
        var n = face.Normali;
        if (n.X > 0) Max.X += amount; else if (n.X < 0) Min.X -= amount;
        else if (n.Y > 0) Max.Y += amount; else if (n.Y < 0) Min.Y -= amount;
        else if (n.Z > 0) Max.Z += amount; else if (n.Z < 0) Min.Z -= amount;
    }

    public int SelectNextCorner()
    {
        SelectedCorner = SelectedCorner == 1 ? 2 : 1;
        return SelectedCorner;
    }

    public void SelectCorner(int corner)
    {
        if (corner is 1 or 2) SelectedCorner = corner;
    }

    public BlockPos? GetCorner(int corner)
    {
        if (!HasSelection || Anchor == null || Min == null || Max == null) return null;
        if (corner == 1) return Anchor.Copy();

        return new BlockPos(
            Anchor.X == Min.X ? Max.X : Min.X,
            Anchor.Y == Min.Y ? Max.Y : Min.Y,
            Anchor.Z == Min.Z ? Max.Z : Min.Z,
            Anchor.dimension);
    }

    /// <summary>
    /// Moves only the selected corner, preserving the opposite corner. This
    /// mirrors Litematica's selected-element nudge behavior.
    /// </summary>
    public void NudgeSelectedCorner(BlockFacing face, int amount)
    {
        if (!HasSelection || amount == 0) return;
        var corner = GetCorner(SelectedCorner);
        var other = GetCorner(SelectedCorner == 1 ? 2 : 1);
        if (corner == null || other == null) return;

        var n = face.Normali;
        corner.X += n.X * amount;
        corner.Y += n.Y * amount;
        corner.Z += n.Z * amount;
        if (SelectedCorner == 1)
        {
            Anchor = corner;
            Min = new BlockPos(Math.Min(corner.X, other.X), Math.Min(corner.Y, other.Y), Math.Min(corner.Z, other.Z), corner.dimension);
            Max = new BlockPos(Math.Max(corner.X, other.X), Math.Max(corner.Y, other.Y), Math.Max(corner.Z, other.Z), corner.dimension);
        }
        else
        {
            SetCorner2(corner);
            SelectedCorner = 2;
        }
    }

    public void Shrink(BlockFacing face, int amount)
    {
        if (Min == null || Max == null || Anchor == null || amount <= 0) return;
        var n = face.Normali;
        if (n.X > 0)
        {
            Max.X = Math.Max(Min.X, Max.X - amount);
            Anchor.X = Math.Min(Anchor.X, Max.X);
        }
        else if (n.X < 0)
        {
            Min.X = Math.Min(Max.X, Min.X + amount);
            Anchor.X = Math.Max(Anchor.X, Min.X);
        }
        else if (n.Y > 0)
        {
            Max.Y = Math.Max(Min.Y, Max.Y - amount);
            Anchor.Y = Math.Min(Anchor.Y, Max.Y);
        }
        else if (n.Y < 0)
        {
            Min.Y = Math.Min(Max.Y, Min.Y + amount);
            Anchor.Y = Math.Max(Anchor.Y, Min.Y);
        }
        else if (n.Z > 0)
        {
            Max.Z = Math.Max(Min.Z, Max.Z - amount);
            Anchor.Z = Math.Min(Anchor.Z, Max.Z);
        }
        else if (n.Z < 0)
        {
            Min.Z = Math.Min(Max.Z, Min.Z + amount);
            Anchor.Z = Math.Max(Anchor.Z, Min.Z);
        }
    }

    /// <summary>
    /// Moves the complete selection, including its anchor, by an integer number
    /// of blocks along a facing. This is the direct equivalent of Litematica's
    /// view-relative selection nudge.
    /// </summary>
    public void Nudge(BlockFacing face, int amount)
    {
        if (Min == null || Max == null || Anchor == null || amount == 0) return;
        var n = face.Normali;
        var dx = n.X * amount;
        var dy = n.Y * amount;
        var dz = n.Z * amount;
        Min.X += dx; Min.Y += dy; Min.Z += dz;
        Max.X += dx; Max.Y += dy; Max.Z += dz;
        Anchor.X += dx; Anchor.Y += dy; Anchor.Z += dz;
    }

    /// <summary>
    /// Translates the complete selection so its anchor reaches the requested
    /// position while preserving both corners and the anchor face.
    /// </summary>
    public void MoveToAnchor(BlockPos newAnchor)
    {
        if (Min == null || Max == null || Anchor == null) return;
        var dx = newAnchor.X - Anchor.X;
        var dy = newAnchor.Y - Anchor.Y;
        var dz = newAnchor.Z - Anchor.Z;
        Min.X += dx; Min.Y += dy; Min.Z += dz;
        Max.X += dx; Max.Y += dy; Max.Z += dz;
        Anchor.X = newAnchor.X; Anchor.Y = newAnchor.Y; Anchor.Z = newAnchor.Z;
        Anchor.dimension = newAnchor.dimension;
        Min.dimension = newAnchor.dimension;
        Max.dimension = newAnchor.dimension;
    }

    public void SetTransformedBounds(BlockPos anchor, BlockFacing? anchorFace, BlockPos min, BlockPos max)
    {
        Anchor = anchor.Copy();
        AnchorFace = anchorFace;
        Min = min.Copy();
        Max = max.Copy();
    }

    public (int x, int y, int z) Dimensions => Min == null || Max == null
        ? (0, 0, 0)
        : (Max.X - Min.X + 1, Max.Y - Min.Y + 1, Max.Z - Min.Z + 1);

    public Int3 AnchorOffset => Anchor == null || Min == null
        ? new Int3()
        : new Int3(Anchor.X - Min.X, Anchor.Y - Min.Y, Anchor.Z - Min.Z);

    public void Clear()
    {
        SelectedCorner = 1;
        Anchor = null; AnchorFace = null; Min = null; Max = null;
    }
}
