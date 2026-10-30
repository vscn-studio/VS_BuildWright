using System.Numerics;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace Buildwright.Core;

/// <summary>Unions transformed solid cuboids on a bounded grid, then emits their boundary in millimetres, Z up.</summary>
public static class BlueprintStlExporter
{
    public static byte[] Build(JObject model, float longestEdgeMm = 100, int resolution = 128)
    {
        if (!float.IsFinite(longestEdgeMm) || longestEdgeMm < 1 || longestEdgeMm > 2000)
            throw new ArgumentOutOfRangeException(nameof(longestEdgeMm), "Print size must be between 1 and 2000 mm.");
        if (resolution < 8 || resolution > 256) throw new ArgumentOutOfRangeException(nameof(resolution));
        var solids = new List<Solid>();
        void Visit(JObject json, Matrix4x4 parent)
        {
            var element = json.ToObject<ShapeElement>()!;
            if (element.From is not { Length: 3 } || element.To is not { Length: 3 }) throw new InvalidDataException("Invalid cuboid bounds.");
            var m = element.GetLocalTransformMatrix(0);
            var world = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]) * parent;
            var extent = new Vector3((float)(element.To[0] - element.From[0]), (float)(element.To[1] - element.From[1]), (float)(element.To[2] - element.From[2])) / 16;
            if (json["faces"] is JObject faces && faces.Properties().Any(p => p.Value["enabled"]?.Value<bool>() != false)
                && Math.Abs(extent.X * extent.Y * extent.Z) > 1e-15f)
            {
                var transform = Matrix4x4.CreateScale(extent) * world;
                if (Matrix4x4.Invert(transform, out var inverse))
                {
                    var corners = Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1), transform)).ToArray();
                    var min = corners.Aggregate(Vector3.Min);
                    var max = corners.Aggregate(Vector3.Max);
                    if (!float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z)) throw new InvalidDataException("Model has non-finite geometry.");
                    solids.Add(new Solid(inverse, min, max));
                }
            }
            if (json["children"] is JArray children)
                foreach (var child in children.OfType<JObject>()) Visit(child, world);
        }
        foreach (var element in ((JArray)model["elements"]!).OfType<JObject>()) Visit(element, Matrix4x4.Identity);
        if (solids.Count == 0) throw new InvalidDataException("No solid cuboids to print. Flat texture planes have no printable thickness.");
        var minimum = solids.Select(s => s.Min).Aggregate(Vector3.Min);
        var maximum = solids.Select(s => s.Max).Aggregate(Vector3.Max);
        var size = maximum - minimum;
        var longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        var pitch = longest / resolution;
        int nx = Math.Max(1, (int)Math.Ceiling(size.X / pitch - 1e-5f)), ny = Math.Max(1, (int)Math.Ceiling(size.Y / pitch - 1e-5f)), nz = Math.Max(1, (int)Math.Ceiling(size.Z / pitch - 1e-5f));
        var step = size / new Vector3(nx, ny, nz);
        var occupied = new bool[checked(nx * ny * nz)];
        int Index(int x, int y, int z) => (z * ny + y) * nx + x;
        bool Has(int x, int y, int z) => x >= 0 && x < nx && y >= 0 && y < ny && z >= 0 && z < nz && occupied[Index(x, y, z)];
        long work = 0;
        foreach (var solid in solids)
        {
            var lo = (solid.Min - minimum) / step;
            var hi = (solid.Max - minimum) / step;
            int x0 = Math.Clamp((int)Math.Floor(lo.X), 0, nx - 1), y0 = Math.Clamp((int)Math.Floor(lo.Y), 0, ny - 1), z0 = Math.Clamp((int)Math.Floor(lo.Z), 0, nz - 1);
            int x1 = Math.Clamp((int)Math.Ceiling(hi.X), 0, nx), y1 = Math.Clamp((int)Math.Ceiling(hi.Y), 0, ny), z1 = Math.Clamp((int)Math.Ceiling(hi.Z), 0, nz);
            work += (long)(x1 - x0) * (y1 - y0) * (z1 - z0);
            if (work > 150_000_000) throw new InvalidDataException("Print model is too complex at this grid resolution. Lower the print resolution or export a smaller selection.");
            for (var z = z0; z < z1; z++)
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var index = Index(x, y, z);
                if (occupied[index]) continue;
                var point = Vector3.Transform(minimum + (new Vector3(x, y, z) + new Vector3(.5f)) * step, solid.Inverse);
                if (point.X >= -1e-6f && point.X <= 1.000001f && point.Y >= -1e-6f && point.Y <= 1.000001f && point.Z >= -1e-6f && point.Z <= 1.000001f)
                    occupied[index] = true;
            }
        }
        if (!occupied.Any(v => v)) throw new InvalidDataException("Solid details are smaller than the print grid. Increase resolution or export a smaller selection.");

        // At every lattice vertex both the solid and its complement must be locally connected.
        // This rejects edge/point contacts that would otherwise produce a non-manifold boundary.
        static bool Connected(int mask)
        {
            if (mask == 0) return true;
            var seen = mask & -mask;
            while (true)
            {
                var next = seen;
                for (var bit = 0; bit < 8; bit++)
                    if ((seen & (1 << bit)) != 0)
                        next |= mask & ((1 << (bit ^ 1)) | (1 << (bit ^ 2)) | (1 << (bit ^ 4)));
                if (next == seen) return seen == mask;
                seen = next;
            }
        }
        var invalid = Enumerable.Range(0, 256).Select(mask => !Connected(mask) || !Connected(mask ^ 255)).ToArray();
        for (var z = 0; z <= nz; z++)
        for (var y = 0; y <= ny; y++)
        for (var x = 0; x <= nx; x++)
        {
            var mask = 0;
            for (var i = 0; i < 8; i++)
                if (Has(x - 1 + (i & 1), y - 1 + ((i >> 1) & 1), z - 1 + ((i >> 2) & 1))) mask |= 1 << i;
            if (invalid[mask])
                throw new InvalidDataException("Print surface has a point/edge contact. Change resolution, join parts with solid material, or export them separately.");
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("Buildwright 1.0.3 | units=mm | Z-up | solid voxel union").CopyTo(header, 0);
        writer.Write(header); writer.Write(0u);
        uint count = 0;
        var scale = longestEdgeMm / longest;
        Vector3 Print(Vector3 grid)
        {
            var p = grid * step * scale;
            return new Vector3(p.X, size.Z * scale - p.Z, p.Y);
        }
        void Vector(Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            if (++count > 2_000_000) throw new InvalidDataException("Print surface is too complex. Lower the print resolution.");
            a = Print(a); b = Print(b); c = Print(c);
            Vector(Vector3.Normalize(Vector3.Cross(b - a, c - a)));
            Vector(a); Vector(b); Vector(c); writer.Write((ushort)0);
        }
        void Quad(Vector3 a, Vector3 u, Vector3 v)
        { Triangle(a, a + u, a + u + v); Triangle(a, a + u + v, a + v); }
        for (var z = 0; z < nz; z++)
        for (var y = 0; y < ny; y++)
        for (var x = 0; x < nx; x++)
        {
            if (!Has(x, y, z)) continue;
            var p = new Vector3(x, y, z);
            if (!Has(x - 1, y, z)) Quad(p, Vector3.UnitZ, Vector3.UnitY);
            if (!Has(x + 1, y, z)) Quad(p + Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
            if (!Has(x, y - 1, z)) Quad(p, Vector3.UnitX, Vector3.UnitZ);
            if (!Has(x, y + 1, z)) Quad(p + Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
            if (!Has(x, y, z - 1)) Quad(p, Vector3.UnitY, Vector3.UnitX);
            if (!Has(x, y, z + 1)) Quad(p + Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        }
        output.Position = 80; writer.Write(count);
        return output.ToArray();
    }

    private sealed record Solid(Matrix4x4 Inverse, Vector3 Min, Vector3 Max);
}
