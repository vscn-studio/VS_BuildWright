using Buildwright.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Buildwright.Client;

internal readonly record struct ChiselVoxelHighlight(int BlockX, int BlockY, int BlockZ,
    int VoxelX, int VoxelY, int VoxelZ, ChiselVoxelDiffKind Kind);

internal sealed class BuildwrightChiselDiffRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly Matrixf model = new();
    private ChiselVoxelHighlight[] current = Array.Empty<ChiselVoxelHighlight>();
    private readonly HashSet<BlockKey> highlightedBlocks = new();
    private readonly HashSet<BlockKey> gridBlocks = new();
    private readonly Dictionary<GridPoint, ChiselVoxelDiffKind> gridVoxels = new();
    private MeshRef? gridMesh;
    private MeshRef? outlineMesh;
    private MeshRef? hoverMesh;
    private BlockPos? origin;
    private GridPoint? hoveredVoxel;

    public double RenderOrder => 0.92;
    public int RenderRange => 9999;

    public BuildwrightChiselDiffRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT, "buildwright-chisel-diff");
        capi.Event.BlockChanged += OnBlockChanged;
    }

    public void SetHighlights(IEnumerable<ChiselVoxelHighlight> highlights)
    {
        var next = highlights.ToArray();
        if (current.SequenceEqual(next)) return;
        current = next;
        RebuildMesh();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.AfterOIT || origin == null || outlineMesh == null) return;
        var entity = capi.World.Player?.Entity;
        if (entity == null) return;
        UpdateHoverMesh();

        var render = capi.Render;
        var camera = entity.CameraPos;
        model.Identity().Set(render.CameraMatrixOrigin)
            .Translate(origin.X - camera.X, origin.Y - camera.Y, origin.Z - camera.Z);
        var shader = render.GetEngineShader(EnumShaderProgram.Wireframe);
        shader.Use();
        render.GLEnableDepthTest();
        render.GLDepthMask(false);
        render.GlToggleBlend(true);
        shader.Uniform("origin", 0f, 0f, 0f);
        shader.Uniform("colorIn", ColorUtil.WhiteArgbVec);
        shader.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);
        shader.UniformMatrix("modelViewMatrix", model.Values);
        try
        {
            render.LineWidth = 1.4f;
            render.RenderMesh(outlineMesh);
            if (gridMesh != null)
            {
                render.LineWidth = 1f;
                render.RenderMesh(gridMesh);
            }
            if (hoverMesh != null)
            {
                render.LineWidth = 2.6f;
                render.RenderMesh(hoverMesh);
            }
        }
        finally
        {
            shader.Stop();
            render.LineWidth = 1.6f;
            render.GLDepthMask(true);
        }
    }

    private void RebuildMesh()
    {
        gridMesh?.Dispose();
        outlineMesh?.Dispose();
        hoverMesh?.Dispose();
        gridMesh = null;
        outlineMesh = null;
        hoverMesh = null;
        hoveredVoxel = null;
        origin = null;
        if (current.Length == 0)
        {
            highlightedBlocks.Clear();
            gridBlocks.Clear();
            gridVoxels.Clear();
            return;
        }

        var originX = current.Min(item => item.BlockX);
        var originY = current.Min(item => item.BlockY);
        var originZ = current.Min(item => item.BlockZ);
        origin = new BlockPos(originX, originY, originZ);
        var blocks = new Dictionary<BlockKey, ChiselVoxelDiffKind>();
        foreach (var voxel in current)
        {
            var block = new BlockKey(voxel.BlockX, voxel.BlockY, voxel.BlockZ);
            if (!blocks.TryGetValue(block, out var existing) || Priority(voxel.Kind) > Priority(existing))
                blocks[block] = voxel.Kind;
        }
        highlightedBlocks.Clear();
        highlightedBlocks.UnionWith(blocks.Keys);

        var outlineEdges = new Dictionary<Edge, ChiselVoxelDiffKind>();
        foreach (var pair in blocks)
        {
            var x = (pair.Key.X - originX) * 16;
            var y = (pair.Key.Y - originY) * 16;
            var z = (pair.Key.Z - originZ) * 16;
            AddBoxEdges(outlineEdges, x, y, z, 16, pair.Value);
        }
        if (outlineEdges.Count > 0) outlineMesh = capi.Render.UploadMesh(BuildMesh(outlineEdges, 190));
        gridBlocks.RemoveWhere(block => !blocks.ContainsKey(block));
        RebuildGridMesh();
    }

    private void OnBlockChanged(BlockPos position, Block oldBlock)
    {
        if (origin == null) return;
        var block = capi.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Solid);
        var changed = new BlockKey(position.X, position.Y, position.Z);
        if (!highlightedBlocks.Contains(changed)) return;

        // The per-voxel grid only applies while a block occupies this cell.
        // Removing a chiseled block must also remove its cached grid mesh.
        if (block == null || block.Id == 0)
        {
            if (!gridBlocks.Remove(changed)) return;
            RebuildGridMesh();
            return;
        }

        if (oldBlock != null && oldBlock.Id == block.Id || !gridBlocks.Add(changed)) return;
        RebuildGridMesh();
    }

    private void RebuildGridMesh()
    {
        gridMesh?.Dispose();
        gridMesh = null;
        gridVoxels.Clear();
        if (origin == null || gridBlocks.Count == 0) return;

        foreach (var voxel in current)
        {
            if (!gridBlocks.Contains(new BlockKey(voxel.BlockX, voxel.BlockY, voxel.BlockZ))) continue;
            var point = new GridPoint((voxel.BlockX - origin.X) * 16 + voxel.VoxelX,
                (voxel.BlockY - origin.Y) * 16 + voxel.VoxelY,
                (voxel.BlockZ - origin.Z) * 16 + voxel.VoxelZ);
            if (!gridVoxels.TryGetValue(point, out var existing) || Priority(voxel.Kind) > Priority(existing))
                gridVoxels[point] = voxel.Kind;
        }

        var edges = new Dictionary<Edge, ChiselVoxelDiffKind>();
        foreach (var pair in gridVoxels) AddExposedFaceEdges(edges, gridVoxels, pair.Key, pair.Value);
        if (edges.Count > 0) gridMesh = capi.Render.UploadMesh(BuildMesh(edges));
        UpdateHoverMesh();
    }

    private void UpdateHoverMesh()
    {
        if (origin == null)
        {
            SetHoveredVoxel(null);
            return;
        }

        var selection = capi.World.Player?.CurrentBlockSelection;
        if (selection?.Position == null)
        {
            SetHoveredVoxel(null);
            return;
        }

        var block = new BlockKey(selection.Position.X, selection.Position.Y, selection.Position.Z);
        if (!gridBlocks.Contains(block))
        {
            SetHoveredVoxel(null);
            return;
        }

        var hit = selection.HitPosition;
        var voxel = new GridPoint((selection.Position.X - origin.X) * 16 + Math.Clamp((int)Math.Floor(hit.X * 16), 0, 15),
            (selection.Position.Y - origin.Y) * 16 + Math.Clamp((int)Math.Floor(hit.Y * 16), 0, 15),
            (selection.Position.Z - origin.Z) * 16 + Math.Clamp((int)Math.Floor(hit.Z * 16), 0, 15));
        SetHoveredVoxel(gridVoxels.ContainsKey(voxel) ? voxel : null);
    }

    private void SetHoveredVoxel(GridPoint? voxel)
    {
        if (hoveredVoxel == voxel) return;
        hoveredVoxel = voxel;
        hoverMesh?.Dispose();
        hoverMesh = null;
        if (!voxel.HasValue || !gridVoxels.TryGetValue(voxel.Value, out var kind)) return;

        var edges = new Dictionary<Edge, ChiselVoxelDiffKind>();
        AddBoxEdges(edges, voxel.Value.X, voxel.Value.Y, voxel.Value.Z, 1, kind);
        hoverMesh = capi.Render.UploadMesh(BuildMesh(edges, 255));
    }

    private static void AddExposedFaceEdges(IDictionary<Edge, ChiselVoxelDiffKind> edges,
        IReadOnlyDictionary<GridPoint, ChiselVoxelDiffKind> voxels, GridPoint voxel, ChiselVoxelDiffKind kind)
    {
        var x = voxel.X;
        var y = voxel.Y;
        var z = voxel.Z;
        if (!HasSameKind(voxels, x - 1, y, z, kind))
            AddFaceEdges(edges, new GridPoint(x, y, z), new GridPoint(x, y + 1, z + 1), kind, EnumAxis.X);
        if (!HasSameKind(voxels, x + 1, y, z, kind))
            AddFaceEdges(edges, new GridPoint(x + 1, y, z), new GridPoint(x + 1, y + 1, z + 1), kind, EnumAxis.X);
        if (!HasSameKind(voxels, x, y - 1, z, kind))
            AddFaceEdges(edges, new GridPoint(x, y, z), new GridPoint(x + 1, y, z + 1), kind, EnumAxis.Y);
        if (!HasSameKind(voxels, x, y + 1, z, kind))
            AddFaceEdges(edges, new GridPoint(x, y + 1, z), new GridPoint(x + 1, y + 1, z + 1), kind, EnumAxis.Y);
        if (!HasSameKind(voxels, x, y, z - 1, kind))
            AddFaceEdges(edges, new GridPoint(x, y, z), new GridPoint(x + 1, y + 1, z), kind, EnumAxis.Z);
        if (!HasSameKind(voxels, x, y, z + 1, kind))
            AddFaceEdges(edges, new GridPoint(x, y, z + 1), new GridPoint(x + 1, y + 1, z + 1), kind, EnumAxis.Z);
    }

    private static bool HasSameKind(IReadOnlyDictionary<GridPoint, ChiselVoxelDiffKind> voxels,
        int x, int y, int z, ChiselVoxelDiffKind kind) =>
        voxels.TryGetValue(new GridPoint(x, y, z), out var neighbor) && neighbor == kind;

    private static void AddFaceEdges(IDictionary<Edge, ChiselVoxelDiffKind> edges, GridPoint min,
        GridPoint max, ChiselVoxelDiffKind kind, EnumAxis normalAxis)
    {
        if (normalAxis == EnumAxis.X)
        {
            AddEdge(edges, new GridPoint(min.X, min.Y, min.Z), new GridPoint(min.X, max.Y, min.Z), kind);
            AddEdge(edges, new GridPoint(min.X, max.Y, min.Z), new GridPoint(min.X, max.Y, max.Z), kind);
            AddEdge(edges, new GridPoint(min.X, max.Y, max.Z), new GridPoint(min.X, min.Y, max.Z), kind);
            AddEdge(edges, new GridPoint(min.X, min.Y, max.Z), new GridPoint(min.X, min.Y, min.Z), kind);
        }
        else if (normalAxis == EnumAxis.Y)
        {
            AddEdge(edges, new GridPoint(min.X, min.Y, min.Z), new GridPoint(max.X, min.Y, min.Z), kind);
            AddEdge(edges, new GridPoint(max.X, min.Y, min.Z), new GridPoint(max.X, min.Y, max.Z), kind);
            AddEdge(edges, new GridPoint(max.X, min.Y, max.Z), new GridPoint(min.X, min.Y, max.Z), kind);
            AddEdge(edges, new GridPoint(min.X, min.Y, max.Z), new GridPoint(min.X, min.Y, min.Z), kind);
        }
        else
        {
            AddEdge(edges, new GridPoint(min.X, min.Y, min.Z), new GridPoint(max.X, min.Y, min.Z), kind);
            AddEdge(edges, new GridPoint(max.X, min.Y, min.Z), new GridPoint(max.X, max.Y, min.Z), kind);
            AddEdge(edges, new GridPoint(max.X, max.Y, min.Z), new GridPoint(min.X, max.Y, min.Z), kind);
            AddEdge(edges, new GridPoint(min.X, max.Y, min.Z), new GridPoint(min.X, min.Y, min.Z), kind);
        }
    }

    private static void AddBoxEdges(IDictionary<Edge, ChiselVoxelDiffKind> edges, int x, int y, int z,
        int size, ChiselVoxelDiffKind kind)
    {
        var p000 = new GridPoint(x, y, z); var p100 = new GridPoint(x + size, y, z);
        var p010 = new GridPoint(x, y + size, z); var p110 = new GridPoint(x + size, y + size, z);
        var p001 = new GridPoint(x, y, z + size); var p101 = new GridPoint(x + size, y, z + size);
        var p011 = new GridPoint(x, y + size, z + size); var p111 = new GridPoint(x + size, y + size, z + size);
        AddEdge(edges, p000, p100, kind); AddEdge(edges, p100, p101, kind);
        AddEdge(edges, p101, p001, kind); AddEdge(edges, p001, p000, kind);
        AddEdge(edges, p010, p110, kind); AddEdge(edges, p110, p111, kind);
        AddEdge(edges, p111, p011, kind); AddEdge(edges, p011, p010, kind);
        AddEdge(edges, p000, p010, kind); AddEdge(edges, p100, p110, kind);
        AddEdge(edges, p101, p111, kind); AddEdge(edges, p001, p011, kind);
    }

    private static void AddEdge(IDictionary<Edge, ChiselVoxelDiffKind> edges, GridPoint start, GridPoint end,
        ChiselVoxelDiffKind kind)
    {
        var edge = new Edge(start, end);
        if (!edges.TryGetValue(edge, out var existing) || Priority(kind) > Priority(existing)) edges[edge] = kind;
    }

    private static MeshData BuildMesh(IReadOnlyDictionary<Edge, ChiselVoxelDiffKind> edges, byte? alphaOverride = null)
    {
        var xyz = new float[edges.Count * 6];
        var rgba = new byte[edges.Count * 8];
        var indices = new int[edges.Count * 2];
        var flags = new int[edges.Count * 2];
        var edgeIndex = 0;
        foreach (var pair in edges)
        {
            var vertex = edgeIndex * 2;
            AddPoint(xyz, vertex, pair.Key.Start);
            AddPoint(xyz, vertex + 1, pair.Key.End);
            var color = Color(pair.Value);
            for (var offset = 0; offset < 2; offset++)
            {
                var rgbaIndex = (vertex + offset) * 4;
                rgba[rgbaIndex] = color.R; rgba[rgbaIndex + 1] = color.G;
                rgba[rgbaIndex + 2] = color.B; rgba[rgbaIndex + 3] = alphaOverride ?? Alpha(pair.Value);
                indices[vertex + offset] = vertex + offset;
                flags[vertex + offset] = 1 << 8;
            }
            edgeIndex++;
        }
        return new MeshData(false)
        {
            xyz = xyz,
            Rgba = rgba,
            Indices = indices,
            Flags = flags,
            VerticesCount = flags.Length,
            VerticesMax = flags.Length,
            IndicesCount = indices.Length,
            IndicesMax = indices.Length,
            mode = EnumDrawMode.Lines
        };
    }

    private static void AddPoint(float[] xyz, int vertex, GridPoint point)
    {
        var index = vertex * 3;
        xyz[index] = point.X / 16f;
        xyz[index + 1] = point.Y / 16f;
        xyz[index + 2] = point.Z / 16f;
    }

    private static int Priority(ChiselVoxelDiffKind kind) => kind switch
    {
        ChiselVoxelDiffKind.Material => 3,
        ChiselVoxelDiffKind.Extra => 2,
        _ => 1
    };

    private static (byte R, byte G, byte B) Color(ChiselVoxelDiffKind kind) => kind switch
    {
        ChiselVoxelDiffKind.Extra => (255, 145, 30),
        ChiselVoxelDiffKind.Material => (245, 48, 55),
        _ => (55, 150, 255)
    };

    private static byte Alpha(ChiselVoxelDiffKind kind) => kind switch
    {
        ChiselVoxelDiffKind.Missing => 105,
        _ => 85
    };

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
        capi.Event.BlockChanged -= OnBlockChanged;
        gridMesh?.Dispose();
        outlineMesh?.Dispose();
        hoverMesh?.Dispose();
        gridMesh = null;
        outlineMesh = null;
        hoverMesh = null;
        origin = null;
        current = Array.Empty<ChiselVoxelHighlight>();
        highlightedBlocks.Clear();
        gridBlocks.Clear();
        gridVoxels.Clear();
    }

    private readonly record struct GridPoint(int X, int Y, int Z) : IComparable<GridPoint>
    {
        public int CompareTo(GridPoint other)
        {
            var x = X.CompareTo(other.X);
            if (x != 0) return x;
            var y = Y.CompareTo(other.Y);
            return y != 0 ? y : Z.CompareTo(other.Z);
        }
    }

    private readonly record struct Edge
    {
        public GridPoint Start { get; }
        public GridPoint End { get; }

        public Edge(GridPoint first, GridPoint second)
        {
            if (first.CompareTo(second) <= 0) { Start = first; End = second; }
            else { Start = second; End = first; }
        }
    }

    private readonly record struct BlockKey(int X, int Y, int Z);
}
