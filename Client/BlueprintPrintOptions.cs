using ImGuiNET;

namespace Buildwright.Client;

/// <summary>Shared session settings for both one-click export entry points.</summary>
internal static class BlueprintPrintOptions
{
    public static float LongestEdgeMm = 100;
    public static int Resolution = 128;

    public static void Draw()
    {
        ImGui.TextUnformatted(BuildwrightLang.Get("ui.model.print-size"));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputFloat("##print-mm", ref LongestEdgeMm, 1, 10, "%.1f"))
            LongestEdgeMm = float.IsFinite(LongestEdgeMm) ? Math.Clamp(LongestEdgeMm, 1, 2000) : 100;
        ImGui.TextUnformatted(BuildwrightLang.Get("ui.model.print-resolution"));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("##print-resolution", ref Resolution, 16, 32)) Resolution = Math.Clamp(Resolution, 32, 256);
    }
}
