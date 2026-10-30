using Buildwright.Core;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Buildwright.Api;

/// <summary>
/// Result of a compat adapter's strict block-entity comparison.
/// NotHandled deliberately falls back to Buildwright's normal tree comparison.
/// </summary>
public enum BuildwrightCompatComparison
{
    NotHandled,
    Match,
    Mismatch,
    Unknown
}

/// <summary>
/// A serialized payload owned by a registered compatibility adapter.
/// The data is stored alongside the normal BlockEntity tree in a .vsbp file.
/// </summary>
public sealed class BuildwrightCompatPayload
{
    [Newtonsoft.Json.JsonProperty("adapter")] public string AdapterId { get; set; } = string.Empty;
    [Newtonsoft.Json.JsonProperty("data")] public JToken Data { get; set; } = new JObject();
}

/// <summary>
/// Optional client-side integration point for mods with custom BlockEntity state or rendering.
/// Implementations must return freshly owned MeshData/ItemStack instances because
/// Buildwright applies opacity and may cache or retain the returned value.
/// </summary>
public interface IBuildwrightCompat
{
    /// <summary>A stable, namespaced identifier, for example "examplemod:machine".</summary>
    string Id { get; }

    /// <summary>Whether this adapter owns the supplied block/entity pair.</summary>
    bool Handles(Block block, BlockEntity? entity);

    /// <summary>Captures mod-specific state. Return null to store no custom payload.</summary>
    JToken? Capture(ICoreClientAPI capi, Block block, BlockEntity entity) => null;

    /// <summary>Builds the local-space ghost mesh. Return null to use Buildwright's fallback mesh.</summary>
    MeshData? BuildGhostMesh(ICoreClientAPI capi, Block block, JToken data) => null;

    /// <summary>Creates a material-list icon stack. Return null to use the normal block stack.</summary>
    ItemStack? CreateDisplayStack(ICoreClientAPI capi, Block block) => null;

    /// <summary>
    /// Applies rotation/mirror changes to captured state before strict comparison or placement.
    /// The default preserves the payload unchanged.
    /// </summary>
    JToken Transform(JToken data, TransformState transform) => data.DeepClone();

    /// <summary>Compares custom state in strict mode.</summary>
    BuildwrightCompatComparison Compare(ICoreClientAPI capi, Block expectedBlock,
        BlockEntity? actualEntity, JToken data, TransformState transform) => BuildwrightCompatComparison.NotHandled;

    /// <summary>
    /// Reserved for compatibility and future restoration support. Buildwright 0.4.0
    /// server-authoritative world operations do not invoke this callback.
    /// </summary>
    bool Restore(ICoreClientAPI capi, Block block, BlockEntity entity,
        JToken data, TransformState transform) => false;
}

/// <summary>
/// Public registry used by optional companion mods. Register from StartClientSide
/// after Buildwright is loaded. Registration replaces an earlier adapter with the
/// same Id, which makes hot-reload and mod updates deterministic.
/// </summary>
public static class BuildwrightCompatApi
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, IBuildwrightCompat> Adapters = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(IBuildwrightCompat adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (string.IsNullOrWhiteSpace(adapter.Id) || !adapter.Id.Contains(':'))
            throw new ArgumentException("Buildwright compat adapter Id must be namespaced (modid:name).", nameof(adapter));
        lock (Sync) Adapters[adapter.Id] = adapter;
    }

    public static bool Unregister(string adapterId)
    {
        lock (Sync) return Adapters.Remove(adapterId);
    }

    public static IReadOnlyCollection<string> RegisteredAdapterIds
    {
        get { lock (Sync) return Adapters.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    internal static List<BuildwrightCompatPayload> Capture(ICoreClientAPI capi, Block block, BlockEntity entity)
    {
        var result = new List<BuildwrightCompatPayload>();
        foreach (var adapter in Snapshot())
        {
            try
            {
                if (!adapter.Handles(block, entity)) continue;
                var data = adapter.Capture(capi, block, entity);
                if (data != null) result.Add(new BuildwrightCompatPayload { AdapterId = adapter.Id, Data = data });
            }
            catch (Exception exception)
            {
                capi.Logger.Warning("[buildwright] Compat capture '{0}' failed for {1}: {2}", adapter.Id, block.Code, exception.Message);
            }
        }
        return result;
    }

    internal static MeshData? BuildGhostMesh(ICoreClientAPI capi, Block block, IEnumerable<BuildwrightCompatPayload>? payloads)
    {
        foreach (var payload in payloads ?? Array.Empty<BuildwrightCompatPayload>())
        {
            if (!TryGet(payload.AdapterId, out var adapter)) continue;
            try
            {
                var mesh = adapter.BuildGhostMesh(capi, block, payload.Data);
                if (mesh?.VerticesCount > 0) return mesh;
            }
            catch (Exception exception)
            {
                capi.Logger.Warning("[buildwright] Compat ghost mesh '{0}' failed for {1}: {2}", adapter.Id, block.Code, exception.Message);
            }
        }
        return null;
    }

    internal static ItemStack? CreateDisplayStack(ICoreClientAPI capi, Block block)
    {
        foreach (var adapter in Snapshot())
        {
            try
            {
                if (!adapter.Handles(block, null)) continue;
                var stack = adapter.CreateDisplayStack(capi, block);
                if (stack != null) return stack;
            }
            catch (Exception exception)
            {
                capi.Logger.Warning("[buildwright] Compat display stack '{0}' failed for {1}: {2}", adapter.Id, block.Code, exception.Message);
            }
        }
        return null;
    }

    internal static BuildwrightCompatComparison Compare(ICoreClientAPI capi, Block block, BlockEntity? actual,
        BuildwrightCompatPayload payload, TransformState transform)
    {
        if (!TryGet(payload.AdapterId, out var adapter)) return BuildwrightCompatComparison.NotHandled;
        try
        {
            return adapter.Compare(capi, block, actual, adapter.Transform(payload.Data, transform), transform);
        }
        catch (Exception exception)
        {
            capi.Logger.Warning("[buildwright] Compat comparison '{0}' failed for {1}: {2}", adapter.Id, block.Code, exception.Message);
            return BuildwrightCompatComparison.Unknown;
        }
    }

    internal static void Restore(ICoreClientAPI capi, Block block, BlockEntity entity,
        IEnumerable<BuildwrightCompatPayload>? payloads, TransformState transform)
    {
        foreach (var payload in payloads ?? Array.Empty<BuildwrightCompatPayload>())
        {
            if (!TryGet(payload.AdapterId, out var adapter)) continue;
            try { adapter.Restore(capi, block, entity, adapter.Transform(payload.Data, transform), transform); }
            catch (Exception exception)
            {
                capi.Logger.Warning("[buildwright] Compat restore '{0}' failed for {1}: {2}", adapter.Id, block.Code, exception.Message);
            }
        }
    }

    private static bool TryGet(string id, out IBuildwrightCompat adapter)
    {
        lock (Sync) return Adapters.TryGetValue(id, out adapter!);
    }

    private static IBuildwrightCompat[] Snapshot()
    {
        lock (Sync) return Adapters.Values.OrderBy(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
