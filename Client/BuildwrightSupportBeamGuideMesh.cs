using Buildwright.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Buildwright.Client;

internal sealed class BuildwrightSupportBeamGuideMesh : IDisposable
{
    private const float BoundaryTolerance = 0.01f;
    private readonly ICoreClientAPI capi;
    private readonly IReadOnlyList<SupportBeamGuide> guides;
    private readonly MeshRef?[] layers;
    private MeshRef? hoverMesh;
    private HoverKey? hoverKey;

    public bool HasMesh => layers.Any(layer => layer?.Initialized == true);

    private BuildwrightSupportBeamGuideMesh(ICoreClientAPI capi, int layerCount,
        IReadOnlyList<SupportBeamGuide> guides)
    {
        this.capi = capi;
        this.guides = guides;
        layers = new MeshRef?[Math.Max(1, layerCount)];
        foreach (var group in guides.GroupBy(guide => guide.Cell.Y))
        {
            if (group.Key < 0 || group.Key >= layers.Length) continue;
            var builder = new LineMeshBuilder();
            foreach (var guide in group) AddGuide(builder, guide);
            var mesh = builder.Build();
            if (mesh.VerticesCount == 0)
            {
                mesh.Dispose();
                continue;
            }
            layers[group.Key] = capi.Render.UploadMesh(mesh);
            mesh.Dispose();
        }
    }

    public static BuildwrightSupportBeamGuideMesh? TryCreate(ICoreClientAPI capi, BlueprintDocument document,
        int layerCount, System.Func<int, int, int, bool>? includeCell = null)
    {
        try
        {
            var guides = ReadGuides(document, includeCell);
            return guides.Count == 0 ? null : new BuildwrightSupportBeamGuideMesh(capi, layerCount, guides);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("[buildwright] Support beam guide creation failed: {0}", exception.Message);
            return null;
        }
    }

    public MeshRef? GetLayer(int layer) => layer >= 0 && layer < layers.Length ? layers[layer] : null;
    public MeshRef? HoverMesh => hoverMesh;

    public void UpdateStartPointer(Vec3d camera, Vec3f view, BlockPos origin, Int3 anchorOffset,
        int rotation, MirrorAxis mirror, int visibleLayers, Vec3d? cursorWorld = null,
        int? placementGridSize = null)
    {
        var inverse = CreateInverseTransform(anchorOffset, rotation, mirror);
        var rayOrigin = inverse.TransformVector(new Vec4f((float)(camera.X - origin.X),
            (float)(camera.Y - origin.Y), (float)(camera.Z - origin.Z), 1f)).XYZ;
        var rayDirection = inverse.TransformVector(new Vec4f(view.X, view.Y, view.Z, 0f)).XYZ.Normalize();
        Vec3f? cursor = null;
        if (cursorWorld != null)
            cursor = inverse.TransformVector(new Vec4f((float)(cursorWorld.X - origin.X),
                (float)(cursorWorld.Y - origin.Y), (float)(cursorWorld.Z - origin.Z), 1f)).XYZ;

        PointerHit? best = null;
        for (var index = 0; index < guides.Count; index++)
        {
            var guide = guides[index];
            if (guide.Cell.Y >= visibleLayers) continue;
            Consider(index, false, guide.Cell, guide.Start, guide.StartFace, guide.GridSize);
        }

        if (best == null)
        {
            SetHover(null, null);
            return;
        }
        SetPointerHit(best.Value);

        void Consider(int guideIndex, bool isEnd, Int3 cell, Vec3f localPoint, BlockFacing face, int gridSize)
        {
            var target = new Vec3f(cell.X + localPoint.X, cell.Y + localPoint.Y, cell.Z + localPoint.Z);
            Vec3f intersection;
            float distance;
            if (cursor != null && TryProjectCursorHit(cursor, cell, target, face, out intersection))
                distance = new Vec3f(intersection.X - rayOrigin.X, intersection.Y - rayOrigin.Y,
                    intersection.Z - rayOrigin.Z).Length();
            else if (!TryIntersectGuidePlane(rayOrigin, rayDirection, cell, target, face, out intersection,
                         out distance)) return;
            if (best != null && best.Value.Distance <= distance) return;
            var snapped = SnapToGuideGrid(intersection, cell, target, face, placementGridSize ?? gridSize);
            best = new PointerHit(guideIndex, isEnd, cell, target, face, gridSize,
                intersection, snapped, distance);
        }
    }

