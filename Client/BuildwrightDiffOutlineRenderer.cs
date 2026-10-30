using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Buildwright.Client;

internal sealed class BuildwrightDiffOutlineRenderer : IRenderer, IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly Matrixf model = new();
    private MeshRef? mesh;
    private BlockPos? origin;

    public double RenderOrder => 0.92;
    public int RenderRange => 9999;

    public BuildwrightDiffOutlineRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT, "buildwright-diff-outlines");
    }

    public void SetBlocks(IEnumerable<BlockPos> positions)
    {
        var blocks = positions.DistinctBy(pos => (pos.X, pos.Y, pos.Z, pos.dimension)).ToList();
        mesh?.Dispose();
        mesh = null;
        origin = null;
        if (blocks.Count == 0) return;

        origin = new BlockPos(blocks.Min(pos => pos.X), blocks.Min(pos => pos.Y), blocks.Min(pos => pos.Z),
            blocks[0].dimension);
        var builder = new LineMeshBuilder();
        foreach (var pos in blocks) AddCube(builder, pos.X - origin.X, pos.Y - origin.Y, pos.Z - origin.Z);
        var data = builder.Build();
        mesh = capi.Render.UploadMesh(data);
        data.Dispose();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.AfterOIT || mesh?.Initialized != true || origin == null) return;
        var entity = capi.World.Player?.Entity;
        if (entity == null) return;
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
            render.LineWidth = 2.5f;
            render.RenderMesh(mesh);
        }
        finally
        {
            shader.Stop();
            render.LineWidth = 1.6f;
            render.GLDepthMask(true);
        }
    }

    private static void AddCube(LineMeshBuilder builder, float x, float y, float z)
    {
        const float inset = -0.002f;
        var x0 = x + inset; var y0 = y + inset; var z0 = z + inset;
        var x1 = x + 1 - inset; var y1 = y + 1 - inset; var z1 = z + 1 - inset;
        var p000 = new Vec3f(x0, y0, z0); var p100 = new Vec3f(x1, y0, z0);
        var p010 = new Vec3f(x0, y1, z0); var p110 = new Vec3f(x1, y1, z0);
        var p001 = new Vec3f(x0, y0, z1); var p101 = new Vec3f(x1, y0, z1);
        var p011 = new Vec3f(x0, y1, z1); var p111 = new Vec3f(x1, y1, z1);
        builder.Add(p000, p100); builder.Add(p100, p101); builder.Add(p101, p001); builder.Add(p001, p000);
        builder.Add(p010, p110); builder.Add(p110, p111); builder.Add(p111, p011); builder.Add(p011, p010);
        builder.Add(p000, p010); builder.Add(p100, p110); builder.Add(p101, p111); builder.Add(p001, p011);
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
        mesh?.Dispose();
        mesh = null;
        origin = null;
    }

    private sealed class LineMeshBuilder
    {
        private readonly List<float> xyz = new();
        private readonly List<byte> rgba = new();
        private readonly List<int> indices = new();
        private readonly List<int> flags = new();

        public void Add(Vec3f start, Vec3f end)
        {
            var index = flags.Count;
            AddVertex(start); AddVertex(end);
            indices.Add(index); indices.Add(index + 1);
        }

        public MeshData Build() => new(false)
        {
            xyz = xyz.ToArray(), Rgba = rgba.ToArray(), Indices = indices.ToArray(), Flags = flags.ToArray(),
            VerticesCount = flags.Count, VerticesMax = flags.Count,
            IndicesCount = indices.Count, IndicesMax = indices.Count, mode = EnumDrawMode.Lines
        };

        private void AddVertex(Vec3f point)
        {
            xyz.Add(point.X); xyz.Add(point.Y); xyz.Add(point.Z);
            rgba.Add(235); rgba.Add(45); rgba.Add(45); rgba.Add(235);
            flags.Add(1 << 8);
        }
    }
}
