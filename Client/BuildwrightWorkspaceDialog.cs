using System.Numerics;
using Buildwright.Core;
using ImGuiNET;
using Vintagestory.API.Client;

namespace Buildwright.Client;

internal enum BuildwrightWorkspacePage { Dashboard, Selection, Blueprints, Projection, Build, Region }

internal sealed class BuildwrightWorkspaceDialog : BuildwrightImGuiDialog
{
    private readonly BuildwrightModSystem owner;
    private readonly BuildwrightWorkspacePage page;
    private string blueprintName = "", targetName = "", importPath = "";
    private string opacity = "0.32";
    private int moveStep = 1, resizeAmount = 1, growthDirection, regionTurns, regionMirror;
    private int cloneX, cloneY, cloneZ;
    private static readonly string[] Directions = ["up", "down", "north", "south", "east", "west"];
    protected override string WindowId => "buildwright-workbench-" + page.ToString().ToLowerInvariant();
    protected override bool CanShow => page != BuildwrightWorkspacePage.Region || owner.IsCreativeMode;
    protected override string WindowTitle => page == BuildwrightWorkspacePage.Dashboard ? L("ui.workbench.title")
        : L("ui.workbench." + page.ToString().ToLowerInvariant() + "-title");
    protected override Vector2 DefaultSize => page == BuildwrightWorkspacePage.Dashboard ? new(310, 470) : new(410, 640);
    protected override Vector2 DefaultPosition => page switch
    {
        BuildwrightWorkspacePage.Dashboard => new(0.02f, 0.15f),
        BuildwrightWorkspacePage.Region => new(0.88f, 0.28f),
        _ => new(0.43f, 0.22f)
    };

    public BuildwrightWorkspaceDialog(ICoreClientAPI api, BuildwrightModSystem owner, BuildwrightWorkspacePage page) : base(api)
    { this.owner = owner; this.page = page; }

