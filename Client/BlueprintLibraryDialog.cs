using System.Numerics;
using ImGuiNET;
using Buildwright.Core;
using Vintagestory.API.Client;

namespace Buildwright.Client;

/// <summary>
/// Rooted local blueprint browser. File summaries refresh on deliberate actions.
/// </summary>
internal sealed class BlueprintLibraryDialog : BuildwrightImGuiDialog
{
    private const string Key = "buildwright-library";
    private const int RowsPerPage = 12;

    private readonly BuildwrightModSystem owner;
    private List<BlueprintLibraryEntry> entries = new();
    private string currentDirectory = string.Empty;
    private string query = string.Empty;
    private string entryName = string.Empty;
    private string? selectedPath;
    private string? clipboardPath;
    private string notice = string.Empty;
    private int pageIndex;
    private int sortMode;
    private bool deletePending;
    private BlueprintEditDialog? editDialog;

    protected override string WindowId => Key;
    protected override string WindowTitle => L("ui.library.title");
    protected override Vector2 DefaultSize => new(980, 650);

    public BlueprintLibraryDialog(ICoreClientAPI capi, BuildwrightModSystem owner) : base(capi)
    {
        this.owner = owner;
        RefreshEntries();
    }

    private int PageCount => Math.Max(1, (entries.Count + RowsPerPage - 1) / RowsPerPage);

