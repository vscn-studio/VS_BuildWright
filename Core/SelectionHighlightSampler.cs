namespace Buildwright.Core;

public static class SelectionHighlightSampler
{
    public static List<Int3> Build(Int3 min, Int3 max, int budget)
    {
        if (budget < 8) throw new ArgumentOutOfRangeException(nameof(budget), "The highlight budget must fit all eight corners.");
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new ArgumentException("Selection minimum must not exceed its maximum.");

        var sizeX = (long)max.X - min.X + 1;
        var sizeY = (long)max.Y - min.Y + 1;
        var sizeZ = (long)max.Z - min.Z + 1;
        var volume = sizeX * sizeY * sizeZ;
        if (volume <= budget) return BuildVolume(min, max, (int)volume);

        var interiorX = Math.Max(0, sizeX - 2);
        var interiorY = Math.Max(0, sizeY - 2);
        var interiorZ = Math.Max(0, sizeZ - 2);
        var surface = volume - interiorX * interiorY * interiorZ;
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)surface / budget)));

        while (true)
        {
            var sampled = BuildSurface(min, max, step);
            if (sampled.Count <= budget) return sampled;
            step++;
        }
    }

    private static List<Int3> BuildVolume(Int3 min, Int3 max, int capacity)
    {
        var result = new List<Int3>(capacity);
        for (var x = min.X; x <= max.X; x++)
        for (var y = min.Y; y <= max.Y; y++)
        for (var z = min.Z; z <= max.Z; z++) result.Add(new Int3(x, y, z));
        return result;
    }

    private static List<Int3> BuildSurface(Int3 min, Int3 max, int step)
    {
        var points = new HashSet<(int X, int Y, int Z)>();
        var xs = SampleAxis(min.X, max.X, step);
        var ys = SampleAxis(min.Y, max.Y, step);
        var zs = SampleAxis(min.Z, max.Z, step);

        foreach (var x in xs)
        foreach (var y in ys)
        {
            points.Add((x, y, min.Z));
            points.Add((x, y, max.Z));
        }
        foreach (var x in xs)
        foreach (var z in zs)
        {
            points.Add((x, min.Y, z));
            points.Add((x, max.Y, z));
        }
        foreach (var y in ys)
        foreach (var z in zs)
        {
            points.Add((min.X, y, z));
            points.Add((max.X, y, z));
        }

        return points.Select(point => new Int3(point.X, point.Y, point.Z)).ToList();
    }

    private static List<int> SampleAxis(int min, int max, int step)
    {
        var values = new List<int>();
        for (long value = min; value <= max; value += step) values.Add((int)value);
        if (values.Count == 0 || values[^1] != max) values.Add(max);
        return values;
    }
}