    public void UpdateEndPointer(BlockPos origin, Int3 anchorOffset, int rotation, MirrorAxis mirror,
        int visibleLayers, Vec3d startWorld, Vec3d endWorld)
    {
        var inverse = CreateInverseTransform(anchorOffset, rotation, mirror);
        var start = WorldToBlueprint(inverse, origin, startWorld);
        var end = WorldToBlueprint(inverse, origin, endWorld);
        var index = FindMatchingGuideIndex(guides, start, end, visibleLayers);
        if (index < 0)
        {
            SetHover(null, null);
            return;
        }

        var guide = guides[index];
        var expectedEnd = new Vec3f(guide.Cell.X + guide.End.X, guide.Cell.Y + guide.End.Y,
            guide.Cell.Z + guide.End.Z);
        var endCell = EndGridCell(expectedEnd, guide.EndFace);
        SetPointerHit(new PointerHit(index, true, endCell, expectedEnd, guide.EndFace, guide.GridSize,
            end, end, DistanceSquared(end, expectedEnd)));
    }

    internal static int FindMatchingGuideIndex(IReadOnlyList<SupportBeamGuide> source, Vec3f start,
        Vec3f end, int visibleLayers)
    {
        const float startToleranceSquared = 0.02f * 0.02f;
        var bestIndex = -1;
        var bestEndDistance = float.MaxValue;
        for (var index = 0; index < source.Count; index++)
        {
            var guide = source[index];
            if (guide.Cell.Y >= visibleLayers) continue;
            var expectedStart = new Vec3f(guide.Cell.X + guide.Start.X, guide.Cell.Y + guide.Start.Y,
                guide.Cell.Z + guide.Start.Z);
            if (DistanceSquared(start, expectedStart) > startToleranceSquared) continue;
            var expectedEnd = new Vec3f(guide.Cell.X + guide.End.X, guide.Cell.Y + guide.End.Y,
                guide.Cell.Z + guide.End.Z);
            var endDistance = DistanceSquared(end, expectedEnd);
            if (endDistance >= bestEndDistance) continue;
            bestIndex = index;
            bestEndDistance = endDistance;
        }
        return bestIndex;
    }

    private void SetPointerHit(PointerHit hit)
    {
        var key = new HoverKey(hit.GuideIndex, hit.IsEnd, Quantize(hit.Snapped.X),
            Quantize(hit.Snapped.Y), Quantize(hit.Snapped.Z), QuantizePointer(hit.Intersection.X),
            QuantizePointer(hit.Intersection.Y), QuantizePointer(hit.Intersection.Z));
        if (hoverKey == key) return;
        var builder = new LineMeshBuilder();
        AddPointerHighlight(builder, hit);
        SetHover(key, builder.Build());
    }

    private static Matrixf CreateInverseTransform(Int3 anchorOffset, int rotation, MirrorAxis mirror)
    {
        var mx = mirror == MirrorAxis.X ? -1f : 1f;
        var my = mirror == MirrorAxis.Y ? -1f : 1f;
        var mz = mirror == MirrorAxis.Z ? -1f : 1f;
        return new Matrixf().Identity()
            .Translate(0.5f, 0f, 0.5f).RotateY(rotation * MathF.PI / 2f).Translate(-0.5f, 0f, -0.5f)
            .Translate(0.5f, 0.5f, 0.5f).Scale(mx, my, mz).Translate(-0.5f, -0.5f, -0.5f)
            .Translate(-anchorOffset.X, -anchorOffset.Y, -anchorOffset.Z).Invert();
    }

    private static Vec3f WorldToBlueprint(Matrixf inverse, BlockPos origin, Vec3d point) =>
        inverse.TransformVector(new Vec4f((float)(point.X - origin.X), (float)(point.Y - origin.Y),
            (float)(point.Z - origin.Z), 1f)).XYZ;

    private static float DistanceSquared(Vec3f first, Vec3f second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        var z = first.Z - second.Z;
        return x * x + y * y + z * z;
    }

    internal static bool TryProjectCursorHit(Vec3f cursor, Int3 cell, Vec3f target, BlockFacing face,
        out Vec3f projected)
    {
        var normal = face.Normalf;
        var signedDistance = new Vec3f(cursor.X - target.X, cursor.Y - target.Y, cursor.Z - target.Z).Dot(normal);
        projected = new Vec3f(cursor.X - normal.X * signedDistance, cursor.Y - normal.Y * signedDistance,
            cursor.Z - normal.Z * signedDistance);
        if (MathF.Abs(signedDistance) > 0.08f) return false;
        return IsInsideGuideFace(projected, cell, 0.04f, face.Axis);
    }