    /// <summary>
    /// Refreshing a directory requires reading every blueprint summary. Keep it
    /// off the paging path: a page change only reuses this in-memory result.
    /// </summary>
    private void RefreshEntries()
    {
        entries = owner.GetBlueprintLibraryEntries(currentDirectory, query, sortMode);
        pageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        if (!entries.Any(entry => string.Equals(entry.RelativePath, selectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            selectedPath = null;
            deletePending = false;
        }
    }

    protected override void DrawContent()
    {
        Text(BuildwrightLang.Get("ui.library.path", DisplayPath()));
        if (ImGui.BeginTable("library-search", 3, ImGuiTableFlags.SizingStretchProp))
        {
            try
            {
                ImGui.TableSetupColumn("query", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("search", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("up", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##library-query", L("ui.library.search-label"), ref query, 4096, ImGuiInputTextFlags.EnterReturnsTrue)) ApplySearch();
                ImGui.TableNextColumn(); ActionButton("ui.library.search", () => ApplySearch());
                ImGui.TableNextColumn(); ActionButton("ui.library.up", () => NavigateUp(), currentDirectory.Length > 0);
            }
            finally { ImGui.EndTable(); }
        }
        DisabledText(HeaderText());
        ImGui.Separator();
        float bodyHeight = Math.Max(120, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 3);
        bool wide = ImGui.GetContentRegionAvail().X >= 720;
        bool bodyVisible = ImGui.BeginChild("library-body", new Vector2(0, bodyHeight));
        try
        {
            if (bodyVisible)
            {
                if (wide && ImGui.BeginTable("library-layout", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    try
                    {
                        ImGui.TableSetupColumn("files", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, 230);
                        ImGui.TableNextColumn(); DrawEntries(bodyHeight - 4);
                        ImGui.TableNextColumn();
                        bool actionsVisible = ImGui.BeginChild("library-actions", new Vector2(0, bodyHeight - 4));
                        try { if (actionsVisible) DrawActions(); }
                        finally { ImGui.EndChild(); }
                    }
                    finally { ImGui.EndTable(); }
                }
                else if (!wide)
                {
                    DrawEntries(Math.Max(140, bodyHeight * 0.55f));
                    ImGui.Separator();
                    DrawActions();
                }
            }
        }
        finally { ImGui.EndChild(); }
        ImGui.Separator();
        if (ImGui.BeginTable("library-paging", 3, ImGuiTableFlags.SizingStretchSame))
        {
            try
            {
                ImGui.TableNextColumn(); ActionButton("ui.library.previous", () => PreviousPage(), pageIndex > 0);
                ImGui.TableNextColumn(); Text(BuildwrightLang.Get("ui.library.page", pageIndex + 1, PageCount));
                ImGui.TableNextColumn(); ActionButton("ui.library.next", () => NextPage(), pageIndex < PageCount - 1);
            }
            finally { ImGui.EndTable(); }
        }
        Text(notice);
    }

    private void DrawEntries(float height)
    {
        bool visible = ImGui.BeginChild("library-files", new Vector2(0, height));
        try
        {
            if (!visible) return;
            if (entries.Count == 0) DisabledText(L("ui.library.empty"));
            if (!ImGui.BeginTable("library", 1, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH)) return;
            try
            {
                foreach (var entry in entries.Skip(pageIndex * RowsPerPage).Take(RowsPerPage).ToArray())
                {
                    ImGui.PushID(entry.RelativePath);
                    try
                    {
                        ImGui.TableNextRow(); ImGui.TableNextColumn();
                        bool selected = string.Equals(selectedPath, entry.RelativePath, StringComparison.OrdinalIgnoreCase);
                        // Keep file names out of the widget ID so names containing ## remain readable.
                        var start = ImGui.GetCursorPos();
                        if (ImGui.Selectable("##entry", selected, ImGuiSelectableFlags.AllowDoubleClick,
                            new Vector2(0, ImGui.GetTextLineHeight())))
                        {
                            Select(entry.RelativePath);
                            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) Activate(entry);
                        }
                        var end = ImGui.GetCursorPos();
                        ImGui.SetCursorPos(start);
                        ImGui.TextUnformatted((entry.IsDirectory ? "[DIR] " : "") + entry.Name);
                        ImGui.SetCursorPos(end);
                        DisabledText(EntryDetails(entry));
                    }
                    finally { ImGui.PopID(); }
                }
            }
            finally { ImGui.EndTable(); }
        }
        finally { ImGui.EndChild(); }
    }

    private void DrawActions()
    {
        string[] sorts = ["name", "blocks", "materials", "modified"];
        Combo("ui.library.sort", sorts.Select(s => L("ui.library.sort-" + s)).ToArray(), sortMode, i =>
        {
            sortMode = i; pageIndex = 0; SafeRefresh();
        });
        ImGui.Separator();
        Text(L("ui.library.selected"));
        var entry = SelectedEntry();
        DisabledText(entry?.Name ?? L("ui.library.none-selected"));
        bool readable = entry is { IsDirectory: false, IsUnreadable: false };
        ActionButton(entry?.IsDirectory == true ? "ui.library.open" : "ui.library.load",
            () => { if (entry != null) Activate(entry); }, entry != null && (entry.IsDirectory || readable));
        ActionButton("ui.library.export", () => ExportSelected(), readable);
        ActionButton("ui.library.export-native", () => ExportSelected(BlueprintFileKind.NativeJson), readable);
        DisabledText(L("ui.model.material-rights"));
        ActionButton("ui.library.export-model", () => ExportSelected(modelFormat: BlueprintModelFormat.ItemShape), readable);
        ActionButton("ui.library.export-blockbench", () => ExportSelected(modelFormat: BlueprintModelFormat.Blockbench), readable);
        ActionButton("ui.library.export-blender", () => ExportSelected(modelFormat: BlueprintModelFormat.BlenderGlb), readable);
        BlueprintPrintOptions.Draw();
        ActionButton("ui.library.export-print", () => ExportSelected(modelFormat: BlueprintModelFormat.PrintStl), readable);
        ActionButton("ui.library.open-exports", owner.OpenExportDirectory);
        ActionButton("ui.library.replace", () => OpenReplaceEditor(), readable);
        ImGui.Spacing(); ImGui.Separator();
        Input("ui.library.name-label", ref entryName);
        ActionGrid("library-names", 2,
            ("ui.library.new-folder", () => NewFolder(), !string.IsNullOrWhiteSpace(entryName)),
            ("ui.library.rename", () => Rename(), entry != null && !string.IsNullOrWhiteSpace(entryName)));
        ActionGrid("library-clipboard", 2,
            ("ui.library.copy", () => CopySelected(), entry != null),
            ("ui.library.paste", () => Paste(), !string.IsNullOrWhiteSpace(clipboardPath)));
        ImGui.Spacing(); ImGui.Separator();
        ActionButton(deletePending ? "ui.library.confirm" : "ui.library.delete", () => DeleteSelected(), entry != null);
    }

    private bool ApplySearch()
    {
        pageIndex = 0;
        notice = string.Empty;
        SafeRefresh();
        return true;
    }

    private bool PreviousPage()
    {
        if (pageIndex > 0) pageIndex--;
        return true;
    }

    private bool NextPage()
    {
        if (pageIndex < PageCount - 1) pageIndex++;
        return true;
    }

    private bool NavigateUp()
    {
        if (string.IsNullOrEmpty(currentDirectory)) return true;
        currentDirectory = Path.GetDirectoryName(currentDirectory.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
        selectedPath = null;
        pageIndex = 0;
        SafeRefresh();
        return true;
    }

    private bool Select(string path)
    {
        selectedPath = path;
        deletePending = false;
        notice = BuildwrightLang.Get("ui.library.selected-path", path);
        return true;
    }

    private bool Activate(BlueprintLibraryEntry entry)
    {
        selectedPath = entry.RelativePath;
        return entry.IsDirectory ? OpenSelected() : LoadSelected();
    }

    private bool OpenSelected()
    {
        var entry = SelectedEntry();
        if (entry == null)
        {
            notice = BuildwrightLang.Get("ui.library.select-first");
            SafeRefresh();
            return true;
        }
        if (!entry.IsDirectory)
        {
            notice = BuildwrightLang.Get("ui.library.open-file");
            SafeRefresh();
            return true;
        }
        currentDirectory = entry.RelativePath;
        selectedPath = null;
        pageIndex = 0;
        notice = string.Empty;
        SafeRefresh();
        return true;
    }

    private bool LoadSelected()
    {
        var entry = SelectedEntry();
        if (entry == null || entry.IsDirectory || entry.IsUnreadable)
        {
            notice = BuildwrightLang.Get("ui.library.select-file");
            SafeRefresh();
            return true;
        }
        owner.LoadBlueprintFromLibraryPath(entry.RelativePath);
        TryClose();
        return true;
    }

    private bool ExportSelected(BlueprintFileKind kind = BlueprintFileKind.BuildwrightZip, BlueprintModelFormat? modelFormat = null)
    {
        var entry = SelectedEntry();
        if (entry == null || entry.IsDirectory || entry.IsUnreadable)
        {
            notice = BuildwrightLang.Get("ui.library.select-file");
            SafeRefresh();
            return true;
        }
        notice = owner.ExportBlueprintFile(entry.RelativePath, kind, modelFormat).Message;
        SafeRefresh();
        return true;
    }

    private bool NewFolder()
    {
        var result = owner.LibraryCreateFolder(currentDirectory, entryName);
        notice = result.Message;
        if (result.Success) entryName = string.Empty;
        SafeRefresh();
        return true;
    }

    private bool CopySelected()
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) notice = BuildwrightLang.Get("ui.library.select-first");
        else
        {
            clipboardPath = selectedPath;
            notice = BuildwrightLang.Get("ui.library.copied", selectedPath);
        }
        SafeRefresh();
        return true;
    }

    private bool Paste()
    {
        if (string.IsNullOrWhiteSpace(clipboardPath)) notice = BuildwrightLang.Get("ui.library.copy-first");
        else notice = owner.LibraryCopy(clipboardPath, currentDirectory).Message;
        SafeRefresh();
        return true;
    }

    private bool Rename()
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) notice = BuildwrightLang.Get("ui.library.select-first");
        else
        {
            var result = owner.LibraryRename(selectedPath, entryName);
            notice = result.Message;
            if (result.Success) { selectedPath = null; entryName = string.Empty; }
        }
        SafeRefresh();
        return true;
    }

    private bool DeleteSelected()
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) notice = BuildwrightLang.Get("ui.library.select-first");
        else if (!deletePending)
        {
            deletePending = true;
            notice = BuildwrightLang.Get("ui.library.delete-warning");
        }
        else
        {
            var result = owner.LibraryDelete(selectedPath);
            notice = result.Message;
            if (result.Success) selectedPath = null;
            deletePending = false;
        }
        SafeRefresh();
        return true;
    }

