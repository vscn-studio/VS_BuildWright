using Buildwright.Core;
using Buildwright.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Buildwright.Client;

internal sealed class WorldOperationService
{
    private const string ChannelName = "buildwright:worldops";
    private const int ProtocolVersion = 2;

    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private readonly Dictionary<string, Action<bool>> completionCallbacks = new();
    private int serverProtocolVersion;
    private int maxOperationVolume;

    public WorldOperationService(ICoreClientAPI capi)
    {
        this.capi = capi;
        channel = capi.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<WorldOperationRequest>()
            .RegisterMessageType<WorldOperationResponse>()
            .RegisterMessageType<ServerCapabilityRequest>()
            .RegisterMessageType<ServerCapabilityResponse>()
            .RegisterMessageType<ExactSupportBeamPlacementRequest>()
            .RegisterMessageType<ExactSupportBeamPlacementResponse>()
            .SetMessageHandler<WorldOperationResponse>(OnOperationResponse)
            .SetMessageHandler<ServerCapabilityResponse>(OnCapabilityResponse)
            .SetMessageHandler<ExactSupportBeamPlacementResponse>(OnExactPlacementResponse);
        capi.Event.LevelFinalize += RequestCapabilities;
    }

    public void RequestCapabilities()
    {
        completionCallbacks.Clear();
        serverProtocolVersion = 0;
        maxOperationVolume = 0;
        if (channel.Connected)
            channel.SendPacket(new ServerCapabilityRequest { ProtocolVersion = ProtocolVersion });
    }