    internal static bool TryIntersectGuidePlane(Vec3f rayOrigin, Vec3f rayDirection, Int3 cell,
        Vec3f target, BlockFacing face, out Vec3f intersection, out float distance)
    {
        intersection = new Vec3f();
        distance = 0;
        var normal = face.Normalf;
        var denominator = rayDirection.Dot(normal);
        if (MathF.Abs(denominator) < 0.0001f) return false;
        distance = new Vec3f(target.X - rayOrigin.X, target.Y - rayOrigin.Y, target.Z - rayOrigin.Z)
            .Dot(normal) / denominator;
        if (distance < 0 || distance > 128f) return false;
        intersection.Set(rayOrigin.X + rayDirection.X * distance, rayOrigin.Y + rayDirection.Y * distance,
            rayOrigin.Z + rayDirection.Z * distance);
        return IsInsideGuideFace(intersection, cell, 0.04f, face.Axis);
    }

    private static bool IsInsideGuideFace(Vec3f point, Int3 cell, float tolerance, EnumAxis axis) =>
        axis switch
        {
            EnumAxis.X => point.Y >= cell.Y - tolerance && point.Y <= cell.Y + 1 + tolerance
                && point.Z >= cell.Z - tolerance && point.Z <= cell.Z + 1 + tolerance,
            EnumAxis.Y => point.X >= cell.X - tolerance && point.X <= cell.X + 1 + tolerance
                && point.Z >= cell.Z - tolerance && point.Z <= cell.Z + 1 + tolerance,
            _ => point.X >= cell.X - tolerance && point.X <= cell.X + 1 + tolerance
                && point.Y >= cell.Y - tolerance && point.Y <= cell.Y + 1 + tolerance
        };

    internal static Vec3f SnapToGuideGrid(Vec3f intersection, Int3 cell, Vec3f target,
        BlockFacing face, int gridSize)
    {
        float Snap(float value, int offset) =>
            offset + Math.Clamp(MathF.Round((value - offset) * gridSize), 0f, gridSize) / gridSize;
        return face.Axis switch
        {
            EnumAxis.X => new Vec3f(target.X, Snap(intersection.Y, cell.Y), Snap(intersection.Z, cell.Z)),
            EnumAxis.Y => new Vec3f(Snap(intersection.X, cell.X), target.Y, Snap(intersection.Z, cell.Z)),
            _ => new Vec3f(Snap(intersection.X, cell.X), Snap(intersection.Y, cell.Y), target.Z)
        };
    }

    internal static bool TryResolveGameStartPoint(BlockPos selectedPos, Vec3d hitPosition,
        BlockFacing selectedFace, bool selectedHasSupportBeam, int selectedReplaceable,
        bool adjacentHasSupportBeam, int adjacentReplaceable, int gridSize, out Vec3d worldPoint)
    {
        var snapped = new Vec3f(SnapCoordinate((float)hitPosition.X, gridSize),
            SnapCoordinate((float)hitPosition.Y, gridSize), SnapCoordinate((float)hitPosition.Z, gridSize));
        BlockPos startPos;
        Vec3f startOffset;
        if (!selectedHasSupportBeam && selectedReplaceable < 6000)
        {
            if (!adjacentHasSupportBeam && adjacentReplaceable < 6000)
            {
                worldPoint = new Vec3d();
                return false;
            }
            startPos = selectedPos.AddCopy(selectedFace);
            var normal = selectedFace.Normali;
            startOffset = new Vec3f(snapped.X - normal.X, snapped.Y - normal.Y, snapped.Z - normal.Z);
        }
        else
        {
            startPos = selectedPos.Copy();
            startOffset = snapped;
        }
        worldPoint = new Vec3d(startPos.X + startOffset.X, startPos.Y + startOffset.Y,
            startPos.Z + startOffset.Z);
        return true;
    }

    private static float SnapCoordinate(float value, int gridSize) =>
        (float)(int)Math.Round(value * gridSize) / gridSize;

