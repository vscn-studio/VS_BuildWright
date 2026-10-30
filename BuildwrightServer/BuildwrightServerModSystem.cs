using Buildwright.Protocol;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Buildwright.Server;

public sealed class BuildwrightServerModSystem : ModSystem
{
    private const string ChannelName = "buildwright:worldops";
    private const int ProtocolVersion = 2;
    private const int MaxOperationVolume = 2_000_000;
    private const int MaxSchematicJsonLength = 64 * 1024 * 1024;
    private const int MaxExactBeamHosts = 16_384;
    private const int MaxExactBeams = 65_536;

    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? channel;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        channel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<WorldOperationRequest>()
            .RegisterMessageType<WorldOperationResponse>()
            .RegisterMessageType<ServerCapabilityRequest>()
            .RegisterMessageType<ServerCapabilityResponse>()
            .RegisterMessageType<ExactSupportBeamPlacementRequest>()
            .RegisterMessageType<ExactSupportBeamPlacementResponse>()
            .SetMessageHandler<WorldOperationRequest>(OnWorldOperation)
            .SetMessageHandler<ServerCapabilityRequest>(OnCapabilityRequest)
            .SetMessageHandler<ExactSupportBeamPlacementRequest>(OnExactSupportBeamPlacement);
        api.Logger.Notification("[buildwrightserver] Buildwright Server 1.0.3 ready");
    }

    private void OnCapabilityRequest(IServerPlayer player, ServerCapabilityRequest request)
    {
        channel?.SendPacket(new ServerCapabilityResponse
        {
            ProtocolVersion = ProtocolVersion,
            MaxOperationVolume = MaxOperationVolume
        }, player);
    }

    private void OnWorldOperation(IServerPlayer player, WorldOperationRequest request)
    {
        if (sapi == null) return;
        sapi.Event.EnqueueMainThreadTask(() => ExecuteAndRespond(player, request), "buildwright-server-world-operation");
    }

    private void OnExactSupportBeamPlacement(IServerPlayer player, ExactSupportBeamPlacementRequest request)
    {
        if (sapi == null) return;
        sapi.Event.EnqueueMainThreadTask(() => ExecuteExactSupportBeamPlacement(player, request),
            "buildwright-exact-support-beam-placement");
    }

    private void ExecuteExactSupportBeamPlacement(IServerPlayer player, ExactSupportBeamPlacementRequest request)
    {
        var response = new ExactSupportBeamPlacementResponse { RequestId = request.RequestId };
        try
        {
            ValidateExactRequest(player, request);
            var worldRequest = new WorldOperationRequest
            {
                RequestId = request.RequestId,
                ProtocolVersion = request.ProtocolVersion,
                Kind = WorldOperationKind.Place,
                Dimension = request.Dimension,
                SchematicJson = request.SchematicJson,
                AnchorX = request.AnchorX,
                AnchorY = request.AnchorY,
                AnchorZ = request.AnchorZ,
                AnchorOffsetX = request.AnchorOffsetX,
                AnchorOffsetY = request.AnchorOffsetY,
                AnchorOffsetZ = request.AnchorOffsetZ,
                RotationQuarterTurns = request.RotationQuarterTurns,
                MirrorAxis = request.MirrorAxis,
                OnlyMissing = request.OnlyMissing,
                ClearExtra = request.ClearExtra,
                ClearSourceAfterPlace = request.ClearSourceAfterPlace,
                SourceMinX = request.SourceMinX,
                SourceMinY = request.SourceMinY,
                SourceMinZ = request.SourceMinZ,
                SourceMaxX = request.SourceMaxX,
                SourceMaxY = request.SourceMaxY,
                SourceMaxZ = request.SourceMaxZ
            };
            var result = Place(worldRequest, exactSupportBeams: true);
            response.Success = true;
            response.Placed = result.Placed;
            response.Cleared = result.Cleared;
            response.UnknownBlocks = result.UnknownBlocks.Take(128).ToList();
            response.ExactBeamHosts = result.ExactBeamHosts;
            response.ExactBeams = result.ExactBeams;
        }
        catch (Exception exception)
        {
            response.Success = false;
            response.Error = exception.Message;
            sapi?.Logger.Warning("[buildwrightserver] Rejected exact support beam placement {0} from {1}: {2}",
                request.RequestId, player.PlayerName, exception.Message);
        }
        channel?.SendPacket(response, player);
    }

    private void ExecuteAndRespond(IServerPlayer player, WorldOperationRequest request)
    {
        var response = new WorldOperationResponse { RequestId = request.RequestId };
        try
        {
            ValidateRequest(player, request);
            var result = request.Kind switch
            {
                WorldOperationKind.Clear => Clear(request, excluded: null),
                WorldOperationKind.Fill => Fill(request),
                WorldOperationKind.Place => Place(request),
                WorldOperationKind.ClearExcept => Clear(request, request.HasExcludedBox ? NormalizeExcluded(request) : null),
                _ => throw new InvalidDataException("Unknown world operation.")
            };
            response.Success = true;
            response.Placed = result.Placed;
            response.Cleared = result.Cleared;
            response.UnknownBlocks = result.UnknownBlocks.Take(128).ToList();
        }
        catch (Exception exception)
        {
            response.Success = false;
            response.Error = exception.Message;
            sapi?.Logger.Warning("[buildwrightserver] Rejected operation {0} from {1}: {2}",
                request.RequestId, player.PlayerName, exception.Message);
        }
        channel?.SendPacket(response, player);
    }

    private void ValidateRequest(IServerPlayer player, WorldOperationRequest request)
    {
        if (request.ProtocolVersion != ProtocolVersion)
            throw new InvalidDataException($"Protocol mismatch (client {request.ProtocolVersion}, server {ProtocolVersion}).");
        if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
            throw new InvalidOperationException("Creative mode is required.");
        if (!player.HasPrivilege(Privilege.buildblocks))
            throw new InvalidOperationException("The build privilege is required.");
        if (player.Entity.Pos.Dimension != request.Dimension)
            throw new InvalidOperationException("The operation dimension does not match the player's dimension.");
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 64)
            throw new InvalidDataException("Invalid request identifier.");

        if (request.Kind is WorldOperationKind.Clear or WorldOperationKind.Fill or WorldOperationKind.ClearExcept)
            ValidateVolume(NormalizeBounds(request), MaxOperationVolume, "Operation");
        if (request.Kind == WorldOperationKind.Place && request.SchematicJson.Length > MaxSchematicJsonLength)
            throw new InvalidDataException("The schematic payload is too large.");
    }

    private void ValidateExactRequest(IServerPlayer player, ExactSupportBeamPlacementRequest request)
    {
        if (request.ProtocolVersion != ProtocolVersion)
            throw new InvalidDataException($"Protocol mismatch (client {request.ProtocolVersion}, server {ProtocolVersion}).");
        if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
            throw new InvalidOperationException("Creative mode is required.");
        if (!player.HasPrivilege(Privilege.buildblocks))
            throw new InvalidOperationException("The build privilege is required.");
        if (player.Entity.Pos.Dimension != request.Dimension)
            throw new InvalidOperationException("The operation dimension does not match the player's dimension.");
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 64)
            throw new InvalidDataException("Invalid request identifier.");
        if (string.IsNullOrWhiteSpace(request.SchematicJson) || request.SchematicJson.Length > MaxSchematicJsonLength)
            throw new InvalidDataException("The schematic payload is invalid or too large.");
        if (request.MirrorAxis is < 0 or > 3)
            throw new InvalidDataException("Invalid mirror axis.");
    }

    private ServerOperationResult Clear(WorldOperationRequest request, Bounds? excluded)
    {
        var bounds = NormalizeBounds(request);
        ValidateWorldBounds(bounds, request.Dimension);
        var accessor = sapi!.World.GetBlockAccessorBulkUpdate(true, true);
        accessor.ReadFromStagedByDefault = true;
        var result = new ServerOperationResult();
        for (var x = bounds.MinX; x <= bounds.MaxX; x++)
        for (var y = bounds.MinY; y <= bounds.MaxY; y++)
        for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
        {
            if (excluded?.Contains(x, y, z) == true) continue;
            ClearCell(accessor, new BlockPos(x, y, z, request.Dimension), result);
        }
        accessor.Commit();
        return result;
    }

    private ServerOperationResult Fill(WorldOperationRequest request)
    {
        var bounds = NormalizeBounds(request);
        ValidateWorldBounds(bounds, request.Dimension);
        var block = ResolveBlock(request.BlockCode) ?? throw new InvalidDataException($"Unknown block '{request.BlockCode}'.");
        var accessor = sapi!.World.GetBlockAccessorBulkUpdate(true, true);
        accessor.ReadFromStagedByDefault = true;
        var result = new ServerOperationResult();
        for (var x = bounds.MinX; x <= bounds.MaxX; x++)
        for (var y = bounds.MinY; y <= bounds.MaxY; y++)
        for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
        {
            var pos = new BlockPos(x, y, z, request.Dimension);
            if (!string.IsNullOrWhiteSpace(request.ReplaceCode))
            {
                var existing = accessor.GetBlock(pos, BlockLayersAccess.Solid);
                if (!string.Equals(existing?.Code?.ToString(), request.ReplaceCode, StringComparison.OrdinalIgnoreCase)) continue;
            }
            accessor.SetBlock(block.Id, pos, block.ForFluidsLayer ? BlockLayersAccess.Fluid : BlockLayersAccess.Solid);
            result.Placed++;
        }
        accessor.Commit();
        return result;
    }

    private ServerOperationResult Place(WorldOperationRequest request, bool exactSupportBeams = false)
    {
        var error = string.Empty;
        BlockSchematic.BlockRemaps ??= new Dictionary<string, Dictionary<string, string>>();
        BlockSchematic.ItemRemaps ??= new Dictionary<string, Dictionary<string, string>>();
        var schematic = BlockSchematic.LoadFromString(request.SchematicJson, ref error)
            ?? throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "Invalid schematic." : error);
        EnsureCollections(schematic);
        ValidateSchematic(schematic, request);

        var targetBounds = TransformBounds(schematic, request);
        var operationVolume = ValidateVolume(targetBounds, MaxOperationVolume, "Target");
        Bounds? sourceBounds = null;
        if (request.ClearSourceAfterPlace)
        {
            sourceBounds = NormalizeSource(request);
            operationVolume = checked(operationVolume + ValidateVolume(sourceBounds.Value, MaxOperationVolume, "Source"));
            if (operationVolume > MaxOperationVolume)
                throw new InvalidDataException($"Combined operation volume {operationVolume} exceeds the server limit {MaxOperationVolume}.");
        }
        ValidateWorldBounds(targetBounds, request.Dimension);
        if (sourceBounds != null) ValidateWorldBounds(sourceBounds.Value, request.Dimension);

        var accessor = sapi!.World.GetBlockAccessorBulkUpdate(true, true);
        accessor.ReadFromStagedByDefault = true;
        var result = new ServerOperationResult();
        var expected = new HashSet<(int X, int Y, int Z)>();
        var entities = new Dictionary<(int X, int Y, int Z), BlockEntityPlacement>();
        var exactEntities = new Dictionary<(int X, int Y, int Z), ExactBlockEntityPlacement>();
        var exactBeamCount = 0;
        var exactBeamHostCount = 0;

        for (var index = 0; index < schematic.Indices.Count; index++)
        {
            if (!schematic.BlockCodes.TryGetValue(schematic.BlockIds[index], out var originalCode)) continue;
            var packed = schematic.Indices[index];
            var local = Unpack(packed);
            var world = Transform(local.X, local.Y, local.Z, request);
            var pos = new BlockPos(world.X, world.Y, world.Z, request.Dimension);
            var block = ResolveTransformedBlock(originalCode, request);
            if (block == null || block.Id == 0)
            {
                result.UnknownBlocks.Add(originalCode.ToString());
                continue;
            }
            if (!block.ForFluidsLayer && block.Replaceable >= 6000) continue;

            expected.Add(world);
            var layer = block.ForFluidsLayer ? BlockLayersAccess.Fluid : BlockLayersAccess.Solid;
            var actual = accessor.GetBlock(pos, layer);
            var hasEntityPayload = schematic.BlockEntities.TryGetValue(packed, out var payload)
                && !string.IsNullOrWhiteSpace(block.EntityClass);
            var isExactBeam = hasEntityPayload && exactSupportBeams
                && IsSupportBeamPayload(schematic, block, payload!, request, pos, ref exactBeamCount);
            if (isExactBeam && ++exactBeamHostCount > MaxExactBeamHosts)
                throw new InvalidDataException("The exact support beam request contains too many beam hosts.");

            if (request.OnlyMissing && actual?.Id > 0)
            {
                if (isExactBeam && actual.Id == block.Id)
                    exactEntities[world] = CaptureExactPlacement(pos, block, payload!);
                continue;
            }
            accessor.SetBlock(block.Id, pos, layer);
            result.Placed++;
            if (hasEntityPayload)
            {
                if (isExactBeam)
                    exactEntities[world] = CaptureExactPlacement(pos, block, payload!);
                else
                    entities[world] = new BlockEntityPlacement(pos.Copy(), block, payload!);
            }
        }

        if (request.ClearExtra)
        {
            ForEach(targetBounds, (x, y, z) =>
            {
                if (!expected.Contains((x, y, z)))
                    ClearCell(accessor, new BlockPos(x, y, z, request.Dimension), result);
            });
        }
        if (sourceBounds != null)
        {
            ForEach(sourceBounds.Value, (x, y, z) =>
            {
                if (!targetBounds.Contains(x, y, z))
                    ClearCell(accessor, new BlockPos(x, y, z, request.Dimension), result);
            });
        }

        accessor.Commit();
        foreach (var placement in exactEntities.Values)
            RestoreExactSupportBeam(schematic, placement, request, result);
        foreach (var placement in entities.Values) RestoreBlockEntity(schematic, placement, request);
        RestoreDecors(schematic, request);
        return result;
    }

    private ExactBlockEntityPlacement CaptureExactPlacement(BlockPos position, Block block, string payload)
    {
        var oldBeams = new List<BeamEndpoints>();
        var behavior = sapi!.World.BlockAccessor.GetBlockEntity(position)?.GetBehavior<BEBehaviorSupportBeam>();
        if (behavior?.Beams != null)
        {
            foreach (var beam in behavior.Beams)
                if (beam?.Start != null && beam.End != null)
                    oldBeams.Add(new BeamEndpoints(beam.Start.ToVec3d().Add(position),
                        beam.End.ToVec3d().Add(position)));
        }
        return new ExactBlockEntityPlacement(position.Copy(), block, payload, oldBeams);
    }

    private bool IsSupportBeamPayload(BlockSchematic schematic, Block block, string payload,
        WorldOperationRequest request, BlockPos position, ref int totalBeamCount)
    {
        if (block is not BlockSupportBeam) return false;
        var tree = schematic.DecodeBlockEntityData(payload);
        var bytes = tree.GetBytes("beams");
        if (bytes == null || bytes.Length == 0) return false;
        PlacedBeam[] beams;
        try
        {
            beams = SerializerUtil.Deserialize<PlacedBeam[]>(bytes) ?? Array.Empty<PlacedBeam>();
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("The support beam payload is invalid.", exception);
        }
        if (beams.Length == 0) throw new InvalidDataException("The support beam payload contains no beams.");
        if (totalBeamCount > MaxExactBeams - beams.Length)
            throw new InvalidDataException("The exact support beam request contains too many beams.");
        foreach (var beam in beams)
        {
            if (beam?.Start == null || beam.End == null)
                throw new InvalidDataException("The support beam payload contains a null beam.");
            if (beam.FacingIndex < 0 || beam.FacingIndex >= BlockFacing.ALLFACES.Length)
                throw new InvalidDataException("The support beam payload contains an invalid facing.");
            if (!IsFinite(beam.Start.X) || !IsFinite(beam.Start.Y) || !IsFinite(beam.Start.Z)
                || !IsFinite(beam.End.X) || !IsFinite(beam.End.Y) || !IsFinite(beam.End.Z))
                throw new InvalidDataException("The support beam payload contains a non-finite coordinate.");
            var length = beam.Start.DistanceTo(beam.End);
            if (length < 0.01f || length > 20.01f)
                throw new InvalidDataException("The support beam payload contains an invalid length.");
            if (!schematic.BlockCodes.ContainsKey(beam.BlockId))
                throw new InvalidDataException("The support beam payload contains an unmapped block id.");
            var beamBlock = sapi!.World.GetBlock(schematic.BlockCodes[beam.BlockId]);
            if (beamBlock is not BlockSupportBeam)
                throw new InvalidDataException("The support beam payload contains a non-support-beam material.");

            var worldStart = TransformPoint(position, beam.Start, request);
            var worldEnd = TransformPoint(position, beam.End, request);
            ValidateWorldPoint(worldStart, request.Dimension);
            ValidateWorldPoint(worldEnd, request.Dimension);
        }
        totalBeamCount += beams.Length;
        return true;
    }

    private void RestoreExactSupportBeam(BlockSchematic schematic, ExactBlockEntityPlacement placement,
        WorldOperationRequest request, ServerOperationResult result)
    {
        try
        {
            var tree = schematic.DecodeBlockEntityData(placement.Payload);
            TransformExactBeamTree(tree, request);
            tree.SetString("blockCode", placement.Block.Code.ToShortString());
            tree.SetInt("posx", placement.Position.X);
            tree.SetInt("posy", placement.Position.InternalY);
            tree.SetInt("posz", placement.Position.Z);

            var accessor = sapi!.World.BlockAccessor;
            var supportPlacer = sapi.ModLoader.GetModSystem<ModSystemSupportBeamPlacer>();
            foreach (var beam in placement.OldBeams)
                supportPlacer.OnBeamRemoved(beam.Start, beam.End);

            accessor.RemoveBlockEntity(placement.Position);
            accessor.SpawnBlockEntity(placement.Block.EntityClass, placement.Position);
            var entity = accessor.GetBlockEntity(placement.Position);
            var behavior = entity?.GetBehavior<BEBehaviorSupportBeam>();
            if (entity == null || behavior == null)
                throw new InvalidDataException("The support beam block entity could not be created.");
            entity.FromTreeAttributes(tree, sapi.World);
            entity.OnLoadCollectibleMappings(sapi.World, schematic.BlockCodes, schematic.ItemCodes, 0, true);
            entity.OnPlacementBySchematic(sapi, accessor, placement.Position, null, 0, null, true);
            behavior = entity.GetBehavior<BEBehaviorSupportBeam>();
            if (behavior?.Beams == null || behavior.Beams.Length == 0)
                throw new InvalidDataException("The support beam entity did not restore its beam data.");
            foreach (var beam in behavior.Beams)
                if (beam?.Start != null && beam.End != null)
                    supportPlacer.OnBeamAdded(beam.Start.ToVec3d().Add(placement.Position),
                        beam.End.ToVec3d().Add(placement.Position));
            accessor.MarkBlockEntityDirty(placement.Position);
            result.ExactBeamHosts++;
            result.ExactBeams += behavior.Beams.Length;
        }
        catch (Exception exception)
        {
            sapi?.Logger.Warning("[buildwrightserver] Could not restore exact support beams at {0}: {1}",
                placement.Position, exception.Message);
            throw;
        }
    }

    private static (double X, double Y, double Z) TransformPoint(BlockPos host, Vec3f point,
        WorldOperationRequest request)
    {
        var x = point.X;
        var y = point.Y;
        var z = point.Z;
        var turns = NormalizeRotation(request.RotationQuarterTurns);
        if (request.MirrorAxis == 1) x = 1f - x;
        else if (request.MirrorAxis == 2) y = 1f - y;
        else if (request.MirrorAxis == 3) z = 1f - z;
        var (rx, rz) = turns switch
        {
            1 => (z, 1f - x),
            2 => (1f - x, 1f - z),
            3 => (1f - z, x),
            _ => (x, z)
        };
        return (host.X + rx, host.InternalY + y, host.Z + rz);
    }

    private static void TransformExactBeamTree(ITreeAttribute tree, WorldOperationRequest request)
    {
        var bytes = tree.GetBytes("beams");
        if (bytes == null || bytes.Length == 0) return;
        var beams = SerializerUtil.Deserialize<PlacedBeam[]>(bytes) ?? Array.Empty<PlacedBeam>();
        foreach (var beam in beams)
        {
            if (beam?.Start == null || beam.End == null) continue;
            beam.Start = TransformLocalPoint(beam.Start, request);
            beam.End = TransformLocalPoint(beam.End, request);
            beam.FacingIndex = TransformFacingIndex(beam.FacingIndex, request);
        }
        tree.SetBytes("beams", SerializerUtil.Serialize(beams));
    }

    private static Vec3f TransformLocalPoint(Vec3f point, WorldOperationRequest request)
    {
        var x = point.X;
        var y = point.Y;
        var z = point.Z;
        if (request.MirrorAxis == 1) x = 1f - x;
        else if (request.MirrorAxis == 2) y = 1f - y;
        else if (request.MirrorAxis == 3) z = 1f - z;
        var turns = NormalizeRotation(request.RotationQuarterTurns);
        return turns switch
        {
            1 => new Vec3f(z, y, 1f - x),
            2 => new Vec3f(1f - x, y, 1f - z),
            3 => new Vec3f(1f - z, y, x),
            _ => new Vec3f(x, y, z)
        };
    }

    private static int TransformFacingIndex(int facingIndex, WorldOperationRequest request)
    {
        if (facingIndex < 0 || facingIndex >= BlockFacing.ALLFACES.Length) return facingIndex;
        var normal = BlockFacing.ALLFACES[facingIndex].Normali;
        var x = normal.X;
        var y = normal.Y;
        var z = normal.Z;
        if (request.MirrorAxis == 1) x = -x;
        else if (request.MirrorAxis == 2) y = -y;
        else if (request.MirrorAxis == 3) z = -z;
        var turns = NormalizeRotation(request.RotationQuarterTurns);
        var (rx, rz) = turns switch
        {
            1 => (z, -x),
            2 => (-x, -z),
            3 => (-z, x),
            _ => (x, z)
        };
        return BlockFacing.FromVector(rx, y, rz).Index;
    }

    private void RestoreBlockEntity(BlockSchematic schematic, BlockEntityPlacement placement, WorldOperationRequest request)
    {
        try
        {
            var tree = schematic.DecodeBlockEntityData(placement.Payload);
            var axis = MirrorToAxis(request.MirrorAxis);
            var angle = NormalizeRotation(request.RotationQuarterTurns) * 90;
            if (angle != 0 || axis != null)
            {
                var temporary = sapi!.World.ClassRegistry.CreateBlockEntity(placement.Block.EntityClass);
                temporary.Pos = placement.Position.Copy();
                temporary.CreateBehaviors(placement.Block, sapi.World);
                if (temporary is IRotatable rotatable)
                    rotatable.OnTransformed(sapi.World, tree, angle, schematic.BlockCodes, schematic.ItemCodes, axis);
            }
            tree.SetString("blockCode", placement.Block.Code.ToShortString());
            tree.SetInt("posx", placement.Position.X);
            tree.SetInt("posy", placement.Position.InternalY);
            tree.SetInt("posz", placement.Position.Z);

            var accessor = sapi!.World.BlockAccessor;
            accessor.RemoveBlockEntity(placement.Position);
            accessor.SpawnBlockEntity(placement.Block.EntityClass, placement.Position);
            var entity = accessor.GetBlockEntity(placement.Position);
            if (entity == null) return;
            entity.FromTreeAttributes(tree, sapi.World);
            entity.OnLoadCollectibleMappings(sapi.World, schematic.BlockCodes, schematic.ItemCodes, 0, true);
            entity.OnPlacementBySchematic(sapi, accessor, placement.Position, null, 0, null, true);
            accessor.MarkBlockEntityDirty(placement.Position);
        }
        catch (Exception exception)
        {
            sapi?.Logger.Warning("[buildwrightserver] Could not restore block entity at {0}: {1}",
                placement.Position, exception.Message);
        }
    }

    private void RestoreDecors(BlockSchematic schematic, WorldOperationRequest request)
    {
        var accessor = sapi!.World.BlockAccessor;
        for (var index = 0; index < schematic.DecorIndices.Count && index < schematic.DecorIds.Count; index++)
        {
            var local = Unpack(schematic.DecorIndices[index]);
            var world = Transform(local.X, local.Y, local.Z, request);
            var stored = schematic.DecorIds[index];
            var codeId = (int)(stored & 0xFFFFFF);
            if (!schematic.BlockCodes.TryGetValue(codeId, out var code)) continue;
            var block = ResolveTransformedBlock(code, request);
            if (block == null) continue;

            var decorIndex = (int)(stored >> 24);
            if (decorIndex < 0) continue;
            var subPosition = decorIndex / 6;
            var face = BlockFacing.ALLFACES[decorIndex % 6];
            var axis = MirrorToAxis(request.MirrorAxis);
            if (axis != null && face.Axis == axis) face = face.Opposite;
            var angle = NormalizeRotation(request.RotationQuarterTurns) * 90;
            if (angle != 0 && face.IsHorizontal) face = face.GetHorizontalRotated(angle);
            accessor.SetDecor(block, new BlockPos(world.X, world.Y, world.Z, request.Dimension), subPosition * 6 + face.Index);
        }
    }

    private Block? ResolveTransformedBlock(AssetLocation originalCode, WorldOperationRequest request)
    {
        var block = sapi!.World.GetBlock(originalCode);
        if (block == null) return null;
        try
        {
            block = request.MirrorAxis switch
            {
                1 => sapi.World.GetBlock(block.GetHorizontallyFlippedBlockCode(EnumAxis.X)) ?? block,
                2 => sapi.World.GetBlock(block.GetVerticallyFlippedBlockCode()) ?? block,
                3 => sapi.World.GetBlock(block.GetHorizontallyFlippedBlockCode(EnumAxis.Z)) ?? block,
                _ => block
            };
            var angle = NormalizeRotation(request.RotationQuarterTurns) * 90;
            return angle == 0 ? block : sapi.World.GetBlock(block.GetRotatedBlockCode(angle)) ?? block;
        }
        catch
        {
            return block;
        }
    }

    private Block? ResolveBlock(string code) => string.IsNullOrWhiteSpace(code) ? null : sapi!.World.GetBlock(new AssetLocation(code));

    private static (int X, int Y, int Z) Transform(int x, int y, int z, WorldOperationRequest request)
    {
        x -= request.AnchorOffsetX;
        y -= request.AnchorOffsetY;
        z -= request.AnchorOffsetZ;
        if (request.MirrorAxis == 1) x = -x;
        else if (request.MirrorAxis == 2) y = -y;
        else if (request.MirrorAxis == 3) z = -z;
        return NormalizeRotation(request.RotationQuarterTurns) switch
        {
            1 => (request.AnchorX + z, request.AnchorY + y, request.AnchorZ - x),
            2 => (request.AnchorX - x, request.AnchorY + y, request.AnchorZ - z),
            3 => (request.AnchorX - z, request.AnchorY + y, request.AnchorZ + x),
            _ => (request.AnchorX + x, request.AnchorY + y, request.AnchorZ + z)
        };
    }

    private static Bounds TransformBounds(BlockSchematic schematic, WorldOperationRequest request)
    {
        var minX = int.MaxValue; var minY = int.MaxValue; var minZ = int.MaxValue;
        var maxX = int.MinValue; var maxY = int.MinValue; var maxZ = int.MinValue;
        foreach (var x in new[] { 0, schematic.SizeX - 1 })
        foreach (var y in new[] { 0, schematic.SizeY - 1 })
        foreach (var z in new[] { 0, schematic.SizeZ - 1 })
        {
            var world = Transform(x, y, z, request);
            minX = Math.Min(minX, world.X); minY = Math.Min(minY, world.Y); minZ = Math.Min(minZ, world.Z);
            maxX = Math.Max(maxX, world.X); maxY = Math.Max(maxY, world.Y); maxZ = Math.Max(maxZ, world.Z);
        }
        return new Bounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private void ValidateSchematic(BlockSchematic schematic, WorldOperationRequest request)
    {
        if (schematic.SizeX is <= 0 or > 1024 || schematic.SizeY is <= 0 or > 1024 || schematic.SizeZ is <= 0 or > 1024)
            throw new InvalidDataException("Schematic dimensions must be between 1 and 1024 blocks per axis.");
        if (schematic.Indices.Count != schematic.BlockIds.Count || schematic.Indices.Count > MaxOperationVolume)
            throw new InvalidDataException("The schematic block list is invalid or too large.");
        if (schematic.DecorIndices.Count != schematic.DecorIds.Count)
            throw new InvalidDataException("The schematic decor list is invalid.");
        foreach (var packed in schematic.Indices) ValidatePackedPosition(packed, schematic);
        foreach (var packed in schematic.DecorIndices) ValidatePackedPosition(packed, schematic);
        if (request.AnchorOffsetX < 0 || request.AnchorOffsetX >= schematic.SizeX ||
            request.AnchorOffsetY < 0 || request.AnchorOffsetY >= schematic.SizeY ||
            request.AnchorOffsetZ < 0 || request.AnchorOffsetZ >= schematic.SizeZ)
            throw new InvalidDataException("The schematic anchor lies outside its bounds.");
        if (request.MirrorAxis is < 0 or > 3)
            throw new InvalidDataException("Invalid mirror axis.");
    }

    private void ValidateWorldBounds(Bounds bounds, int dimension)
    {
        var accessor = sapi!.World.BlockAccessor;
        if (!accessor.IsValidPos(new BlockPos(bounds.MinX, bounds.MinY, bounds.MinZ, dimension)) ||
            !accessor.IsValidPos(new BlockPos(bounds.MaxX, bounds.MaxY, bounds.MaxZ, dimension)))
            throw new InvalidOperationException("The operation extends outside the valid world bounds.");
    }

    private static long ValidateVolume(Bounds bounds, long maximum, string name)
    {
        long volume;
        try
        {
            volume = checked(((long)bounds.MaxX - bounds.MinX + 1) * ((long)bounds.MaxY - bounds.MinY + 1) *
                             ((long)bounds.MaxZ - bounds.MinZ + 1));
        }
        catch (OverflowException)
        {
            throw new InvalidDataException($"{name} volume is invalid.");
        }
        if (volume <= 0 || volume > maximum)
            throw new InvalidDataException($"{name} volume {volume} exceeds the server limit {maximum}.");
        return volume;
    }

    private static void ClearCell(IBlockAccessor accessor, BlockPos pos, ServerOperationResult result)
    {
        var solid = accessor.GetBlock(pos, BlockLayersAccess.Solid);
        var fluid = accessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (solid?.Id <= 0 && fluid?.Id <= 0) return;
        accessor.SetBlock(0, pos, BlockLayersAccess.Solid);
        accessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
        result.Cleared++;
    }

    private static void ForEach(Bounds bounds, Action<int, int, int> action)
    {
        for (var x = bounds.MinX; x <= bounds.MaxX; x++)
        for (var y = bounds.MinY; y <= bounds.MaxY; y++)
        for (var z = bounds.MinZ; z <= bounds.MaxZ; z++) action(x, y, z);
    }

    private static void EnsureCollections(BlockSchematic schematic)
    {
        schematic.BlockCodes ??= new Dictionary<int, AssetLocation>();
        schematic.ItemCodes ??= new Dictionary<int, AssetLocation>();
        schematic.Indices ??= new List<uint>();
        schematic.BlockIds ??= new List<int>();
        schematic.DecorIndices ??= new List<uint>();
        schematic.DecorIds ??= new List<long>();
        schematic.BlockEntities ??= new Dictionary<uint, string>();
    }

    private static (int X, int Y, int Z) Unpack(uint packed) =>
        ((int)(packed & 0x3ff), (int)((packed >> 20) & 0x3ff), (int)((packed >> 10) & 0x3ff));

    private static void ValidatePackedPosition(uint packed, BlockSchematic schematic)
    {
        var local = Unpack(packed);
        if (local.X >= schematic.SizeX || local.Y >= schematic.SizeY || local.Z >= schematic.SizeZ)
            throw new InvalidDataException("The schematic contains a block outside its declared bounds.");
    }

    private static int NormalizeRotation(int turns) => (turns % 4 + 4) % 4;

    private void ValidateWorldPoint((double X, double Y, double Z) point, int dimension)
    {
        if (!IsFinite(point.X) || !IsFinite(point.Y) || !IsFinite(point.Z))
            throw new InvalidDataException("The support beam endpoint is not finite.");
        var position = new BlockPos((int)Math.Floor(point.X), (int)Math.Floor(point.Y),
            (int)Math.Floor(point.Z), dimension);
        if (!sapi!.World.BlockAccessor.IsValidPos(position))
            throw new InvalidOperationException("The support beam endpoint is outside the valid world bounds.");
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static EnumAxis? MirrorToAxis(int mirror) => mirror switch
    {
        1 => EnumAxis.X,
        2 => EnumAxis.Y,
        3 => EnumAxis.Z,
        _ => null
    };

    private static Bounds NormalizeBounds(WorldOperationRequest request) => new(
        Math.Min(request.MinX, request.MaxX), Math.Min(request.MinY, request.MaxY), Math.Min(request.MinZ, request.MaxZ),
        Math.Max(request.MinX, request.MaxX), Math.Max(request.MinY, request.MaxY), Math.Max(request.MinZ, request.MaxZ));

    private static Bounds NormalizeExcluded(WorldOperationRequest request) => new(
        Math.Min(request.ExcludeMinX, request.ExcludeMaxX), Math.Min(request.ExcludeMinY, request.ExcludeMaxY), Math.Min(request.ExcludeMinZ, request.ExcludeMaxZ),
        Math.Max(request.ExcludeMinX, request.ExcludeMaxX), Math.Max(request.ExcludeMinY, request.ExcludeMaxY), Math.Max(request.ExcludeMinZ, request.ExcludeMaxZ));

    private static Bounds NormalizeSource(WorldOperationRequest request) => new(
        Math.Min(request.SourceMinX, request.SourceMaxX), Math.Min(request.SourceMinY, request.SourceMaxY), Math.Min(request.SourceMinZ, request.SourceMaxZ),
        Math.Max(request.SourceMinX, request.SourceMaxX), Math.Max(request.SourceMinY, request.SourceMaxY), Math.Max(request.SourceMinZ, request.SourceMaxZ));

    private readonly record struct Bounds(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ)
    {
        public bool Contains(int x, int y, int z) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY && z >= MinZ && z <= MaxZ;
    }

    private readonly record struct BlockEntityPlacement(BlockPos Position, Block Block, string Payload);
    private readonly record struct ExactBlockEntityPlacement(BlockPos Position, Block Block, string Payload,
        IReadOnlyList<BeamEndpoints> OldBeams);
    private readonly record struct BeamEndpoints(Vec3d Start, Vec3d End);

    private sealed class ServerOperationResult
    {
        public int Placed { get; set; }
        public int Cleared { get; set; }
        public int ExactBeamHosts { get; set; }
        public int ExactBeams { get; set; }
        public HashSet<string> UnknownBlocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
