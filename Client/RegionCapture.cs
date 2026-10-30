using Buildwright.Core;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Newtonsoft.Json.Linq;

namespace Buildwright.Client;

internal static class RegionCapture
{
    private const uint Mask = 0x3ff;

    public static BlueprintDocument Capture(IWorldAccessor world, BlockPos min, BlockPos max, BlockPos anchor, BlockFacing? anchorFace)
    {
        var sizeX = max.X - min.X + 1;
        var sizeY = max.Y - min.Y + 1;
        var sizeZ = max.Z - min.Z + 1;
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0) throw new InvalidDataException("Selection dimensions must be positive.");
        if (sizeX > 1024 || sizeY > 1024 || sizeZ > 1024) throw new InvalidDataException("Vintage Story schematic coordinates support at most 1024 blocks per axis.");

        var schematic = new BlockSchematic
        {
            SizeX = sizeX,
            SizeY = sizeY,
            SizeZ = sizeZ,
            GameVersion = Vintagestory.API.Config.GameVersion.ShortGameVersion,
            OriginalPos = min.Copy()
        };
        var document = new BlueprintDocument();
        var materialCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockEntityRecords = new List<BlockEntityRecord>();
        var chiselRecords = new List<ChiselVoxelData>();
        var blockCount = 0;

        for (var x = 0; x < sizeX; x++)
        for (var y = 0; y < sizeY; y++)
        for (var z = 0; z < sizeZ; z++)
        {
            var pos = new BlockPos(min.X + x, min.Y + y, min.Z + z, min.dimension);
            var solid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
            var fluid = world.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
            if (fluid?.Id == solid?.Id) fluid = null;

            AddBlock(schematic, solid, x, y, z, materialCodes, ref blockCount);
            AddBlock(schematic, fluid, x, y, z, materialCodes, ref blockCount);

            var decors = world.BlockAccessor.GetSubDecors(pos);
            if (decors != null)
            {
                foreach (var decor in decors)
                {
                    if (decor.Value?.Code == null) continue;
                    schematic.BlockCodes[decor.Value.Id] = decor.Value.Code;
                    materialCodes.Add(decor.Value.Code.ToString());
                    schematic.DecorIndices.Add(Pack(x, y, z));
                    schematic.DecorIds.Add(((long)decor.Key << 24) + decor.Value.Id);
                }
            }

            var entity = world.BlockAccessor.GetBlockEntity(pos);
            if (entity == null) continue;
            var local = new Int3(x, y, z);
            var record = BlockEntityCapture.CaptureRecord(entity, local, schematic);
            if (record != null)
            {
                record.CompatData = Buildwright.Api.BuildwrightCompatApi.Capture((Vintagestory.API.Client.ICoreClientAPI)world.Api,
                    solid ?? fluid!, entity);
                blockEntityRecords.Add(record);
                var packed = Pack(x, y, z);
                schematic.BlockEntities[packed] = record.TreeAscii85;
            }

            try
            {
                entity.OnStoreCollectibleMappings(schematic.BlockCodes, schematic.ItemCodes);
            }
            catch
            {
                // The custom chisel record below still preserves voxel data.
            }

            if (entity.Api != null)
            {
                var chisel = BlockEntityCapture.TryCaptureChisel(entity, local);
                if (chisel != null)
                {
                    chiselRecords.Add(chisel);
                    AddChiselMaterialMappings(world, schematic, chisel);
                }
            }
        }

        document.Schematic = JObject.Parse(schematic.ToJson());
        document.BlockEntities = blockEntityRecords;
        document.ChiselData = chiselRecords;
        document.Metadata = new BlueprintMetadata
        {
            Name = "Captured Blueprint",
            Author = BuildwrightAuthorIdentity.AuthorName(world),
            GameVersion = schematic.GameVersion,
            Dimensions = new Int3(sizeX, sizeY, sizeZ),
            AnchorOffset = new Int3(anchor.X - min.X, anchor.Y - min.Y, anchor.Z - min.Z),
            AnchorFace = anchorFace?.Code,
            BlockCount = blockCount,
            MaterialCount = materialCodes.Count,
            Capabilities = BuildCapabilities(blockEntityRecords, chiselRecords)
        };
        foreach (var code in schematic.BlockCodes.Values)
        {
            var domain = code.Domain;
            if (!string.IsNullOrWhiteSpace(domain) && !domain.Equals("game", StringComparison.OrdinalIgnoreCase)
                && !document.Metadata.Dependencies.Contains(domain, StringComparer.OrdinalIgnoreCase))
                document.Metadata.Dependencies.Add(domain);
        }
        return document;
    }

    private static void AddBlock(BlockSchematic schematic, Block? block, int x, int y, int z,
        ISet<string> materialCodes, ref int blockCount)
    {
        if (block == null || block.Id == 0 || block.Code == null) return;
        var code = block.Code.ToString();
        schematic.BlockCodes[block.Id] = block.Code;
        materialCodes.Add(code);
        schematic.Indices.Add(Pack(x, y, z));
        schematic.BlockIds.Add(block.Id);
        blockCount++;
    }

    private static List<string> BuildCapabilities(List<BlockEntityRecord> entities, List<ChiselVoxelData> chisel)
    {
        var capabilities = new List<string> { "schematic", "anchor", "dependencies" };
        if (entities.Count > 0) capabilities.Add("blockentities");
        if (chisel.Count > 0) capabilities.Add("chisel-voxels");
        return capabilities;
    }

    private static void AddChiselMaterialMappings(IWorldAccessor world, BlockSchematic schematic, ChiselVoxelData chisel)
    {
        AddMappings(chisel.BlockIds);
        AddMappings(chisel.DecorIds);

        void AddMappings(IEnumerable<int>? ids)
        {
            if (ids == null) return;
            foreach (var id in ids)
            {
                var material = world.GetBlock(id);
                if (material?.Code != null) schematic.BlockCodes[id] = material.Code;
            }
        }
    }

    public static uint Pack(int x, int y, int z) => ((uint)x & Mask) | (((uint)z & Mask) << 10) | (((uint)y & Mask) << 20);

    public static Int3 Unpack(uint packed) => new((int)(packed & Mask), (int)((packed >> 20) & Mask), (int)((packed >> 10) & Mask));
}
