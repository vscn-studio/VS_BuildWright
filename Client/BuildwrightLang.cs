using Vintagestory.API.Config;

namespace Buildwright.Client;

internal static class BuildwrightLang
{
    public static string Get(string key, params object[] args) => Lang.Get("buildwright:" + key, args);
}
