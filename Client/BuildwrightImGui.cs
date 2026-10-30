using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using VSImGui;
using VSImGui.API;

namespace Buildwright.Client;

/// <summary>One callback owns this mod's windows without changing other mods' ImGui state.</summary>
internal sealed class BuildwrightImGui : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly ImGuiModSystem renderer;
    private readonly List<BuildwrightImGuiDialog> windows = [];
    private readonly BuildwrightImGuiFont font = new();
    private readonly BuildwrightImGuiInput input;
    private bool leaving;
    private bool closing;
    private bool disposed;
    public bool PlayerControl { get; private set; }
    public bool AnyOpened => windows.Any(w => w.IsOpened());
    public bool CanOpen => !leaving && !closing && !disposed;

    public BuildwrightImGui(ICoreClientAPI api)
    {
        this.api = api;
        input = new BuildwrightImGuiInput(() => api.Input.ClipboardText, text => api.Input.ClipboardText = text,
            exception => api.Logger.Warning("[buildwright] Clipboard access failed: {0}", exception.Message));
        renderer = api.ModLoader.GetModSystem<ImGuiModSystem>();
        renderer.Draw += Draw;
        renderer.Closed += CloseAll;
        api.Event.LeaveWorld += LeaveWorld;
    }

    public void Add(BuildwrightImGuiDialog window)
    {
        if (window is BuildwrightConfirmDialog)
            foreach (var previous in windows.OfType<BuildwrightConfirmDialog>().ToArray()) previous.Dispose();
        windows.Add(window);
    }
    public void Remove(BuildwrightImGuiDialog window) => windows.Remove(window);
    public void Show() { PlayerControl = false; renderer.Show(); }
    public void SetPlayerControl(bool enabled)
    {
        PlayerControl = enabled;
        if (!enabled) renderer.Show();
    }

    private CallbackGUIStatus Draw(float dt)
    {
        if (leaving || !api.PlayerReadyFired || !AnyOpened) return CallbackGUIStatus.Closed;
        bool pushed = font.Push(api.Logger);
        try
        {
            using var inputScope = input.Use();
            using var theme = BuildwrightImGuiTheme.Use();
            // Actions can create, close or dispose other windows while drawing.
            foreach (var window in windows.ToArray())
            {
                bool confirming = windows.OfType<BuildwrightConfirmDialog>().Any(w => w.IsOpened());
                if (window.IsOpened()) window.Draw(PlayerControl || (confirming && window is not BuildwrightConfirmDialog));
            }
        }
        finally { if (pushed) ImGui.PopFont(); }
        return !AnyOpened ? CallbackGUIStatus.Closed
            : PlayerControl ? CallbackGUIStatus.DontGrabMouse : CallbackGUIStatus.GrabMouse;
    }

    private void CloseAll()
    {
        closing = true;
        try { foreach (var window in windows.ToArray()) window.TryClose(); }
        finally { PlayerControl = false; closing = false; }
    }

    private void LeaveWorld() { leaving = true; CloseAll(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        renderer.Draw -= Draw;
        renderer.Closed -= CloseAll;
        api.Event.LeaveWorld -= LeaveWorld;
        foreach (var window in windows.ToArray()) window.Dispose();
        font.Dispose();
        input.Dispose();
    }
}

internal abstract class BuildwrightImGuiDialog : IDisposable
{
    protected readonly ICoreClientAPI capi;
    protected readonly BuildwrightImGui windows;
    private bool opened;
    private bool disposed;
    private bool focusNextFrame;
    protected abstract string WindowId { get; }
    protected abstract string WindowTitle { get; }
    protected virtual Vector2 DefaultSize => new(620, 500);
    protected virtual Vector2 DefaultPosition => new(0.5f);
    protected virtual bool CanShow => true;

    protected BuildwrightImGuiDialog(ICoreClientAPI capi)
    {
        this.capi = capi;
        windows = capi.ModLoader.GetModSystem<BuildwrightModSystem>().Windows;
        windows.Add(this);
    }