    public bool CanModifyWorld(out string reason)
    {
        if (capi.World.Player?.WorldData?.CurrentGameMode != EnumGameMode.Creative)
        {
            reason = BuildwrightLang.Get("chat.server-creative-required");
            return false;
        }
        if (!channel.Connected)
        {
            reason = BuildwrightLang.Get("chat.server-mod-required");
            return false;
        }
        if (serverProtocolVersion != 0 && serverProtocolVersion != ProtocolVersion)
        {
            reason = BuildwrightLang.Get("chat.server-version-mismatch", ProtocolVersion, serverProtocolVersion);
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public OperationResult Place(BlueprintDocument document, BlockPos anchor, int rotation, MirrorAxis mirror,
        bool onlyMissing, bool clearExtra) => SendPlace(document, anchor, rotation, mirror, onlyMissing, clearExtra, null, null);

    public OperationResult PlaceAndClearSource(BlueprintDocument document, BlockPos anchor, int rotation, MirrorAxis mirror,
        bool onlyMissing, bool clearExtra, BlockPos sourceMin, BlockPos sourceMax, Action<bool>? completed = null) =>
        SendPlace(document, anchor, rotation, mirror, onlyMissing, clearExtra, sourceMin, sourceMax, completed);

    public OperationResult ClearRegion(BlockPos min, BlockPos max)
    {
        if (!CanModifyWorld(out var reason)) return OperationResult.Failed(reason);
        return Send(new WorldOperationRequest
        {
            Kind = WorldOperationKind.Clear,
            Dimension = min.dimension,
            MinX = min.X, MinY = min.Y, MinZ = min.Z,
            MaxX = max.X, MaxY = max.Y, MaxZ = max.Z
        });
    }

    public OperationResult FillRegion(BlockPos min, BlockPos max, AssetLocation code, AssetLocation? replaceOnly = null)
    {
        if (!CanModifyWorld(out var reason)) return OperationResult.Failed(reason);
        return Send(new WorldOperationRequest
        {
            Kind = WorldOperationKind.Fill,
            Dimension = min.dimension,
            MinX = min.X, MinY = min.Y, MinZ = min.Z,
            MaxX = max.X, MaxY = max.Y, MaxZ = max.Z,
            BlockCode = code.ToString(),
            ReplaceCode = replaceOnly?.ToString() ?? string.Empty
        });
    }

    public OperationResult ClearRegionExcept(BlockPos min, BlockPos max, IReadOnlySet<(int X, int Y, int Z)> excluded)
    {
        if (!CanModifyWorld(out var reason)) return OperationResult.Failed(reason);
        var request = new WorldOperationRequest
        {
            Kind = WorldOperationKind.ClearExcept,
            Dimension = min.dimension,
            MinX = min.X, MinY = min.Y, MinZ = min.Z,
            MaxX = max.X, MaxY = max.Y, MaxZ = max.Z
        };
        if (excluded.Count > 0)
        {
            request.HasExcludedBox = true;
            request.ExcludeMinX = excluded.Min(point => point.X);
            request.ExcludeMinY = excluded.Min(point => point.Y);
            request.ExcludeMinZ = excluded.Min(point => point.Z);
            request.ExcludeMaxX = excluded.Max(point => point.X);
            request.ExcludeMaxY = excluded.Max(point => point.Y);
            request.ExcludeMaxZ = excluded.Max(point => point.Z);
        }
        return Send(request);
    }

    public static HashSet<(int X, int Y, int Z)> GetTargetVolume(BlueprintDocument document, BlockPos anchor, int rotation, MirrorAxis mirror)
    {
        var volume = new HashSet<(int X, int Y, int Z)>();
        foreach (var local in BlueprintTransforms.EnumerateBox(document.Metadata.Dimensions))
        {
            var world = BlueprintTransforms.CellToWorld(local, document.Metadata.AnchorOffset,
                new Int3(anchor.X, anchor.Y, anchor.Z), rotation, mirror);
            volume.Add((world.X, world.Y, world.Z));
        }
        return volume;
    }

    private OperationResult SendPlace(BlueprintDocument document, BlockPos anchor, int rotation, MirrorAxis mirror,
        bool onlyMissing, bool clearExtra, BlockPos? sourceMin, BlockPos? sourceMax, Action<bool>? completed = null)
    {
        if (!CanModifyWorld(out var reason)) return OperationResult.Failed(reason);
        if (ContainsSupportBeamData(document))
            return SendExactSupportBeamPlacement(document, anchor, rotation, mirror, onlyMissing, clearExtra,
                sourceMin, sourceMax, completed);
        var request = new WorldOperationRequest
        {
            Kind = WorldOperationKind.Place,
            Dimension = anchor.dimension,
            SchematicJson = document.Schematic.ToString(Formatting.None),
            AnchorX = anchor.X, AnchorY = anchor.Y, AnchorZ = anchor.Z,
            AnchorOffsetX = document.Metadata.AnchorOffset.X,
            AnchorOffsetY = document.Metadata.AnchorOffset.Y,
            AnchorOffsetZ = document.Metadata.AnchorOffset.Z,
            RotationQuarterTurns = rotation,
            MirrorAxis = (int)mirror,
            OnlyMissing = onlyMissing,
            ClearExtra = clearExtra
        };
        if (sourceMin != null && sourceMax != null)
        {
            request.ClearSourceAfterPlace = true;
            request.SourceMinX = sourceMin.X; request.SourceMinY = sourceMin.Y; request.SourceMinZ = sourceMin.Z;
            request.SourceMaxX = sourceMax.X; request.SourceMaxY = sourceMax.Y; request.SourceMaxZ = sourceMax.Z;
        }
        return Send(request, completed);
    }

    private OperationResult SendExactSupportBeamPlacement(BlueprintDocument document, BlockPos anchor,
        int rotation, MirrorAxis mirror, bool onlyMissing, bool clearExtra, BlockPos? sourceMin,
        BlockPos? sourceMax, Action<bool>? completed)
    {
        var request = new ExactSupportBeamPlacementRequest
        {
            SchematicJson = document.Schematic.ToString(Formatting.None),
            Dimension = anchor.dimension,
            AnchorX = anchor.X, AnchorY = anchor.Y, AnchorZ = anchor.Z,
            AnchorOffsetX = document.Metadata.AnchorOffset.X,
            AnchorOffsetY = document.Metadata.AnchorOffset.Y,
            AnchorOffsetZ = document.Metadata.AnchorOffset.Z,
            RotationQuarterTurns = rotation,
            MirrorAxis = (int)mirror,
            OnlyMissing = onlyMissing,
            ClearExtra = clearExtra
        };
        if (sourceMin != null && sourceMax != null)
        {
            request.ClearSourceAfterPlace = true;
            request.SourceMinX = sourceMin.X; request.SourceMinY = sourceMin.Y; request.SourceMinZ = sourceMin.Z;
            request.SourceMaxX = sourceMax.X; request.SourceMaxY = sourceMax.Y; request.SourceMaxZ = sourceMax.Z;
        }
        return SendExact(request, completed);
    }

    private OperationResult Send(WorldOperationRequest request, Action<bool>? completed = null)
    {
        var requestId = string.Empty;
        try
        {
            if (maxOperationVolume > 0 && TryVolume(request.MinX, request.MinY, request.MinZ,
                    request.MaxX, request.MaxY, request.MaxZ, out var volume) && volume > maxOperationVolume)
                return OperationResult.Failed(BuildwrightLang.Get("chat.server-volume-limit", volume, maxOperationVolume));
            requestId = Guid.NewGuid().ToString("N");
            request.RequestId = requestId;
            request.ProtocolVersion = ProtocolVersion;
            if (completed != null) completionCallbacks[requestId] = completed;
            channel.SendPacket(request);
            return OperationResult.QueuedRequest();
        }
        catch (Exception exception)
        {
            if (requestId.Length > 0) completionCallbacks.Remove(requestId);
            return OperationResult.Failed(BuildwrightLang.Get("chat.server-request-failed", exception.Message));
        }
    }

    private void OnOperationResponse(WorldOperationResponse response)
    {
        capi.Event.EnqueueMainThreadTask(() =>
        {
            completionCallbacks.Remove(response.RequestId, out var completed);
            if (!response.Success)
            {
                capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.server-operation-failed", response.Error));
                completed?.Invoke(false);
                return;
            }
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.server-operation-result", response.Placed, response.Cleared));
            if (response.UnknownBlocks.Count > 0)
                capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.unknown-blocks",
                    string.Join(", ", response.UnknownBlocks.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))));
            completed?.Invoke(true);
        }, "buildwright-world-operation-response");
    }

    private void OnCapabilityResponse(ServerCapabilityResponse response)
    {
        capi.Event.EnqueueMainThreadTask(() =>
        {
            serverProtocolVersion = response.ProtocolVersion;
            maxOperationVolume = response.MaxOperationVolume;
        }, "buildwright-server-capabilities");
    }

    private void OnExactPlacementResponse(ExactSupportBeamPlacementResponse response)
    {
        capi.Event.EnqueueMainThreadTask(() =>
        {
            completionCallbacks.Remove(response.RequestId, out var completed);
            if (!response.Success)
            {
                capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.server-operation-failed", response.Error));
                completed?.Invoke(false);
                return;
            }
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.exact-beam-result",
                response.ExactBeamHosts, response.ExactBeams, response.Placed, response.Cleared));
            if (response.UnknownBlocks.Count > 0)
                capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.unknown-blocks",
                    string.Join(", ", response.UnknownBlocks.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))));
            completed?.Invoke(true);
        }, "buildwright-exact-support-beam-response");
    }

    private OperationResult SendExact(ExactSupportBeamPlacementRequest request, Action<bool>? completed)
    {
        var requestId = string.Empty;
        try
        {
            requestId = Guid.NewGuid().ToString("N");
            request.RequestId = requestId;
            request.ProtocolVersion = ProtocolVersion;
            if (completed != null) completionCallbacks[requestId] = completed;
            channel.SendPacket(request);
            return OperationResult.QueuedRequest();
        }
        catch (Exception exception)
        {
            if (requestId.Length > 0) completionCallbacks.Remove(requestId);
            return OperationResult.Failed(BuildwrightLang.Get("chat.server-request-failed", exception.Message));
        }
    }

    private static bool ContainsSupportBeamData(BlueprintDocument document)
    {
        foreach (var record in document.BlockEntities)
        {
            var tree = BlockEntityCapture.Decode(record.TreeAscii85);
            var bytes = tree?.GetBytes("beams");
            if (bytes is { Length: > 0 }) return true;
        }
        if (document.Schematic["BlockEntities"] is JObject entities)
        {
            foreach (var property in entities.Properties())
            {
                if (property.Value.Value<string>() is not { } payload) continue;
                var tree = BlockEntityCapture.Decode(payload);
                if (tree?.GetBytes("beams") is { Length: > 0 }) return true;
            }
        }
        return false;
    }

    private static bool TryVolume(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, out long volume)
    {
        try
        {
            volume = checked((Math.Abs((long)maxX - minX) + 1) * (Math.Abs((long)maxY - minY) + 1) *
                             (Math.Abs((long)maxZ - minZ) + 1));
            return true;
        }
        catch (OverflowException)
        {
            volume = long.MaxValue;
            return false;
        }
    }
}

internal sealed class OperationResult
{
    public int Placed { get; set; }
    public int Cleared { get; set; }
    public List<string> UnknownBlocks { get; } = new();
    public bool Success { get; init; }
    public bool Queued { get; set; }
    public string Error { get; init; } = string.Empty;
    public static OperationResult Failed(string error) => new() { Success = false, Error = error };
    public static OperationResult QueuedRequest() => new() { Success = true, Queued = true };
    public OperationResult() { Success = true; }
}
