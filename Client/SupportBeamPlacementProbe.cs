using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Buildwright.Client;

internal sealed class SupportBeamPlacementProbe
{
    private static readonly MethodInfo? GetWorkspaceMethod = typeof(ModSystemSupportBeamPlacer).GetMethod(
        "getWorkSpace", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
    private static readonly MethodInfo? GetEndOffsetMethod = typeof(ModSystemSupportBeamPlacer).GetMethod(
        "getEndOffset", BindingFlags.Instance | BindingFlags.NonPublic, null,
        new[] { typeof(IPlayer), typeof(BeamPlacerWorkSpace) }, null);

    private readonly ICoreClientAPI capi;
    private ModSystemSupportBeamPlacer? placer;
    private bool warned;

    public SupportBeamPlacementProbe(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool TryRead(out SupportBeamPlacementState state)
    {
        state = default;
        var player = capi.World.Player;
        if (player == null || GetWorkspaceMethod == null || GetEndOffsetMethod == null) return false;

        try
        {
            placer ??= capi.ModLoader.GetModSystem<ModSystemSupportBeamPlacer>();
            if (placer == null) return false;
            var workspace = GetWorkspaceMethod.Invoke(placer, new object[] { player.PlayerUID }) as BeamPlacerWorkSpace;
            if (workspace == null) return false;

            var gridSize = Math.Max(1, workspace.GridSize);
            if (!workspace.nowBuilding || workspace.startPos == null || workspace.startOffset == null)
            {
                state = new SupportBeamPlacementState(false, null, null, gridSize);
                return true;
            }

            var endOffset = GetEndOffsetMethod.Invoke(placer, new object[] { player, workspace }) as Vec3f;
            if (endOffset == null) return false;
            state = new SupportBeamPlacementState(true,
                workspace.startPos.ToVec3d().Add(workspace.startOffset),
                workspace.startPos.ToVec3d().Add(endOffset), gridSize);
            return true;
        }
        catch (Exception exception)
        {
            if (!warned)
            {
                warned = true;
                var cause = exception is TargetInvocationException { InnerException: not null }
                    ? exception.InnerException
                    : exception;
                capi.Logger.Warning("[buildwright] Support beam placement state unavailable: {0}", cause.Message);
            }
            return false;
        }
    }
}

internal readonly record struct SupportBeamPlacementState(bool IsBuilding, Vec3d? StartWorld,
    Vec3d? EndWorld, int GridSize);