    protected bool IsDisposed => disposed;
    public bool IsOpened() => opened && !disposed;
    public bool TryOpen()
    {
        if (disposed || !windows.CanOpen || !CanShow) return false;
        if (!opened) { opened = true; focusNextFrame = true; OnGuiOpened(); }
        windows.Show();
        return true;
    }

    public bool TryClose()
    {
        if (opened) { opened = false; OnGuiClosed(); }
        return true;
    }

    public virtual void OnGuiOpened() { }
    public virtual void OnGuiClosed() { }

    internal void Draw(bool playerControl)
    {
        if (!CanShow) { TryClose(); return; }
        var viewport = ImGui.GetMainViewport();
        var size = Vector2.Min(DefaultSize, Vector2.Max(new(240, 160), viewport.Size - new Vector2(24)));
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * DefaultPosition, ImGuiCond.FirstUseEver, DefaultPosition);
        ImGui.SetNextWindowSizeConstraints(Vector2.Min(new(300, 180), viewport.Size), viewport.Size);
        if (focusNextFrame) { ImGui.SetNextWindowFocus(); focusNextFrame = false; }
        bool keepOpen = opened;
        var flags = ImGuiWindowFlags.NoDocking;
        if (playerControl) flags |= ImGuiWindowFlags.NoInputs;
        bool visible = ImGui.Begin(WindowTitle + "###" + WindowId, ref keepOpen, flags);
        try { if (visible) DrawContent(); }
        finally { ImGui.End(); }
        if (!keepOpen) TryClose();
    }

    protected abstract void DrawContent();
    protected static string L(string key) => BuildwrightLang.Get(key);
    protected static void Text(string text)
    {
        ImGui.PushTextWrapPos(0);
        try { ImGui.TextUnformatted(text); }
        finally { ImGui.PopTextWrapPos(); }
    }
    protected static void DisabledText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        try { Text(text); }
        finally { ImGui.PopStyleColor(); }
    }
    protected static void Button(string key, Action action)
    {
        if (ImGui.Button(L(key))) action();
    }
    protected static void ActionButton(string key, Action action, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        try { if (ImGui.Button(L(key), new Vector2(-1, 0))) action(); }
        finally { ImGui.EndDisabled(); }
    }
    protected static void ActionGrid(string id, int columns, params (string Key, Action Action, bool Enabled)[] actions)
    {
        if (!ImGui.BeginTable(id, columns, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            foreach (var action in actions)
            {
                ImGui.TableNextColumn();
                ActionButton(action.Key, action.Action, action.Enabled);
            }
        }
        finally { ImGui.EndTable(); }
    }
    protected static void Input(string key, ref string value, bool password = false)
    {
        ImGui.TextUnformatted(L(key));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##" + key, ref value, 4096, password ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None);
    }
    protected static void Combo(string key, string[] names, int selected, Action<int> changed)
    {
        ImGui.TextUnformatted(L(key));
        ImGui.SetNextItemWidth(-1);
        selected = Math.Clamp(selected, 0, Math.Max(0, names.Length - 1));
        if (names.Length > 0 && ImGui.Combo("##" + key, ref selected, names, names.Length)) changed(selected);
    }
    protected void Confirm(string text, Action action) => new BuildwrightConfirmDialog(capi, text, action).TryOpen();

    public virtual void Dispose()
    {
        if (disposed) return;
        TryClose();
        disposed = true;
        windows.Remove(this);
    }
}

internal sealed class BuildwrightConfirmDialog(ICoreClientAPI api, string text, Action accepted) : BuildwrightImGuiDialog(api)
{
    protected override string WindowId => "buildwright-confirm";
    protected override string WindowTitle => L("ui.library.confirm");
    protected override Vector2 DefaultSize => new(480, 220);
    protected override void DrawContent()
    {
        Text(text);
        ImGui.Separator();
        Button("ui.library.confirm", () => { TryClose(); accepted(); });
        ImGui.SameLine();
        Button("ui.library.edit-cancel", () => TryClose());
    }
    public override void OnGuiClosed() => windows.Remove(this);
}