    private void Action(string key, Action action)
    { if (ImGui.Button(L("ui.workbench." + key), new Vector2(-1, 0))) action(); }
    private void Field(string key, ref string value) => Input("ui.workbench." + key, ref value);
    private void Number(string key, ref int value, int minimum = int.MinValue)
    {
        ImGui.TextUnformatted(L("ui.workbench." + key)); ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("##" + key, ref value)) value = Math.Max(minimum, value);
    }
    private void Select(string key, string[] names, int selected, Action<int> changed) => Combo("ui.workbench." + key, names, selected, changed);
    private void ConfirmAction(string key, string message, Action action) => Action(key, () => Confirm(L("ui.workbench." + message), action));
    private void Open(BuildwrightWorkspacePage target) => owner.WorkspaceOpenTool(target);

    protected override void DrawContent()
    {
        var state = owner.GetWorkspaceState();
        Text(state.BlueprintName == null ? L("ui.workbench.blueprint-empty") : BuildwrightLang.Get("ui.workbench.blueprint-state", state.BlueprintName));
        ImGui.Separator();
        switch (page)
        {
            case BuildwrightWorkspacePage.Dashboard:
                Action("dock.selection", () => Open(BuildwrightWorkspacePage.Selection));
                Action("dock.library", owner.WorkspaceOpenLibrary);
                Action("blueprints-title", () => Open(BuildwrightWorkspacePage.Blueprints));
                Action("dock.projection", () => Open(BuildwrightWorkspacePage.Projection));
                Action("dock.build", () => Open(BuildwrightWorkspacePage.Build));
                if (owner.IsCreativeMode) Action("region-title", () => Open(BuildwrightWorkspacePage.Region));
                ImGui.Separator();
                Action("cancel", owner.WorkspaceCancel);
                break;
            case BuildwrightWorkspacePage.Selection:
                if (state.HasSelection) Text($"{state.Dimensions} 路 {state.SelectionCells:N0}");
                Action("corner1", owner.WorkspaceSetCorner1);
                Action("corner2", owner.WorkspaceSetCorner2);
                Action("clear", owner.WorkspaceClearSelection);
                Field("blueprint-name", ref blueprintName);
                Action("save", () => owner.WorkspaceSaveBlueprint(blueprintName));
                ImGui.Separator();
                Select("direction", Directions.Select(d => L("ui.workbench.direction." + d)).ToArray(), growthDirection, i => growthDirection = i);
                Number("amount", ref resizeAmount, 1);
                Action("grow", () => owner.WorkspaceResize(Directions[growthDirection], resizeAmount, true));
                Action("shrink", () => owner.WorkspaceResize(Directions[growthDirection], resizeAmount, false));
                break;
            case BuildwrightWorkspacePage.Blueprints:
                Field("blueprint-name", ref blueprintName);
                Action("load-preview", () => owner.WorkspaceLoadBlueprint(blueprintName));
                Action("open-library", owner.WorkspaceOpenLibrary);
                Field("import-path", ref importPath);
                Action("import", () => owner.WorkspaceImportBlueprint(importPath, blueprintName));
                Text(L("ui.workbench.export-hint"));
                Action("export", () => owner.WorkspaceExportBlueprint(blueprintName));
                DisabledText(L("ui.model.material-rights"));
                ActionButton("ui.library.export-model", () => owner.WorkspaceExportBlueprint(blueprintName, modelFormat: BlueprintModelFormat.ItemShape));
                ActionButton("ui.library.export-blockbench", () => owner.WorkspaceExportBlueprint(blueprintName, modelFormat: BlueprintModelFormat.Blockbench));
                ActionButton("ui.library.export-blender", () => owner.WorkspaceExportBlueprint(blueprintName, modelFormat: BlueprintModelFormat.BlenderGlb));
                BlueprintPrintOptions.Draw();
                ActionButton("ui.library.export-print", () => owner.WorkspaceExportBlueprint(blueprintName, modelFormat: BlueprintModelFormat.PrintStl));
                ActionButton("ui.library.open-exports", owner.OpenExportDirectory);
                Field("copy-name", ref targetName);
                Action("duplicate", () => owner.WorkspaceDuplicateBlueprint(blueprintName, targetName, false));
                Action("template", () => owner.WorkspaceDuplicateBlueprint(blueprintName, targetName, true));
                break;
            case BuildwrightWorkspacePage.Projection:
                Action("lock-toggle", owner.WorkspaceTogglePlacement);
                Action("rotate-left", () => owner.WorkspaceRotate(-1));
                Action("rotate-right", () => owner.WorkspaceRotate(1));
                Action("rotate-180", () => owner.WorkspaceRotate(2));
                Select("mirror", [L("ui.workbench.mirror-none"), "X", "Y", "Z"], (int)state.Mirror, i => owner.WorkspaceSetMirror((MirrorAxis)i));
                Field("opacity", ref opacity);
                Action("apply", () => owner.WorkspaceSetOpacity(opacity));
                string[] modes = ["full", "missing", "wrong", "difference"];
                Select("display", modes.Select(m => L("ui.workbench.display." + m)).ToArray(), state.DisplayMode == GhostDisplayMode.Layer ? 0 : (int)state.DisplayMode, i => owner.WorkspaceSetDisplay(modes[i]));
                Number("step", ref moveStep, 1);
                Action("move.forward", () => owner.WorkspaceMoveRelative(moveStep, 0, 0));
                Action("move.back", () => owner.WorkspaceMoveRelative(-moveStep, 0, 0));
                Action("move.left", () => owner.WorkspaceMoveRelative(0, -moveStep, 0));
                Action("move.right", () => owner.WorkspaceMoveRelative(0, moveStep, 0));
                Action("move.up", () => owner.WorkspaceMoveRelative(0, 0, moveStep));
                Action("move.down", () => owner.WorkspaceMoveRelative(0, 0, -moveStep));
                break;
            case BuildwrightWorkspacePage.Build:
                string[] matching = ["loose", "medium", "strict"];
                Select("matching", matching.Select(m => L("ui.workbench.matching." + m)).ToArray(), (int)state.MatchingMode, i => owner.WorkspaceSetMatchingMode(matching[i]));
                if (state.HasTracker) ImGui.ProgressBar((float)state.Progress, new Vector2(-1, 0));
                Action("dock.diff", owner.WorkspaceOpenDiff);
                Action("dock.materials", owner.WorkspaceOpenMaterials);
                Action("toggle-hud", owner.WorkspaceToggleHud);
                Action("layer-down", () => owner.WorkspaceAdjustLayer(-1));
                Action("layer-up", () => owner.WorkspaceAdjustLayer(1));
                if (owner.IsCreativeMode)
                {
                    ImGui.Separator(); Text(L("ui.workbench.creative-note"));
                    ConfirmAction("place-world", "confirm-place", () => owner.WorkspacePlace(false, false));
                    ConfirmAction("fill-world", "confirm-fill", () => owner.WorkspacePlace(true, false));
                    ConfirmAction("clear-extra", "confirm-clear-extra", () => owner.WorkspacePlace(false, true));
                }
                break;
            case BuildwrightWorkspacePage.Region:
                Number("offset-x", ref cloneX); Number("offset-y", ref cloneY); Number("offset-z", ref cloneZ);
                ConfirmAction("clone", "confirm-clone", () => owner.WorkspaceCloneOrMoveRegion(false, cloneX, cloneY, cloneZ));
                ConfirmAction("move-region", "confirm-move-region", () => owner.WorkspaceCloneOrMoveRegion(true, cloneX, cloneY, cloneZ));
                Select("rotation", ["90掳", "180掳", "270掳"], regionTurns, i => regionTurns = i);
                ConfirmAction("rotate-region", "confirm-rotate-region", () => owner.WorkspaceRotateRegion(regionTurns + 1));
                Select("mirror", ["X", "Y", "Z"], regionMirror, i => regionMirror = i);
                ConfirmAction("mirror-region", "confirm-mirror-region", () => owner.WorkspaceMirrorRegion((MirrorAxis)(regionMirror + 1)));
                ConfirmAction("clear-region", "confirm-clear-region", owner.WorkspaceClearRegion);
                break;
        }
    }
}

internal sealed class BuildwrightWorkspaceState
{
    public bool HasSelection { get; init; }
    public Int3 Dimensions { get; init; } = new();
    public long SelectionCells { get; init; }
    public string? BlueprintName { get; init; }
    public bool HasGhost { get; init; }
    public bool GhostLocked { get; init; }
    public int RotationDegrees { get; init; }
    public MirrorAxis Mirror { get; init; }
    public GhostDisplayMode DisplayMode { get; init; }
    public MatchingMode MatchingMode { get; init; }
    public bool HasTracker { get; init; }
    public double Progress { get; init; }
    public int Missing { get; init; }
    public int Wrong { get; init; }
    public int Extra { get; init; }
}