    private bool OpenReplaceEditor()
    {
        var entry = SelectedEntry();
        if (entry == null || entry.IsDirectory || entry.IsUnreadable)
        {
            notice = BuildwrightLang.Get("ui.library.select-file");
            SafeRefresh();
            return true;
        }
        editDialog?.TryClose();
        editDialog?.Dispose();
        editDialog = new BlueprintEditDialog(capi, owner, entry.RelativePath,
            message =>
            {
                notice = message;
                SafeRefresh();
            });
        editDialog.TryOpen();
        return true;
    }

    private BlueprintLibraryEntry? SelectedEntry() => string.IsNullOrWhiteSpace(selectedPath)
        ? null
        : entries.FirstOrDefault(entry => string.Equals(entry.RelativePath, selectedPath, StringComparison.OrdinalIgnoreCase));

    private void SafeRefresh()
    {
        try { RefreshEntries(); }
        catch (Exception exception)
        {
            capi.Logger.Error("[buildwright] Failed to refresh the blueprint library: {0}", exception);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("ui.library.refresh-failed"));
        }
    }

    private string DisplayPath() => string.IsNullOrWhiteSpace(currentDirectory) ? "/" : "/" + currentDirectory;
    private string HeaderText() => BuildwrightLang.Get("ui.library.header", entries.Count, string.IsNullOrWhiteSpace(selectedPath) ? BuildwrightLang.Get("ui.library.none-selected") : selectedPath);
    private string EntryDetails(BlueprintLibraryEntry entry)
    {
        if (entry.IsDirectory) return entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        if (entry.IsUnreadable) return L("ui.library.unreadable");
        var summary = entry.Summary!;
        return BuildwrightLang.Get("ui.library.entry-details", summary.Dimensions, summary.BlockCount, summary.MaterialCount, summary.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    public override void Dispose()
    {
        editDialog?.TryClose();
        editDialog?.Dispose();
        editDialog = null;
        base.Dispose();
    }
}
