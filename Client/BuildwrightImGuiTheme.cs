using System.Numerics;
using ImGuiNET;

namespace Buildwright.Client;

// Compact spacing and the dark palette used by the KeyOverlay reference windows.
internal static class BuildwrightImGuiTheme
{
    internal static Scope Use()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 5));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(5, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 12);
        ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 14);
        (ImGuiCol Color, uint Value)[] colors =
        [
            (ImGuiCol.Text, 0xFFFFFFFF), (ImGuiCol.TextDisabled, 0xFFAAAAAA),
            (ImGuiCol.WindowBg, 0xFF282020), (ImGuiCol.ChildBg, 0), (ImGuiCol.PopupBg, 0xFF382828),
            (ImGuiCol.Border, 0xFF483C3C), (ImGuiCol.Separator, 0xFF483C3C),
            (ImGuiCol.TitleBg, 0xFF1B1515), (ImGuiCol.TitleBgActive, 0xFF261D1D), (ImGuiCol.TitleBgCollapsed, 0xFF1B1515),
            (ImGuiCol.FrameBg, 0xAA3F3838), (ImGuiCol.FrameBgHovered, 0xCC4B4343), (ImGuiCol.FrameBgActive, 0xFF322C2C),
            (ImGuiCol.Button, 0xFF524444), (ImGuiCol.ButtonHovered, 0xFFF37C00), (ImGuiCol.ButtonActive, 0xFFA25300),
            (ImGuiCol.Header, 0), (ImGuiCol.HeaderHovered, 0xBBDD7200), (ImGuiCol.HeaderActive, 0xFF954C00),
            (ImGuiCol.CheckMark, 0xFFFB8800), (ImGuiCol.SliderGrab, 0xFFFB8800), (ImGuiCol.SliderGrabActive, 0xFFFFAB45),
            (ImGuiCol.ScrollbarBg, 0xFF282020), (ImGuiCol.ScrollbarGrab, 0xFF605454),
            (ImGuiCol.ScrollbarGrabHovered, 0xFF807070), (ImGuiCol.ScrollbarGrabActive, 0xFFFB8800),
            (ImGuiCol.TextSelectedBg, 0x887F4600), (ImGuiCol.NavHighlight, 0xFFFB8800)
        ];
        foreach (var color in colors) ImGui.PushStyleColor(color.Color, color.Value);
        return new(colors.Length);
    }

    internal readonly struct Scope(int colors) : IDisposable
    {
        public void Dispose() { ImGui.PopStyleColor(colors); ImGui.PopStyleVar(8); }
    }
}