    internal static IReadOnlyList<SupportBeamGuide> ReadGuides(BlueprintDocument document,
        System.Func<int, int, int, bool>? includeCell = null)
    {
        var guides = new List<SupportBeamGuide>();
        var seen = new HashSet<(int X, int Y, int Z, int SX, int SY, int SZ, int EX, int EY, int EZ)>();
        foreach (var record in document.BlockEntities)
        {
            var cell = record.Position;
            if (includeCell != null && !includeCell(cell.X, cell.Y, cell.Z)) continue;
            var tree = BlockEntityCapture.Decode(record.TreeAscii85);
            var bytes = tree?.GetBytes("beams");
            if (bytes == null || bytes.Length == 0) continue;
            PlacedBeam[] beams;
            try { beams = SerializerUtil.Deserialize<PlacedBeam[]>(bytes) ?? Array.Empty<PlacedBeam>(); }
            catch { continue; }
            foreach (var beam in beams)
            {
                if (beam?.Start == null || beam.End == null) continue;
                var placementFace = beam.FacingIndex >= 0 && beam.FacingIndex < BlockFacing.ALLFACES.Length
                    ? BlockFacing.ALLFACES[beam.FacingIndex]
                    : BlockFacing.NORTH;
                var startFace = ResolveStartFace(beam.Start, beam.FacingIndex);
                var endFace = ResolveEndFace(beam.Start, beam.End);
                var key = (cell.X, cell.Y, cell.Z,
                    Quantize(beam.Start.X), Quantize(beam.Start.Y), Quantize(beam.Start.Z),
                    Quantize(beam.End.X), Quantize(beam.End.Y), Quantize(beam.End.Z));
                if (!seen.Add(key)) continue;
                guides.Add(new SupportBeamGuide(new Int3(cell.X, cell.Y, cell.Z), beam.Start.Clone(), beam.End.Clone(),
                    startFace, endFace, placementFace, InferGridSize(beam.Start)));
            }
        }
        return guides;
    }

    internal static BlockFacing ResolveStartFace(Vec3f start, int facingIndex)
    {
        var stored = facingIndex >= 0 && facingIndex < BlockFacing.ALLFACES.Length
            ? BlockFacing.ALLFACES[facingIndex]
            : BlockFacing.NORTH;
        if (DistanceToFace(start, stored) <= BoundaryTolerance) return stored;
        if (DistanceToFace(start, stored.Opposite) <= BoundaryTolerance) return stored.Opposite;

        var nearest = stored;
        var nearestDistance = float.MaxValue;
        foreach (var facing in BlockFacing.ALLFACES)
        {
            var distance = DistanceToFace(start, facing);
            if (distance >= nearestDistance) continue;
            nearest = facing;
            nearestDistance = distance;
        }
        return nearestDistance <= BoundaryTolerance ? nearest : stored;
    }

    internal static int InferGridSize(Vec3f start) =>
        IsQuarter(start.X) && IsQuarter(start.Y) && IsQuarter(start.Z) ? 4 : 16;

    internal static BlockFacing ResolveEndFace(Vec3f start, Vec3f end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dz = end.Z - start.Z;
        BlockFacing? result = null;
        var best = -1f;
        Consider(end.X, MathF.Abs(dx), dx >= 0 ? BlockFacing.WEST : BlockFacing.EAST);
        Consider(end.Y, MathF.Abs(dy), dy >= 0 ? BlockFacing.DOWN : BlockFacing.UP);
        Consider(end.Z, MathF.Abs(dz), dz >= 0 ? BlockFacing.NORTH : BlockFacing.SOUTH);
        if (result != null) return result;
        if (MathF.Abs(dx) >= MathF.Abs(dy) && MathF.Abs(dx) >= MathF.Abs(dz))
            return dx >= 0 ? BlockFacing.WEST : BlockFacing.EAST;
        if (MathF.Abs(dy) >= MathF.Abs(dz)) return dy >= 0 ? BlockFacing.DOWN : BlockFacing.UP;
        return dz >= 0 ? BlockFacing.NORTH : BlockFacing.SOUTH;

        void Consider(float coordinate, float score, BlockFacing facing)
        {
            if (MathF.Abs(coordinate - MathF.Round(coordinate)) > BoundaryTolerance || score <= best) return;
            best = score;
            result = facing;
        }
    }

    private static bool IsQuarter(float value) => MathF.Abs(value * 4f - MathF.Round(value * 4f)) < 0.001f;
    private static int Quantize(float value) => (int)MathF.Round(value * 64f);
    private static int QuantizePointer(float value) => (int)MathF.Round(value * 512f);

