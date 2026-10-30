using System.Runtime.InteropServices;
using ImGuiNET;

namespace Buildwright.Client;

/// <summary>Compatibility with VSImGui 1.2.7's legacy modifier and clipboard backend.</summary>
internal sealed class BuildwrightImGuiInput(Func<string> readClipboard, Action<string> writeClipboard,
    Action<Exception> reportError) : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetClipboard(IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetClipboard(IntPtr userData, IntPtr text);

    // Keep delegates rooted for as long as native ImGui can call them.
    private GetClipboard? getClipboard;
    private SetClipboard? setClipboard;
    private IntPtr clipboardBuffer;

    public IDisposable Use()
    {
        getClipboard ??= Read;
        setClipboard ??= Write;
        return new Scope(this);
    }

    private IntPtr Read(IntPtr userData)
    {
        string text;
        try { text = readClipboard() ?? string.Empty; }
        catch (Exception exception) { reportError(exception); text = string.Empty; }
        // ImGui borrows this UTF-8 pointer until the next read or disposal.
        if (clipboardBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(clipboardBuffer);
        return clipboardBuffer = Marshal.StringToCoTaskMemUTF8(text);
    }

    private void Write(IntPtr userData, IntPtr text)
    {
        try { writeClipboard(Marshal.PtrToStringUTF8(text) ?? string.Empty); }
        catch (Exception exception) { reportError(exception); }
    }

    public void Dispose()
    {
        if (clipboardBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(clipboardBuffer);
        clipboardBuffer = IntPtr.Zero;
    }

    private sealed class Scope : IDisposable
    {
        private ImGuiIOPtr io = ImGui.GetIO();
        private readonly IntPtr previousGet, previousSet, previousUserData;
        private readonly bool ctrl, shift, alt, super;
        private readonly ImGuiKey mods;

        public Scope(BuildwrightImGuiInput owner)
        {
            previousGet = io.GetClipboardTextFn;
            previousSet = io.SetClipboardTextFn;
            previousUserData = io.ClipboardUserData;
            ctrl = io.KeyCtrl; shift = io.KeyShift; alt = io.KeyAlt; super = io.KeySuper;
            mods = io.KeyMods;
            io.GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(owner.getClipboard!);
            io.SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(owner.setClipboard!);
            io.ClipboardUserData = IntPtr.Zero;

            // 1.2.7 queues physical keys but writes legacy KeyCtrl/Shift fields.
            // NewFrame overwrites those fields because ModCtrl/Shift events are absent.
            // Rebuild modifiers from the processed physical keys for our widgets only.
            io.KeyCtrl = ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl);
            io.KeyShift = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
            io.KeyAlt = ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);
            io.KeySuper = ImGui.IsKeyDown(ImGuiKey.LeftSuper) || ImGui.IsKeyDown(ImGuiKey.RightSuper);
            io.KeyMods = (io.KeyCtrl ? ImGuiKey.ModCtrl : 0) | (io.KeyShift ? ImGuiKey.ModShift : 0)
                | (io.KeyAlt ? ImGuiKey.ModAlt : 0) | (io.KeySuper ? ImGuiKey.ModSuper : 0);
        }

        public void Dispose()
        {
            io.GetClipboardTextFn = previousGet;
            io.SetClipboardTextFn = previousSet;
            io.ClipboardUserData = previousUserData;
            io.KeyCtrl = ctrl; io.KeyShift = shift; io.KeyAlt = alt; io.KeySuper = super;
            io.KeyMods = mods;
        }
    }
}
