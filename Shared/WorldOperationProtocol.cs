using ProtoBuf;

namespace Buildwright.Protocol;

public enum WorldOperationKind
{
    Clear = 1,
    Fill = 2,
    Place = 3,
    ClearExcept = 4
}

[ProtoContract]
public sealed class WorldOperationRequest
{
    [ProtoMember(1)] public string RequestId { get; set; } = string.Empty;
    [ProtoMember(2)] public int ProtocolVersion { get; set; } = 2;
    [ProtoMember(3)] public WorldOperationKind Kind { get; set; }
    [ProtoMember(4)] public int Dimension { get; set; }
    [ProtoMember(5)] public int MinX { get; set; }
    [ProtoMember(6)] public int MinY { get; set; }
    [ProtoMember(7)] public int MinZ { get; set; }
    [ProtoMember(8)] public int MaxX { get; set; }
    [ProtoMember(9)] public int MaxY { get; set; }
    [ProtoMember(10)] public int MaxZ { get; set; }
    [ProtoMember(11)] public string BlockCode { get; set; } = string.Empty;
    [ProtoMember(12)] public string ReplaceCode { get; set; } = string.Empty;
    [ProtoMember(13)] public string SchematicJson { get; set; } = string.Empty;
    [ProtoMember(14)] public int AnchorX { get; set; }
    [ProtoMember(15)] public int AnchorY { get; set; }
    [ProtoMember(16)] public int AnchorZ { get; set; }
    [ProtoMember(17)] public int AnchorOffsetX { get; set; }
    [ProtoMember(18)] public int AnchorOffsetY { get; set; }
    [ProtoMember(19)] public int AnchorOffsetZ { get; set; }
    [ProtoMember(20)] public int RotationQuarterTurns { get; set; }
    [ProtoMember(21)] public int MirrorAxis { get; set; }
    [ProtoMember(22)] public bool OnlyMissing { get; set; }
    [ProtoMember(23)] public bool ClearExtra { get; set; }
    [ProtoMember(24)] public bool HasExcludedBox { get; set; }
    [ProtoMember(25)] public int ExcludeMinX { get; set; }
    [ProtoMember(26)] public int ExcludeMinY { get; set; }
    [ProtoMember(27)] public int ExcludeMinZ { get; set; }
    [ProtoMember(28)] public int ExcludeMaxX { get; set; }
    [ProtoMember(29)] public int ExcludeMaxY { get; set; }
    [ProtoMember(30)] public int ExcludeMaxZ { get; set; }
    [ProtoMember(31)] public bool ClearSourceAfterPlace { get; set; }
    [ProtoMember(32)] public int SourceMinX { get; set; }
    [ProtoMember(33)] public int SourceMinY { get; set; }
    [ProtoMember(34)] public int SourceMinZ { get; set; }
    [ProtoMember(35)] public int SourceMaxX { get; set; }
    [ProtoMember(36)] public int SourceMaxY { get; set; }
    [ProtoMember(37)] public int SourceMaxZ { get; set; }
}

[ProtoContract]
public sealed class WorldOperationResponse
{
    [ProtoMember(1)] public string RequestId { get; set; } = string.Empty;
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public int Placed { get; set; }
    [ProtoMember(4)] public int Cleared { get; set; }
    [ProtoMember(5)] public string Error { get; set; } = string.Empty;
    [ProtoMember(6)] public List<string> UnknownBlocks { get; set; } = new();
}

[ProtoContract]
public sealed class ServerCapabilityRequest
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = 2;
}

[ProtoContract]
public sealed class ServerCapabilityResponse
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = 2;
    [ProtoMember(2)] public int MaxOperationVolume { get; set; }
}

[ProtoContract]
public sealed class ExactSupportBeamPlacementRequest
{
    [ProtoMember(1)] public string RequestId { get; set; } = string.Empty;
    [ProtoMember(2)] public int ProtocolVersion { get; set; } = 2;
    [ProtoMember(3)] public int Dimension { get; set; }
    [ProtoMember(4)] public string SchematicJson { get; set; } = string.Empty;
    [ProtoMember(5)] public int AnchorX { get; set; }
    [ProtoMember(6)] public int AnchorY { get; set; }
    [ProtoMember(7)] public int AnchorZ { get; set; }
    [ProtoMember(8)] public int AnchorOffsetX { get; set; }
    [ProtoMember(9)] public int AnchorOffsetY { get; set; }
    [ProtoMember(10)] public int AnchorOffsetZ { get; set; }
    [ProtoMember(11)] public int RotationQuarterTurns { get; set; }
    [ProtoMember(12)] public int MirrorAxis { get; set; }
    [ProtoMember(13)] public bool OnlyMissing { get; set; }
    [ProtoMember(14)] public bool ClearExtra { get; set; }
    [ProtoMember(15)] public bool ClearSourceAfterPlace { get; set; }
    [ProtoMember(16)] public int SourceMinX { get; set; }
    [ProtoMember(17)] public int SourceMinY { get; set; }
    [ProtoMember(18)] public int SourceMinZ { get; set; }
    [ProtoMember(19)] public int SourceMaxX { get; set; }
    [ProtoMember(20)] public int SourceMaxY { get; set; }
    [ProtoMember(21)] public int SourceMaxZ { get; set; }
}

[ProtoContract]
public sealed class ExactSupportBeamPlacementResponse
{
    [ProtoMember(1)] public string RequestId { get; set; } = string.Empty;
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public int Placed { get; set; }
    [ProtoMember(4)] public int Cleared { get; set; }
    [ProtoMember(5)] public string Error { get; set; } = string.Empty;
    [ProtoMember(6)] public List<string> UnknownBlocks { get; set; } = new();
    [ProtoMember(7)] public int ExactBeamHosts { get; set; }
    [ProtoMember(8)] public int ExactBeams { get; set; }
}
