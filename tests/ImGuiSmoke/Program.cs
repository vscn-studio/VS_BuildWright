using System.Numerics;
using Buildwright.Client;
using ImGuiNET;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var context = ImGui.CreateContext();
try
{
    var io = ImGui.GetIO();
    unsafe { io.NativePtr->IniFilename = null; }
    io.DisplaySize = new Vector2(800, 600);
    io.DeltaTime = 1f / 60;
    io.ConfigInputTrickleEventQueue = false;
    io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
    string clipboard = "";
    using var input = new BuildwrightImGuiInput(() => clipboard, text => clipboard = text,
        exception => throw new InvalidOperationException("Clipboard callback failed", exception));
    string value = "中文蓝图 / Blueprint";
    void Frame(bool focus = false, bool password = false)
    {
        ImGui.NewFrame();
        var get = io.GetClipboardTextFn;
        var set = io.SetClipboardTextFn;
        var userData = io.ClipboardUserData;
        var mods = io.KeyMods;
        using (input.Use())
        {
            ImGui.SetNextWindowPos(new Vector2(10, 10));
            ImGui.SetNextWindowSize(new Vector2(700, 200));
            ImGui.Begin("Clipboard test");
            if (focus) ImGui.SetKeyboardFocusHere();
            ImGui.InputText("##text", ref value, 4096, password ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None);
            ImGui.End();
        }
        Assert(io.GetClipboardTextFn == get && io.SetClipboardTextFn == set && io.ClipboardUserData == userData,
            "Clipboard callbacks must be restored after drawing Buildwright");
        Assert(io.KeyMods == mods, "Shared modifier state must be restored");
        ImGui.Render();
    }
    void Key(ImGuiKey key, bool down) => io.AddKeyEvent(key, down);
    void Press(ImGuiKey key)
    {
        Key(key, true); Frame(); Key(key, false); Frame();
    }

    Frame(true); Frame(); Frame();
    Key(ImGuiKey.LeftCtrl, true);
    Press(ImGuiKey.A);
    Press(ImGuiKey.C);
    Assert(clipboard == value, "Ctrl+A / Ctrl+C must copy selected Unicode text through native InputText");
    Press(ImGuiKey.X);
    Assert(value.Length == 0 && clipboard == "中文蓝图 / Blueprint", "Ctrl+X must cut the selected text");
    clipboard = "粘贴路径 C:\\蓝图\\house.json";
    Press(ImGuiKey.V);
    Assert(value == clipboard, "Ctrl+V must paste system clipboard Unicode text");
    Key(ImGuiKey.LeftCtrl, false); Frame();
    Key(ImGuiKey.RightCtrl, true);
    Press(ImGuiKey.A);
    clipboard = "右 Ctrl 粘贴";
    Press(ImGuiKey.V);
    Assert(value == clipboard, "Right Ctrl must also support select all and paste");
    Key(ImGuiKey.RightCtrl, false); Frame();

    // A password must retain ImGui's copy protection even with the bridge active.
    value = "secret";
    Frame(true, true); Frame(password: true);
    Key(ImGuiKey.LeftCtrl, true);
    Key(ImGuiKey.A, true); Frame(password: true);
    Key(ImGuiKey.A, false); Frame(password: true);
    clipboard = "unchanged";
    Key(ImGuiKey.C, true); Frame(password: true);
    Key(ImGuiKey.C, false); Frame(password: true);
    Assert(clipboard == "unchanged", "Password inputs must not copy secret text");
    Console.WriteLine("Native ImGui clipboard smoke tests passed (Ctrl+A/C/X/V, right Ctrl, Unicode, password, shared state).");
}
finally { ImGui.DestroyContext(context); }
