using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Buildwright.Client;

/// <summary>
/// Draws one selection cuboid with an exact twelve-edge border. The translucent
/// faces are supplied by the game's cube highlight renderer so they participate
/// in OIT correctly.
/// </summary>
internal sealed class BuildwrightSelectionRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly Matrixf model = new();
    private MeshRef? borderMesh;
    private BlockPos? origin;
    private int width;
    private int height;
    private int depth;

    public double RenderOrder => 0.91;
    public int RenderRange => 9999;

    public BuildwrightSelectionRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT, "buildwright-selection-box");
    }

    public void SetBounds(BlockPos min, BlockPos max)
    {
        origin = min.Copy();
        var nextWidth = max.X - min.X + 1;
        var nextHeight = max.Y - min.Y + 1;
        var nextDepth = max.Z - min.Z + 1;
        if (nextWidth == width && nextHeight == height && nextDepth == depth && borderMesh != null)
            return;

        width = nextWidth;
        height = nextHeight;
        depth = nextDepth;
        RebuildMesh();
    }

    public void Clear() => origin = null;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.AfterOIT || origin == null || borderMesh == null) return;
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
            render.LineWidth = 2.4f;
            render.RenderMesh(borderMesh);
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
        borderMesh?.Dispose();
        borderMesh = capi.Render.UploadMesh(BuildBorderMesh(width, height, depth));
    }

    private static MeshData BuildBorderMesh(float width, float height, float depth)
    {
        var builder = new LineMeshBuilder(24, 24, 238, 54, 62, 235);
        var p000 = new Vec3f(0, 0, 0);
        var p100 = new Vec3f(width, 0, 0);
        var p010 = new Vec3f(0, height, 0);
        var p110 = new Vec3f(width, height, 0);
        var p001 = new Vec3f(0, 0, depth);
        var p101 = new Vec3f(width, 0, depth);
        var p011 = new Vec3f(0, height, depth);
        var p111 = new Vec3f(width, height, depth);

        builder.Add(p000, p100); builder.Add(p100, p101); builder.Add(p101, p001); builder.Add(p001, p000);
        builder.Add(p010, p110); builder.Add(p110, p111); builder.Add(p111, p011); builder.Add(p011, p010);
        builder.Add(p000, p010); builder.Add(p100, p110); builder.Add(p101, p111); builder.Add(p001, p011);
        return builder.Build();
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
        borderMesh?.Dispose();
        borderMesh = null;
        origin = null;
    }

    private sealed class LineMeshBuilder
    {
        private readonly List<float> xyz;
        private readonly List<byte> rgba;
        private readonly List<int> indices;
        private readonly List<int> flags;
        private readonly byte red;
        private readonly byte green;
        private readonly byte blue;
        private readonly byte alpha;

        public LineMeshBuilder(int vertexCapacity, int indexCapacity, byte red, byte green, byte blue, byte alpha)
        {
            xyz = new List<float>(vertexCapacity * 3);
            rgba = new List<byte>(vertexCapacity * 4);
            indices = new List<int>(indexCapacity);
            flags = new List<int>(vertexCapacity);
            this.red = red;
            this.green = green;
            this.blue = blue;
            this.alpha = alpha;
        }

        public void Add(Vec3f start, Vec3f end)
        {
            var index = flags.Count;
            AddVertex(start);
            AddVertex(end);
            indices.Add(index);
            indices.Add(index + 1);
        }

        public MeshData Build()
        {
            return new MeshData(false)
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
        }

        private void AddVertex(Vec3f point)
        {
            xyz.Add(point.X); xyz.Add(point.Y); xyz.Add(point.Z);
            rgba.Add(red); rgba.Add(green); rgba.Add(blue); rgba.Add(alpha);
            // Keep the border slightly in front of the translucent faces.
            flags.Add(1 << 8);
        }
    }
}