    private static float DistanceToFace(Vec3f point, BlockFacing facing)
    {
        var normal = facing.Normali;
        if (normal.X != 0) return MathF.Abs(point.X - (normal.X > 0 ? 1f : 0f));
        if (normal.Y != 0) return MathF.Abs(point.Y - (normal.Y > 0 ? 1f : 0f));
        return MathF.Abs(point.Z - (normal.Z > 0 ? 1f : 0f));
    }

    private static void AddGuide(LineMeshBuilder builder, SupportBeamGuide guide)
    {
        var gridColor = new Rgba(48, 210, 230, 145);
        var outlineColor = new Rgba(78, 225, 240, 225);
        AddFaceGrid(builder, guide.Cell, guide.Start, guide.StartFace, guide.GridSize, gridColor, outlineColor);

        var endPoint = new Vec3f(guide.Cell.X + guide.End.X, guide.Cell.Y + guide.End.Y, guide.Cell.Z + guide.End.Z);
        var endCell = EndGridCell(endPoint, guide.EndFace);
        var endLocal = new Vec3f(endPoint.X - endCell.X, endPoint.Y - endCell.Y, endPoint.Z - endCell.Z);
        AddFaceGrid(builder, endCell, endLocal, guide.EndFace, guide.GridSize, gridColor, outlineColor);

        var startPoint = new Vec3f(guide.Cell.X + guide.Start.X, guide.Cell.Y + guide.Start.Y,
            guide.Cell.Z + guide.Start.Z);
        var startColor = new Rgba(255, 196, 36, 255);
        var endColor = new Rgba(110, 235, 110, 255);
        AddNormal(builder, startPoint, guide.PlacementFace, startColor);
        AddNormal(builder, endPoint, guide.EndFace, endColor);
    }

    private static void AddPointerHighlight(LineMeshBuilder builder, PointerHit hit)
    {
        var normal = hit.Face.Normali;
        var offset = new Vec3f(normal.X * 0.014f, normal.Y * 0.014f, normal.Z * 0.014f);
        var snapped = new Vec3f(hit.Snapped.X + offset.X, hit.Snapped.Y + offset.Y, hit.Snapped.Z + offset.Z);
        var crossColor = new Rgba(255, 255, 255, 255);
        AddNormal(builder, snapped, hit.Face, crossColor);
    }

    private static void AddFaceGrid(LineMeshBuilder builder, Int3 cell, Vec3f point, BlockFacing face,
        int gridSize, Rgba gridColor, Rgba outlineColor)
    {
        var normal = face.Normali;
        var plane = PlaneCoordinate(point, face);
        var offset = new Vec3f(normal.X * 0.003f, normal.Y * 0.003f, normal.Z * 0.003f);
        for (var step = 0; step <= gridSize; step++)
        {
            var value = (float)step / gridSize;
            AddGridLinePair(builder, cell, face.Axis, plane, value, offset,
                step == 0 || step == gridSize ? outlineColor : gridColor);
        }
    }

    private static void AddNormal(LineMeshBuilder builder, Vec3f point, BlockFacing face, Rgba color)
    {
        var normal = face.Normali;
        var start = new Vec3f(point.X + normal.X * 0.007f, point.Y + normal.Y * 0.007f,
            point.Z + normal.Z * 0.007f);
        builder.Add(start, new Vec3f(start.X + normal.X * 0.2f, start.Y + normal.Y * 0.2f,
            start.Z + normal.Z * 0.2f), color);
    }

    private static Int3 EndGridCell(Vec3f point, BlockFacing face)
    {
        var cell = new Int3(CellCoordinate(point.X), CellCoordinate(point.Y), CellCoordinate(point.Z));
        var normal = face.Normali;
        if (normal.X != 0) cell.X = (int)MathF.Round(point.X) - (normal.X > 0 ? 1 : 0);
        if (normal.Y != 0) cell.Y = (int)MathF.Round(point.Y) - (normal.Y > 0 ? 1 : 0);
        if (normal.Z != 0) cell.Z = (int)MathF.Round(point.Z) - (normal.Z > 0 ? 1 : 0);
        return cell;
    }

    private static int CellCoordinate(float value) => (int)MathF.Floor(value - 0.0001f);

