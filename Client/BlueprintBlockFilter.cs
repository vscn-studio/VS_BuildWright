using Vintagestory.API.Common;

namespace Buildwright.Client;

/// <summary>
/// Natural, replaceable vegetation is incidental to a building selection. It
/// should neither become a blueprint requirement nor block completion checks.
/// </summary>
internal static class BlueprintBlockFilter
{
    public static bool IsTransient(Block? block)
    {
        return block is { Id: > 0, ForFluidsLayer: false } && block.Replaceable >= 6000;
    }
}
