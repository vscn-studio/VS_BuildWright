using Buildwright.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Buildwright.Client;

internal sealed class BuildwrightGhostRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly BuildwrightGhostMesh mesh;
    private readonly BlueprintTerrainProjection? terrainProjection;
    private readonly BuildwrightSupportBeamGuideMesh? supportBeamGuides;
    private readonly SupportBeamPlacementProbe supportBeamPlacement;
    private readonly Int3 anchorOffset;
    private readonly BlockFacing? anchorFace;
    private readonly Matrixf model = new();
    private readonly Matrixf guideModelView = new();
    private bool floating;
    private int visibleLayers;
    private MatchingMode matchingMode;

    public BlockPos Origin { get; private set; }
    public int RotationQuarterTurns { get; private set; }
    public MirrorAxis Mirror { get; private set; }
    public bool IsFloating => floating;
    public int RenderRange { get; }
    public int VisibleLayers => visibleLayers;
    public double RenderOrder => 0.5;

    public BuildwrightGhostRenderer(ICoreClientAPI capi, BuildwrightGhostMesh mesh, BlockPos origin,
        Int3 anchorOffset, BlockFacing? anchorFace, int renderRange,
        BlueprintTerrainProjection? terrainProjection = null,
        BuildwrightSupportBeamGuideMesh? supportBeamGuides = null,
        MatchingMode matchingMode = MatchingMode.Loose)
    {
        this.capi = capi;
        this.mesh = mesh;
        this.terrainProjection = terrainProjection;
        this.supportBeamGuides = supportBeamGuides;
        supportBeamPlacement = new SupportBeamPlacementProbe(capi);
        this.matchingMode = matchingMode;
        this.Origin = origin.Copy();
        this.anchorOffset = anchorOffset;
        this.anchorFace = anchorFace;
        this.RenderRange = renderRange;
        floating = true;
        visibleLayers = mesh.LayerCount;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "buildwright-ghost");
    }

    public void Place() => floating = false;
    public void Unplace() => floating = true;
    public void Move(int dx, int dy, int dz)
    {
        Origin = new BlockPos(Origin.X + dx, Origin.Y + dy, Origin.Z + dz, Origin.dimension);
    }

    public bool SnapToTarget()
    {
        var selection = capi.World.Player?.CurrentBlockSelection;
        if (selection?.Position == null || selection.Face == null) return false;

        var normal = selection.Face.Normali;
        Origin = new BlockPos(selection.Position.X + normal.X, selection.Position.Y + normal.Y,
            selection.Position.Z + normal.Z, selection.Position.dimension);
        return true;
    }

    public void MoveRelative(ICoreClientAPI api, int forward, int right, int up)
    {
        var facing = BlockFacing.HorizontalFromYaw(api.World.Player?.Entity?.Pos.Yaw ?? 0f);
        var forwardNormal = facing.Normali;
        var rightNormal = facing == BlockFacing.NORTH ? BlockFacing.EAST.Normali
            : facing == BlockFacing.EAST ? BlockFacing.SOUTH.Normali
            : facing == BlockFacing.SOUTH ? BlockFacing.WEST.Normali
            : BlockFacing.NORTH.Normali;
        Move(forwardNormal.X * forward + rightNormal.X * right,
            up,
            forwardNormal.Z * forward + rightNormal.Z * right);
    }
    public void Rotate(int quarterTurns = 1) => RotationQuarterTurns = ((RotationQuarterTurns + quarterTurns) % 4 + 4) % 4;
    public void SetRotation(int quarterTurns) => RotationQuarterTurns = ((quarterTurns % 4) + 4) % 4;
    public void CycleMirror() => Mirror = Mirror switch { MirrorAxis.None => MirrorAxis.X, MirrorAxis.X => MirrorAxis.Y, MirrorAxis.Y => MirrorAxis.Z, _ => MirrorAxis.None };
    public void SetMirror(MirrorAxis axis) => Mirror = axis;
    public void SetMatchingMode(MatchingMode mode) => matchingMode = mode;
    public void DecreaseLayer() => visibleLayers = Math.Max(0, visibleLayers - 1);
    public void IncreaseLayer() => visibleLayers = Math.Min(mesh.LayerCount, visibleLayers + 1);

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Opaque) return;
        if (floating) UpdateFloatingPlacement();
        terrainProjection?.Update(Origin, anchorOffset, RotationQuarterTurns, Mirror, visibleLayers);
        if ((!mesh.HasMesh && supportBeamGuides?.HasMesh != true) || visibleLayers <= 0) return;
        var player = capi.World.Player;
        if (player?.Entity == null) return;
        var cam = player.Entity.CameraPos;
        var rpi = capi.Render;
        var mx = Mirror == MirrorAxis.X ? -1f : 1f;
        var my = Mirror == MirrorAxis.Y ? -1f : 1f;
        var mz = Mirror == MirrorAxis.Z ? -1f : 1f;
        if (mesh.HasMesh)
        {
            var dx = cam.X - Origin.X;
            var dy = cam.Y - Origin.Y;
            var dz = cam.Z - Origin.Z;
            var farView = dx * dx + dy * dy + dz * dz > 48 * 48;
            // Keep every face nearby for inspection. At distance, opaque ghost
            // geometry uses back-face culling to avoid shading/rendering hidden
            // sides; mirrored meshes retain the no-cull path to preserve winding.
            var useCull = farView && Mirror == MirrorAxis.None;
            if (useCull) rpi.GlEnableCullFace();
            else rpi.GlDisableCullFace();
            rpi.GlToggleBlend(true);
            var shader = rpi.PreparedStandardShader(Origin.X, Origin.Y, Origin.Z);
            shader.ViewMatrix = rpi.CameraMatrixOriginf;
            shader.ProjectionMatrix = rpi.CurrentProjectionMatrix;
            shader.ModelMatrix = model.Identity()
                .Translate(Origin.X - cam.X, Origin.Y - cam.Y, Origin.Z - cam.Z)
                .Translate(0.5f, 0f, 0.5f).RotateY(RotationQuarterTurns * MathF.PI / 2f).Translate(-0.5f, 0f, -0.5f)
                .Translate(0.5f, 0.5f, 0.5f).Scale(mx, my, mz).Translate(-0.5f, -0.5f, -0.5f)
                .Translate(-anchorOffset.X, -anchorOffset.Y, -anchorOffset.Z).Values;
            try
            {
                for (var layer = 0; layer < visibleLayers; layer++)
                    if (mesh.GetLayer(layer) is { Initialized: true } layerMesh)
                        rpi.RenderMultiTextureMesh(layerMesh, "tex");
            }
            finally
            {
                shader.Stop();
                rpi.GlEnableCullFace();
            }
        }

        if (matchingMode == MatchingMode.Strict && supportBeamGuides?.HasMesh == true && IsHoldingSupportBeam())
        {
            if (supportBeamPlacement.TryRead(out var placement) && placement.IsBuilding
                && placement.StartWorld != null && placement.EndWorld != null)
            {
                supportBeamGuides.UpdateEndPointer(Origin, anchorOffset, RotationQuarterTurns, Mirror,
                    visibleLayers, placement.StartWorld, placement.EndWorld);
            }
            else
            {
                var view = Vintagestory.API.Common.Entities.EntityPos.GetViewVector(capi.Input.MousePitch,
                    capi.Input.MouseYaw);
                var selection = player.CurrentBlockSelection;
                var placementGridSize = player.Entity.Controls.CtrlKey ? 16 : 4;
                Vec3d? cursorHit = null;
                if (selection?.Position != null && selection.HitPosition != null && selection.Face != null)
                {
                    var selectedEntity = capi.World.BlockAccessor.GetBlockEntity(selection.Position);
                    var selectedBlock = capi.World.BlockAccessor.GetBlock(selection.Position);
                    var adjacentPos = selection.Position.AddCopy(selection.Face);
                    var adjacentEntity = capi.World.BlockAccessor.GetBlockEntity(adjacentPos);
                    var adjacentBlock = capi.World.BlockAccessor.GetBlock(adjacentPos);
                    if (BuildwrightSupportBeamGuideMesh.TryResolveGameStartPoint(selection.Position,
                            selection.HitPosition, selection.Face,
                            selectedEntity?.GetBehavior<BEBehaviorSupportBeam>() != null, selectedBlock?.Replaceable ?? 0,
                            adjacentEntity?.GetBehavior<BEBehaviorSupportBeam>() != null, adjacentBlock?.Replaceable ?? 0,
                            placementGridSize, out var gameStartPoint))
                        cursorHit = gameStartPoint;
                }
                supportBeamGuides.UpdateStartPointer(cam, view, Origin, anchorOffset,
                    RotationQuarterTurns, Mirror, visibleLayers, cursorHit, placementGridSize);
            }
            var shader = rpi.GetEngineShader(EnumShaderProgram.Wireframe);
            guideModelView.Identity().Set(rpi.CameraMatrixOriginf)
                .Translate(Origin.X - cam.X, Origin.Y - cam.Y, Origin.Z - cam.Z)
                .Translate(0.5f, 0f, 0.5f).RotateY(RotationQuarterTurns * MathF.PI / 2f).Translate(-0.5f, 0f, -0.5f)
                .Translate(0.5f, 0.5f, 0.5f).Scale(mx, my, mz).Translate(-0.5f, -0.5f, -0.5f)
                .Translate(-anchorOffset.X, -anchorOffset.Y, -anchorOffset.Z);
            shader.Use();
            rpi.GLEnableDepthTest();
            rpi.GLDepthMask(false);
            rpi.GlDisableCullFace();
            rpi.GlToggleBlend(true);
            shader.Uniform("origin", 0f, 0f, 0f);
            shader.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);
            shader.UniformMatrix("modelViewMatrix", guideModelView.Values);
            try
            {
                shader.Uniform("colorIn", ColorUtil.WhiteArgbVec);
                rpi.LineWidth = 1.4f;
                RenderBaseGuides();
                if (supportBeamGuides.HoverMesh is { Initialized: true } hover)
                {
                    rpi.GLDisableDepthTest();
                    shader.Uniform("colorIn", ColorUtil.WhiteArgbVec);
                    rpi.LineWidth = 2.2f;
                    rpi.RenderMesh(hover);
                }
            }
            finally
            {
                shader.Stop();
                rpi.LineWidth = 1.6f;
                rpi.GLDepthMask(true);
                rpi.GLEnableDepthTest();
                rpi.GlEnableCullFace();
            }

            void RenderBaseGuides()
            {
                for (var layer = 0; layer < visibleLayers; layer++)
                    if (supportBeamGuides.GetLayer(layer) is { Initialized: true } guideLayer)
                        rpi.RenderMesh(guideLayer);
            }
        }
    }

    private bool IsHoldingSupportBeam() =>
        capi.World.Player?.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible is BlockSupportBeam;

    private void UpdateFloatingPlacement()
    {
        if (!SnapToTarget()) return;
        if (anchorFace == null || !anchorFace.IsHorizontal || capi.World.Player?.Entity?.Pos == null) return;
        var playerFacing = BlockFacing.HorizontalFromYaw(capi.World.Player.Entity.Pos.Yaw).Opposite;
        RotationQuarterTurns = ((playerFacing.HorizontalAngleIndex - anchorFace.HorizontalAngleIndex) % 4 + 4) % 4;
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        terrainProjection?.Dispose();
        supportBeamGuides?.Dispose();
        mesh.Dispose();
    }
}