    private static float PlaneCoordinate(Vec3f start, BlockFacing facing)
    {
        var normal = facing.Normali;
        if (normal.X != 0) return IsBoundary(start.X) ? MathF.Round(start.X) : start.X;
        if (normal.Y != 0) return IsBoundary(start.Y) ? MathF.Round(start.Y) : start.Y;
        return IsBoundary(start.Z) ? MathF.Round(start.Z) : start.Z;
    }

    private static bool IsBoundary(float value) =>
        MathF.Abs(value) <= BoundaryTolerance || MathF.Abs(value - 1f) <= BoundaryTolerance;

    private static void AddGridLinePair(LineMeshBuilder builder, Int3 cell, EnumAxis axis, float plane,
        float value, Vec3f offset, Rgba color)
    {
        Vec3f firstStart;
        Vec3f firstEnd;
        Vec3f secondStart;
        Vec3f secondEnd;
        switch (axis)
        {
            case EnumAxis.X:
                firstStart = new Vec3f(plane, 0, value); firstEnd = new Vec3f(plane, 1, value);
                secondStart = new Vec3f(plane, value, 0); secondEnd = new Vec3f(plane, value, 1);
                break;
            case EnumAxis.Y:
                firstStart = new Vec3f(0, plane, value); firstEnd = new Vec3f(1, plane, value);
                secondStart = new Vec3f(value, plane, 0); secondEnd = new Vec3f(value, plane, 1);
                break;
            default:
                firstStart = new Vec3f(0, value, plane); firstEnd = new Vec3f(1, value, plane);
                secondStart = new Vec3f(value, 0, plane); secondEnd = new Vec3f(value, 1, plane);
                break;
        }
        builder.Add(ToBlueprint(firstStart, cell, offset), ToBlueprint(firstEnd, cell, offset), color);
        builder.Add(ToBlueprint(secondStart, cell, offset), ToBlueprint(secondEnd, cell, offset), color);
    }

    private static Vec3f ToBlueprint(Vec3f point, Int3 cell, Vec3f offset) =>
        new(cell.X + point.X + offset.X, cell.Y + point.Y + offset.Y, cell.Z + point.Z + offset.Z);

    public void Dispose()
    {
        foreach (var layer in layers) layer?.Dispose();
        hoverMesh?.Dispose();
        hoverMesh = null;
    }

    internal sealed record SupportBeamGuide(Int3 Cell, Vec3f Start, Vec3f End,
        BlockFacing StartFace, BlockFacing EndFace, BlockFacing PlacementFace, int GridSize);

    private void SetHover(HoverKey? key, MeshData? data)
    {
        if (key == null && hoverKey == null && hoverMesh == null)
        {
            data?.Dispose();
            return;
        }
        hoverMesh?.Dispose();
        hoverMesh = null;
        hoverKey = key;
        if (data == null) return;
        if (data.VerticesCount > 0) hoverMesh = capi.Render.UploadMesh(data);
        data.Dispose();
    }

    private readonly record struct HoverKey(int GuideIndex, bool IsEnd, int X, int Y, int Z,
        int RawX, int RawY, int RawZ);
    private readonly record struct PointerHit(int GuideIndex, bool IsEnd, Int3 Cell, Vec3f Target,
        BlockFacing Face, int GridSize, Vec3f Intersection, Vec3f Snapped, float Distance);

    private readonly record struct Rgba(byte Red, byte Green, byte Blue, byte Alpha);

    private sealed class LineMeshBuilder
    {
        private readonly List<float> xyz = new();
        private readonly List<byte> rgba = new();
        private readonly List<int> indices = new();
        private readonly List<int> flags = new();

        public void Add(Vec3f start, Vec3f end, Rgba color)
        {
            var index = flags.Count;
            AddVertex(start, color);
            AddVertex(end, color);
            indices.Add(index);
            indices.Add(index + 1);
        }

        public MeshData Build() => new(false)
        {
            xyz = xyz.ToArray(),
            Rgba = rgba.ToArray(),
            Indices = indices.ToArray(),
            Flags = flags.ToArray(),
            VerticesCount = flags.Count,
            VerticesMax = flags.Count,
            IndicesCount = indices.Count,
            IndicesMax = indices.Count,
            mode = EnumDrawMode.Lines
        };

        private void AddVertex(Vec3f point, Rgba color)
        {
            xyz.Add(point.X); xyz.Add(point.Y); xyz.Add(point.Z);
            rgba.Add(color.Red); rgba.Add(color.Green); rgba.Add(color.Blue); rgba.Add(color.Alpha);
            flags.Add(1 << 8);
        }
    }
}
