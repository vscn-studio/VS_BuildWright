namespace Buildwright.Core;

public static class BlueprintTransforms
{
    public static Int3 CellToWorld(Int3 local, Int3 anchorOffset, Int3 origin, int rotationQuarterTurns, MirrorAxis mirror)
    {
        var x = local.X - anchorOffset.X;
        var y = local.Y - anchorOffset.Y;
        var z = local.Z - anchorOffset.Z;

        switch (mirror)
        {
            case MirrorAxis.X: x = -x; break;
            case MirrorAxis.Y: y = -y; break;
            case MirrorAxis.Z: z = -z; break;
        }

        int rx;
        int rz;
        switch (((rotationQuarterTurns % 4) + 4) % 4)
        {
            case 1: rx = z; rz = -x; break;
            case 2: rx = -x; rz = -z; break;
            case 3: rx = -z; rz = x; break;
            default: rx = x; rz = z; break;
        }

        return new Int3(origin.X + rx, origin.Y + y, origin.Z + rz);
    }

    public static Int3 TransformDimensions(Int3 dimensions, int rotationQuarterTurns)
    {
        return ((rotationQuarterTurns % 4) + 4) % 2 == 0
            ? dimensions.Copy()
            : new Int3(dimensions.Z, dimensions.Y, dimensions.X);
    }

    public static (Int3 Min, Int3 Max) BoxToWorldBounds(Int3 dimensions, Int3 anchorOffset,
        Int3 origin, int rotationQuarterTurns, MirrorAxis mirror)
    {
        if (dimensions.X <= 0 || dimensions.Y <= 0 || dimensions.Z <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Blueprint dimensions must be positive.");

        var min = new Int3(int.MaxValue, int.MaxValue, int.MaxValue);
        var max = new Int3(int.MinValue, int.MinValue, int.MinValue);
        foreach (var x in new[] { 0, dimensions.X - 1 })
        foreach (var y in new[] { 0, dimensions.Y - 1 })
        foreach (var z in new[] { 0, dimensions.Z - 1 })
        {
            var world = CellToWorld(new Int3(x, y, z), anchorOffset, origin, rotationQuarterTurns, mirror);
            min.X = Math.Min(min.X, world.X); min.Y = Math.Min(min.Y, world.Y); min.Z = Math.Min(min.Z, world.Z);
            max.X = Math.Max(max.X, world.X); max.Y = Math.Max(max.Y, world.Y); max.Z = Math.Max(max.Z, world.Z);
        }
        return (min, max);
    }

    public static Int3[] EnumerateBox(Int3 dimensions)
    {
        var result = new List<Int3>(Math.Max(0, dimensions.X * dimensions.Y * dimensions.Z));
        for (var x = 0; x < dimensions.X; x++)
        for (var y = 0; y < dimensions.Y; y++)
        for (var z = 0; z < dimensions.Z; z++)
            result.Add(new Int3(x, y, z));
        return result.ToArray();
    }
}
