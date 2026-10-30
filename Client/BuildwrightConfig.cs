using Buildwright.Core;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Client;

namespace Buildwright.Client;

public sealed class BuildwrightConfig
{
    [JsonProperty("CreatorInstallId", NullValueHandling = NullValueHandling.Ignore)]
    private string? LegacyCreatorInstallId { get; set; }

    public MatchingMode DefaultMatchingMode { get; set; } = MatchingMode.Loose;
    public float GhostAlpha { get; set; } = 0.32f;
    public int RenderDistanceBlocks { get; set; } = 256;
    public int HudOffsetX { get; set; } = 8;
    public int HudOffsetY { get; set; } = 60;
    public int ScanIntervalMs { get; set; } = 350;
    public bool ToolItemEnabled { get; set; } = true;
    public BuildwrightToolMode ToolMode { get; set; } = BuildwrightToolMode.AreaSelection;
    public Dictionary<string, BuildwrightWindowPosition> WindowPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static BuildwrightConfig Load(ICoreAPI api)
    {
        try
        {
            var value = api.LoadModConfig<BuildwrightConfig>("Buildwright.json");
            if (value != null)
            {
                value.WindowPositions ??= new Dictionary<string, BuildwrightWindowPosition>(StringComparer.OrdinalIgnoreCase);
                if (value.LegacyCreatorInstallId is not null)
                {
                    value.LegacyCreatorInstallId = null;
                    api.StoreModConfig(value, "Buildwright.json");
                }
                return value;
            }
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[buildwright] Could not load Buildwright.json: {0}", ex.Message);
        }

        var defaults = new BuildwrightConfig();
        try { api.StoreModConfig(defaults, "Buildwright.json"); } catch { }
        return defaults;
    }
}

internal static class BuildwrightAuthorIdentity
{
    public static string AuthorName(IWorldAccessor world)
    {
        var player = (world.Api as ICoreClientAPI)?.World.Player;
        return string.IsNullOrWhiteSpace(player?.PlayerName) ? "Unknown Author" : player.PlayerName.Trim();
    }
}

public sealed class BuildwrightWindowPosition
{
    public int X { get; set; }
    public int Y { get; set; }
}
