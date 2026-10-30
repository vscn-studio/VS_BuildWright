using Buildwright.Core;
using Buildwright.Api;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Buildwright.Client;

public sealed class BuildwrightModSystem : ModSystem
{
    private const int SelectionHighlightSlot = 8611;
    private const int SelectionAnchorHighlightSlot = 8612;
    private const int SelectionSelectedCornerHighlightSlot = 8613;
    private const int MoveTargetHighlightSlot = 8614;
    private const int MoveTargetAnchorHighlightSlot = 8615;
    private const int SelectionCorner2HighlightSlot = 8617;
    private const int MaxHighlightCells = 32768;
    private const int MaxGridPlacements = 512;

    internal BuildwrightImGui Windows { get; private set; } = null!;
    internal bool IsCreativeMode => capi?.World.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative;
    private ICoreClientAPI? capi;
    private readonly SelectionState selection = new();
    private BuildwrightConfig config = new();
    private BlueprintRepository? repository;
    private WorldOperationService? worldOperations;
    private BuildwrightWorkspaceDialog? workspaceDialog;
    private readonly Dictionary<BuildwrightWorkspacePage, BuildwrightWorkspaceDialog> toolDialogs = new();
    private BuildwrightBuildInfoDialog? materialsDialog;
    private BuildwrightBuildInfoDialog? diffDialog;
    private BlueprintLibraryDialog? libraryDialog;
    private BuildwrightGhostMesh? activeMesh;
    private BuildwrightGhostRenderer? activeRenderer;
    private BuildwrightTracker? activeTracker;
    private BuildwrightHud? activeHud;
    private BuildwrightControlHud? controlHud;
    private BuildwrightSelectionRenderer? selectionRenderer;
    private BlockSchematic? activeSchematic;
    private string? activeName;
    private MatchingMode activeMode;
    private Int3 activeAnchorOffset = new();
    private BlueprintDocument? activeDocument;
    private GhostDisplayMode displayMode = GhostDisplayMode.Full;
    private char? clipAxis;
    private int clipMin;
    private int clipMax;
    private long tickListener = -1;
    private long controlHudTickListener = -1;
    private long lastScan;
    private bool progressHudHidden;
    private MaterialCounter.SortMode materialSort = MaterialCounter.SortMode.Name;
    private bool completionReported;
    private AssetLocation? toolPrimaryBlock;
    private AssetLocation? toolSecondaryBlock;
    private BlockPos? toolMoveTarget;
    private BlueprintDocument? toolMoveDocument;
    private BuildwrightGhostRenderer? toolMoveRenderer;
    private BlockPos? gridStart;
    private BlockPos? gridEnd;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        Windows = new BuildwrightImGui(api);
        config = BuildwrightConfig.Load(api);
        if (!Enum.IsDefined(typeof(BuildwrightToolMode), config.ToolMode)) config.ToolMode = BuildwrightToolMode.AreaSelection;
        activeMode = config.DefaultMatchingMode;
        repository = new BlueprintRepository(Path.Combine(GamePaths.DataPath, "Buildwright", "Blueprints"));
        worldOperations = new WorldOperationService(api);
        RegisterHotkeys(api);
        RegisterCommands(api);
        api.Input.InWorldAction += OnInWorldAction;
        api.Event.MouseDown += OnMouseDown;
        api.Event.MouseWheelMove += OnMouseWheelMove;
        controlHud = new BuildwrightControlHud(api, GetControlHudSnapshot);
        selectionRenderer = new BuildwrightSelectionRenderer(api);
        if (ShouldShowControlHud()) controlHud.TryOpen();
        controlHudTickListener = api.Event.RegisterGameTickListener(OnControlHudTick, 100);
        api.Logger.Notification("[buildwright] Buildwright 1.0.3 ready (client)");
    }

    private void OnControlHudTick(float dt)
    {
        if (!IsCreativeMode) CloseRegionOperationsWindow();
        var showControls = ShouldShowControlHud();
        if (controlHud != null)
        {
            if (showControls)
            {
                if (!controlHud.IsOpened()) controlHud.TryOpen();
                controlHud.Refresh();
            }
            else if (controlHud.IsOpened())
            {
                controlHud.TryClose();
            }
        }
        if (activeHud != null && !progressHudHidden && !activeHud.IsOpened()) activeHud.TryOpen();
        activeHud?.Refresh();
    }

    private void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handled)
    {
        if (!on || !IsToolInputActive()) return;

        if (action == EnumEntityAction.InWorldLeftMouseDown)
        {
            HandleToolPrimary();
            handled = EnumHandling.PreventDefault;
        }
        else if (action == EnumEntityAction.InWorldRightMouseDown)
        {
            HandleToolSecondary();
            handled = EnumHandling.PreventDefault;
        }
    }

    private void OnMouseDown(MouseEvent args)
    {
        if (args.Handled || args.Button != EnumMouseButton.Middle || !IsToolInputActive()) return;
        HandleToolSelect();
        args.Handled = true;
    }

    private void OnMouseWheelMove(MouseWheelEventArgs args)
    {
        // Let the progress HUD's material list consume the wheel before the
        // stick tool mode/selection handler sees it.
        if (activeHud?.IsMouseOverMaterials() == true) return;
        if (args.IsHandled || !config.ToolItemEnabled || !IsProjectionStickHeld()) return;
        var amount = args.delta != 0 ? args.delta : Math.Sign(args.deltaPrecise);
        if (amount == 0) return;

        // Alt+wheel adjusts the selection along the closest view direction.
        // Alt takes precedence over Ctrl's tool-mode cycling so Alt+Ctrl can
        // move the whole selection without also changing modes.
        if (IsAltHeld() && config.ToolMode.UsesSelection())
        {
            if (config.ToolMode == BuildwrightToolMode.Move && toolMoveTarget != null && !IsCtrlHeld())
            {
                NudgeMoveTarget(amount);
            }
            else
            {
                AdjustSelectionFromScroll(amount);
            }
            args.SetHandled();
            return;
        }

        if (!IsCtrlHeld()) return;
        CycleToolMode(forward: amount < 0);
        args.SetHandled();
    }

    private void HandleToolPrimary()
    {
        if (config.ToolMode.UsesSelection()) SetCorner(1);
        else if (config.ToolMode == BuildwrightToolMode.GridPaste) SetGridAnchor(first: true);
        else PositionProjectionWithTool();
    }

    private void HandleToolSecondary()
    {
        if (config.ToolMode.UsesSelection()) SetCorner(2);
        else if (config.ToolMode == BuildwrightToolMode.GridPaste) SetGridAnchor(first: false);
        else PositionProjectionWithTool();
    }

    private void HandleToolSelect()
    {
        switch (config.ToolMode)
        {
            case BuildwrightToolMode.Fill when IsCtrlHeld():
                SelectToolBlock(primary: true);
                break;
            case BuildwrightToolMode.ReplaceBlock when IsCtrlHeld() && IsShiftHeld():
                SelectToolBlock(primary: false);
                break;
            case BuildwrightToolMode.ReplaceBlock when IsCtrlHeld():
                SelectToolBlock(primary: true);
                break;
            case BuildwrightToolMode.Move:
                SetMoveTarget();
                break;
            case var mode when mode.UsesSelection():
                SelectNextSelectionCorner();
                break;
            case BuildwrightToolMode.SchematicPlacement or BuildwrightToolMode.PasteSchematic or BuildwrightToolMode.GridPaste or BuildwrightToolMode.Rebuild:
                capi?.ShowChatMessage("[Buildwright] " + (activeRenderer == null
                    ? BuildwrightLang.Get("chat.no-active-ghost")
                    : BuildwrightLang.Get("chat.tool-projection-selected", activeName ?? string.Empty)));
                break;
            default:
                Status();
                break;
        }
    }

    private void PositionProjectionWithTool()
    {
        if (capi == null || activeRenderer == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
            return;
        }
        if (!activeRenderer.SnapToTarget())
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.aim-block"));
            return;
        }
        if (activeRenderer.IsFloating) activeRenderer.Place();
        BuildTracker();
        capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-positioned"));
    }

    private void SetGridAnchor(bool first)
    {
        var target = GetTargetAnchor();
        if (target == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.aim-block"));
            return;
        }
        if (first) gridStart = target; else gridEnd = target;
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get(first ? "chat.tool-grid-start" : "chat.tool-grid-end", target));
    }

    private void SetMoveTarget()
    {
        var target = capi?.World.Player?.CurrentBlockSelection?.Position;
        if (target == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.aim-block"));
            return;
        }
        if (!selection.HasSelection)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-requires-selection"));
            return;
        }
        if (toolMoveRenderer is { IsFloating: true }) toolMoveRenderer.Place();
        toolMoveTarget = target.Copy();
        if (!RebuildMovePreview())
        {
            ClearMovePreview();
            return;
        }
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-move-target", toolMoveTarget));
    }

    private void NudgeMoveTarget(int amount)
    {
        if (toolMoveTarget == null || amount == 0) return;
        if (toolMoveRenderer is { IsFloating: true })
        {
            toolMoveRenderer.Place();
            toolMoveTarget = toolMoveRenderer.Origin.Copy();
        }
        var normal = ClosestLookingFacing().Normali;
        toolMoveTarget.X += normal.X * amount;
        toolMoveTarget.Y += normal.Y * amount;
        toolMoveTarget.Z += normal.Z * amount;
        toolMoveRenderer?.Move(normal.X * amount, normal.Y * amount, normal.Z * amount);
        UpdateHighlights();
    }

    private bool RebuildMovePreview()
    {
        if (capi == null || toolMoveTarget == null || !selection.HasSelection) return false;
        var oldRotation = toolMoveRenderer?.RotationQuarterTurns ?? 0;
        var oldMirror = toolMoveRenderer?.Mirror ?? MirrorAxis.None;
        var oldFloating = toolMoveRenderer?.IsFloating == true;
        if (oldFloating) toolMoveTarget = toolMoveRenderer!.Origin.Copy();
        try
        {
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            var schematic = ParseSchematic(document);
            var entityClasses = document.BlockEntities.ToDictionary(
                record => RegionCapture.Pack(record.Position.X, record.Position.Y, record.Position.Z),
                record => record.EntityClass ?? string.Empty);
            var compatData = document.BlockEntities.ToDictionary(
                record => RegionCapture.Pack(record.Position.X, record.Position.Y, record.Position.Z),
                record => (IReadOnlyList<BuildwrightCompatPayload>)record.CompatData);
            var terrain = BlueprintTerrainProjection.TryCreate(capi, schematic, toolMoveTarget);
            var beamGuides = BuildwrightSupportBeamGuideMesh.TryCreate(capi, document, schematic.SizeY);
            var mesh = new BuildwrightGhostMesh(capi, schematic, Math.Clamp(config.GhostAlpha, 0.05f, 1f),
                chiselData: document.ChiselData, entityClasses: entityClasses, compatData: compatData,
                excludeBlock: terrain == null ? null : BlueprintTerrainProjection.RequiresTerrainPipeline,
                environmentOrigin: toolMoveTarget);
            var renderer = new BuildwrightGhostRenderer(capi, mesh, toolMoveTarget,
                document.Metadata.AnchorOffset, ResolveAnchorFace(document), config.RenderDistanceBlocks, terrain,
                beamGuides, activeMode);
            renderer.SetRotation(oldRotation);
            renderer.SetMirror(oldMirror);
            if (!oldFloating) renderer.Place();
            toolMoveRenderer?.Dispose();
            toolMoveDocument = document;
            toolMoveRenderer = renderer;
            UpdateHighlights();
            return true;
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[buildwright] Failed to build move preview: {0}", exception);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-operation-failed", exception.Message));
            return false;
        }
    }

    private void ClearMovePreview(bool clearTarget = true)
    {
        toolMoveRenderer?.Dispose();
        toolMoveRenderer = null;
        toolMoveDocument = null;
        if (clearTarget) toolMoveTarget = null;
        ClearMoveTargetHighlights();
    }

    private void SelectToolBlock(bool primary)
    {
        var target = capi?.World.Player?.CurrentBlockSelection?.Position;
        var block = target == null ? null : capi?.World.BlockAccessor.GetBlock(target);
        if (block == null || block.Id == 0)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.aim-block"));
            return;
        }
        if (primary) toolPrimaryBlock = block.Code; else toolSecondaryBlock = block.Code;
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get(primary ? "chat.tool-primary-block" : "chat.tool-secondary-block", block.Code));
    }

    private BlockPos? GetTargetAnchor()
    {
        var hit = capi?.World.Player?.CurrentBlockSelection;
        if (hit?.Position == null) return null;
        var normal = hit.Face?.Normali;
        return new BlockPos(hit.Position.X + (normal?.X ?? 0), hit.Position.Y + (normal?.Y ?? 0),
            hit.Position.Z + (normal?.Z ?? 0), hit.Position.dimension);
    }

    private bool IsProjectionStickHeld()
    {
        var manager = capi?.World.Player?.InventoryManager;
        var stacks = new[] { manager?.ActiveHotbarSlot?.Itemstack, manager?.OffhandHotbarSlot?.Itemstack };
        return stacks.Any(stack => stack != null &&
            (stack.Id == 2644 || string.Equals(stack.Collectible?.Code?.ToString(), "game:stick", StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsToolInputActive() => config.ToolItemEnabled && !config.ToolMode.IsDisabled() &&
        IsProjectionStickHeld() && capi?.Input.MouseGrabbed == true;

    private bool ShouldShowControlHud() => config.ToolItemEnabled && !config.ToolMode.IsDisabled() && IsProjectionStickHeld();

    private bool IsCtrlHeld() => IsKeyHeld(GlKeys.LControl, GlKeys.RControl);
    private bool IsAltHeld() => IsKeyHeld(GlKeys.LAlt, GlKeys.RAlt);
    private bool IsShiftHeld() => IsKeyHeld(GlKeys.LShift, GlKeys.RShift);

    private bool IsKeyHeld(GlKeys first, GlKeys second)
    {
        var state = capi?.Input.KeyboardKeyStateRaw;
        return state != null && (((int)first < state.Length && state[(int)first]) || ((int)second < state.Length && state[(int)second]));
    }

    private void AdjustSelectionFromScroll(int amount)
    {
        if (!selection.HasSelection) return;

        var facing = ClosestLookingFacing();
        if (IsCtrlHeld()) selection.Nudge(facing, amount);
        else selection.NudgeSelectedCorner(facing, amount);
        if (config.ToolMode == BuildwrightToolMode.Move && toolMoveTarget != null) RebuildMovePreview();
        else UpdateHighlights();
    }

    private void SelectNextSelectionCorner()
    {
        if (!selection.HasSelection)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-requires-selection"));
            return;
        }

        var corner = selection.SelectNextCorner();
        UpdateHighlights();
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.selection-corner-selected", corner));
    }

    private BlockFacing ClosestLookingFacing()
    {
        var player = capi?.World.Player;
        if (player == null) return BlockFacing.NORTH;

        // CameraPitch is wrapped in EntityPos, so comparing it against a
        // signed +/- threshold misclassifies most views as upward. The view
        // vector is already normalized in world coordinates and BlockFacing
        // selects the closest of the six cardinal directions.
        var view = player.Entity.Pos.GetViewVector();
        return BlockFacing.FromVector(view) ?? BlockFacing.NORTH;
    }

    private BuildwrightControlHudSnapshot GetControlHudSnapshot()
    {
        var lines = new List<BuildwrightControlHudLine>
        {
            BuildwrightControlHudLine.Title(BuildwrightLang.Get("ui.controls.title")),
            BuildwrightControlHudLine.Normal(
                new BuildwrightControlHudSegment(BuildwrightLang.Get("ui.controls.mode-prefix", (int)config.ToolMode + 1, BuildwrightToolModes.Count), BuildwrightControlHudColors.Blue),
                new BuildwrightControlHudSegment(BuildwrightLang.Get(config.ToolMode.LanguageKey()), BuildwrightControlHudColors.Green)),
            BuildwrightControlHudLine.Normal(new BuildwrightControlHudSegment(ToolHelpText(), BuildwrightControlHudColors.Text))
        };

        if (config.ToolMode.UsesSelection())
        {
            var dimensions = selection.Dimensions;
            lines.Add(selection.HasSelection
                ? BuildwrightControlHudLine.Normal(
                    new BuildwrightControlHudSegment(BuildwrightLang.Get("ui.controls.selection-prefix"), BuildwrightControlHudColors.Blue),
                    new BuildwrightControlHudSegment(BuildwrightLang.Get("ui.controls.selection-size", dimensions.x, dimensions.y, dimensions.z,
                        (long)dimensions.x * dimensions.y * dimensions.z), BuildwrightControlHudColors.Green))
                : BuildwrightControlHudLine.Normal(new BuildwrightControlHudSegment(BuildwrightLang.Get("ui.controls.selection-empty"), BuildwrightControlHudColors.Red)));
            if (selection.HasSelection)
            {
                lines.Add(BuildwrightControlHudLine.Normal(
                    new BuildwrightControlHudSegment(BuildwrightLang.Get("ui.controls.selection-corner-prefix"), BuildwrightControlHudColors.Blue),
                    new BuildwrightControlHudSegment(BuildwrightLang.Get("ui.controls.selection-corner", selection.SelectedCorner,
                        HotkeyLabel("buildwright-selection-corner", "Ctrl+Shift+C")), BuildwrightControlHudColors.Green)));
            }
            lines.Add(BuildwrightControlHudLine.Normal(new BuildwrightControlHudSegment(
                BuildwrightLang.Get(config.ToolMode == BuildwrightToolMode.Move && toolMoveTarget != null
                    ? "ui.controls.adjust-move-target"
                    : "ui.controls.adjust-corner"), BuildwrightControlHudColors.Blue)));
            if (config.ToolMode == BuildwrightToolMode.Move && toolMoveTarget != null)
                lines.Add(BuildwrightControlHudLine.Normal(new BuildwrightControlHudSegment(
                    BuildwrightLang.Get("ui.controls.move-transform",
                        HotkeyLabel("buildwright-rotate", "Ctrl+Shift+R"),
                        HotkeyLabel("buildwright-mirror", "Ctrl+Shift+M")), BuildwrightControlHudColors.Blue)));
            lines.Add(BuildwrightControlHudLine.Normal(new BuildwrightControlHudSegment(
                BuildwrightLang.Get("ui.controls.adjust-nudge"), BuildwrightControlHudColors.Blue)));
        }
        else
        {
            lines.Add(BuildwrightControlHudLine.Normal(new BuildwrightControlHudSegment(ToolContextText(), activeRenderer == null
                ? BuildwrightControlHudColors.Red
                : BuildwrightControlHudColors.Green)));
        }

        var stickHeld = config.ToolItemEnabled && IsProjectionStickHeld();
        lines.Add(BuildwrightControlHudLine.Normal(
            new BuildwrightControlHudSegment(BuildwrightLang.Get("ui.controls.mode-cycle-short",
                HotkeyLabel("buildwright-tool-mode-previous", "Ctrl+Shift+[") + " / " +
                HotkeyLabel("buildwright-tool-mode-next", "Ctrl+Shift+]") ), BuildwrightControlHudColors.Blue),
            new BuildwrightControlHudSegment(BuildwrightLang.Get("ui.controls.stick-prefix"), BuildwrightControlHudColors.Text),
            new BuildwrightControlHudSegment(BuildwrightLang.Get(stickHeld ? "ui.controls.stick-ready" : "ui.controls.stick-missing"),
                stickHeld ? BuildwrightControlHudColors.Green : BuildwrightControlHudColors.Red)));
        return new BuildwrightControlHudSnapshot(lines);
    }

    private string ToolContextText() => config.ToolMode switch
    {
        BuildwrightToolMode.Fill => BuildwrightLang.Get("ui.tool.context.fill", ToolCode(toolPrimaryBlock)),
        BuildwrightToolMode.ReplaceBlock => BuildwrightLang.Get("ui.tool.context.replace", ToolCode(toolPrimaryBlock), ToolCode(toolSecondaryBlock)),
        BuildwrightToolMode.GridPaste => BuildwrightLang.Get("ui.tool.context.grid", GridPoint(gridStart), GridPoint(gridEnd)),
        BuildwrightToolMode.Move => BuildwrightLang.Get("ui.tool.context.move-transform", GridPoint(toolMoveTarget),
            (toolMoveRenderer?.RotationQuarterTurns ?? 0) * 90, toolMoveRenderer?.Mirror ?? MirrorAxis.None),
        _ => activeRenderer == null
            ? BuildwrightLang.Get("ui.controls.none")
            : BuildwrightLang.Get(activeRenderer.IsFloating ? "ui.controls.following" : "ui.controls.locked")
    };

    private static string ToolCode(AssetLocation? code) => code?.ToString() ?? "-";
    private static string GridPoint(BlockPos? pos) => pos == null ? "-" : $"{pos.X}, {pos.Y}, {pos.Z}";

    private string ToolHelpText()
    {
        return config.ToolMode switch
        {
            BuildwrightToolMode.Fill => BuildwrightLang.Get("ui.tool.help.fill"),
            BuildwrightToolMode.ReplaceBlock => BuildwrightLang.Get("ui.tool.help.replace-block"),
            _ => BuildwrightLang.Get(config.ToolMode.HelpLanguageKey())
        };
    }

    private string HotkeyLabel(string code, string fallback)
    {
        try
        {
            var hotkey = capi?.Input.GetHotKeyByCode(code);
            var mapping = hotkey?.CurrentMapping.ToString();
            return string.IsNullOrWhiteSpace(mapping) ? fallback : mapping;
        }
        catch { return fallback; }
    }

    private void RegisterHotkeys(ICoreClientAPI api)
    {
        Register(api, "buildwright-place", BuildwrightLang.Get("hotkey.place"), GlKeys.P, TogglePlace, true, true);
        Register(api, "buildwright-mirror", BuildwrightLang.Get("hotkey.mirror"), GlKeys.M, CycleMirror, true, true);
        Register(api, "buildwright-library", BuildwrightLang.Get("hotkey.workbench"), GlKeys.B, OpenWorkspace);
        Register(api, "buildwright-player-control", BuildwrightLang.Get("hotkey.player-control"), GlKeys.V, TogglePlayerControl);
        Register(api, "buildwright-cancel", BuildwrightLang.Get("hotkey.cancel"), GlKeys.X, Cancel, true, true);
        Register(api, "buildwright-layer-down", BuildwrightLang.Get("hotkey.layer-down"), GlKeys.PageDown, () => AdjustLayer(-1));
        Register(api, "buildwright-layer-up", BuildwrightLang.Get("hotkey.layer-up"), GlKeys.PageUp, () => AdjustLayer(1));
        Register(api, "buildwright-checklist", BuildwrightLang.Get("hotkey.checklist"), GlKeys.L, ToggleHud, true, true);
        Register(api, "buildwright-rotate", BuildwrightLang.Get("hotkey.rotate"), GlKeys.R, () => WorkspaceRotate(1), true, true);
        Register(api, "buildwright-display", BuildwrightLang.Get("hotkey.display"), GlKeys.G, WorkspaceCycleDisplay, true, true);
        Register(api, "buildwright-projection-tool", BuildwrightLang.Get("hotkey.projection-tool"), GlKeys.T, () => WorkspaceOpenTool(BuildwrightWorkspacePage.Projection), true, true);
        Register(api, "buildwright-library-open", BuildwrightLang.Get("hotkey.library-open"), GlKeys.F, OpenLibrary, true, true);
        Register(api, "buildwright-materials", BuildwrightLang.Get("hotkey.materials"), GlKeys.E, () => WorkspaceOpenMaterials(), true, true);
        Register(api, "buildwright-diff", BuildwrightLang.Get("hotkey.diff"), GlKeys.D, () => WorkspaceOpenDiff(), true, true);
        Register(api, "buildwright-move-forward", BuildwrightLang.Get("hotkey.move-forward"), GlKeys.Up, () => WorkspaceMoveRelative(1, 0, 0), true, true);
        Register(api, "buildwright-move-back", BuildwrightLang.Get("hotkey.move-back"), GlKeys.Down, () => WorkspaceMoveRelative(-1, 0, 0), true, true);
        Register(api, "buildwright-move-left", BuildwrightLang.Get("hotkey.move-left"), GlKeys.Left, () => WorkspaceMoveRelative(0, -1, 0), true, true);
        Register(api, "buildwright-move-right", BuildwrightLang.Get("hotkey.move-right"), GlKeys.Right, () => WorkspaceMoveRelative(0, 1, 0), true, true);
        Register(api, "buildwright-tool-toggle", BuildwrightLang.Get("hotkey.tool-toggle"), GlKeys.O, ToggleToolItem, true, true);
        Register(api, "buildwright-tool-mode-previous", BuildwrightLang.Get("hotkey.tool-mode-previous"), GlKeys.LBracket, () => CycleToolMode(false), true, true);
        Register(api, "buildwright-tool-mode-next", BuildwrightLang.Get("hotkey.tool-mode-next"), GlKeys.RBracket, () => CycleToolMode(true), true, true);
        Register(api, "buildwright-selection-corner", BuildwrightLang.Get("hotkey.selection-corner"), GlKeys.C, SelectNextSelectionCorner, true, true);
        Register(api, "buildwright-tool-execute", BuildwrightLang.Get("hotkey.tool-execute"), GlKeys.Enter, BeginToolOperation, true, true);
    }

    private static void Register(ICoreClientAPI api, string code, string name, GlKeys key, Action action,
        bool ctrl = false, bool shift = false, bool alt = false)
    {
        api.Input.RegisterHotKey(code, name, key, HotkeyType.CharacterControls,
            altPressed: alt, ctrlPressed: ctrl, shiftPressed: shift);
        api.Input.SetHotKeyHandler(code, _ => { action(); return true; });
    }

    private void ToggleToolItem()
    {
        config.ToolItemEnabled = !config.ToolItemEnabled;
        StoreConfig();
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get(config.ToolItemEnabled
            ? "chat.tool-enabled" : "chat.tool-disabled"));
    }

    private void TogglePlayerControl()
    {
        if (!Windows.AnyOpened)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.open-workbench-first"));
            return;
        }

        var enablePlayerControl = !Windows.PlayerControl;
        Windows.SetPlayerControl(enablePlayerControl);
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get(enablePlayerControl
            ? "chat.player-control-enabled" : "chat.player-control-disabled"));
    }

    private void CycleToolMode(bool forward)
    {
        var creative = capi?.World.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative;
        var previous = config.ToolMode;
        config.ToolMode = config.ToolMode.Cycle(forward, creative);
        if (previous == BuildwrightToolMode.Move && config.ToolMode != BuildwrightToolMode.Move) ClearMovePreview();
        StoreConfig();
        UpdateHighlights();
        OnControlHudTick(0);
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-mode",
            (int)config.ToolMode + 1, BuildwrightToolModes.Count, BuildwrightLang.Get(config.ToolMode.LanguageKey())));
    }

    private void StoreConfig()
    {
        try { capi?.StoreModConfig(config, "Buildwright.json"); } catch { }
    }

    private void BeginToolOperation()
    {
        if (capi == null || worldOperations == null) return;
        if (!config.ToolMode.IsExecutable())
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-no-operation"));
            return;
        }
        if (!worldOperations.CanModifyWorld(out var reason))
        {
            capi.ShowChatMessage("[Buildwright] " + reason);
            return;
        }

        new BuildwrightConfirmDialog(capi, BuildwrightLang.Get(config.ToolMode.ConfirmLanguageKey()), ExecuteToolOperation).TryOpen();
    }

    private void ExecuteToolOperation()
    {
        switch (config.ToolMode)
        {
            case BuildwrightToolMode.Fill:
                ExecuteFillTool();
                break;
            case BuildwrightToolMode.ReplaceBlock:
                ExecuteReplaceTool();
                break;
            case BuildwrightToolMode.PasteSchematic:
                ExecuteProjectionTool(onlyMissing: false);
                break;
            case BuildwrightToolMode.GridPaste:
                ExecuteGridPasteTool();
                break;
            case BuildwrightToolMode.Move:
                ExecuteMoveTool();
                break;
            case BuildwrightToolMode.Delete:
                ExecuteDeleteTool();
                break;
            case BuildwrightToolMode.Rebuild:
                ExecuteProjectionTool(onlyMissing: true);
                break;
        }
    }

    private void ExecuteFillTool()
    {
        if (!TryGetToolSelection(out var min, out var max) || toolPrimaryBlock == null || worldOperations == null) return;
        ReportToolResult(worldOperations.FillRegion(min, max, toolPrimaryBlock));
    }

    private void ExecuteReplaceTool()
    {
        if (!TryGetToolSelection(out var min, out var max) || toolPrimaryBlock == null || toolSecondaryBlock == null || worldOperations == null) return;
        ReportToolResult(worldOperations.FillRegion(min, max, toolPrimaryBlock, toolSecondaryBlock));
    }

    private void ExecuteProjectionTool(bool onlyMissing)
    {
        if (activeDocument == null || activeRenderer == null || worldOperations == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-requires-projection"));
            return;
        }
        if (activeRenderer.IsFloating)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-lock-projection"));
            return;
        }
        ReportToolResult(worldOperations.Place(activeDocument, activeRenderer.Origin, activeRenderer.RotationQuarterTurns,
            activeRenderer.Mirror, onlyMissing, clearExtra: false));
    }

    private void ExecuteGridPasteTool()
    {
        if (activeDocument == null || activeRenderer == null || worldOperations == null || gridStart == null || gridEnd == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-requires-grid"));
            return;
        }
        if (activeRenderer.IsFloating)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-lock-projection"));
            return;
        }

        var dimensions = activeDocument.Metadata.Dimensions;
        var quarterTurns = activeRenderer.RotationQuarterTurns;
        var xStep = Math.Max(1, quarterTurns % 2 == 0 ? dimensions.X : dimensions.Z);
        var yStep = Math.Max(1, dimensions.Y);
        var zStep = Math.Max(1, quarterTurns % 2 == 0 ? dimensions.Z : dimensions.X);
        var placementCount = GridCount(gridStart.X, gridEnd.X, xStep) * GridCount(gridStart.Y, gridEnd.Y, yStep) * GridCount(gridStart.Z, gridEnd.Z, zStep);
        if (placementCount > MaxGridPlacements)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-grid-limit", placementCount, MaxGridPlacements));
            return;
        }
        var anchors = from x in GridAxis(gridStart.X, gridEnd.X, xStep)
                      from y in GridAxis(gridStart.Y, gridEnd.Y, yStep)
                      from z in GridAxis(gridStart.Z, gridEnd.Z, zStep)
                      select new BlockPos(x, y, z, gridStart.dimension);
        var result = new OperationResult();
        foreach (var anchor in anchors)
        {
            var placed = worldOperations.Place(activeDocument, anchor, quarterTurns, activeRenderer.Mirror, onlyMissing: false, clearExtra: false);
            if (!placed.Success)
            {
                capi?.ShowChatMessage("[Buildwright] " + placed.Error);
                return;
            }
            result.Placed += placed.Placed;
            result.Cleared += placed.Cleared;
            result.Queued |= placed.Queued;
            result.UnknownBlocks.AddRange(placed.UnknownBlocks);
        }
        ReportToolResult(result);
    }

    private static IEnumerable<int> GridAxis(int start, int end, int step)
    {
        var direction = end >= start ? 1 : -1;
        for (var value = start; direction > 0 ? value <= end : value >= end; value += direction * step) yield return value;
    }

    private static long GridCount(int start, int end, int step) => Math.Abs((long)end - start) / step + 1;

    private void ExecuteMoveTool()
    {
        if (!TryGetToolSelection(out var min, out var max) || toolMoveTarget == null || capi == null || worldOperations == null) return;
        if (toolMoveRenderer is { IsFloating: true })
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-lock-projection"));
            return;
        }
        try
        {
            var document = toolMoveDocument ?? RegionCapture.Capture(capi.World, min, max, selection.Anchor!, selection.AnchorFace);
            var target = toolMoveTarget.Copy();
            var rotation = toolMoveRenderer?.RotationQuarterTurns ?? 0;
            var mirror = toolMoveRenderer?.Mirror ?? MirrorAxis.None;
            var targetVolume = WorldOperationService.GetTargetVolume(document, target, rotation, mirror);
            var (targetMin, targetMax) = BoundsFromVolume(targetVolume, target.dimension);
            var targetFacing = TransformFacing(selection.AnchorFace, rotation, mirror);
            var placed = worldOperations.PlaceAndClearSource(document, target, rotation, mirror, onlyMissing: false,
                clearExtra: true, min, max, succeeded =>
                {
                    if (!succeeded) return;
                    selection.SetTransformedBounds(target, targetFacing, targetMin, targetMax);
                    ClearMovePreview();
                    UpdateHighlights();
                });
            if (!placed.Success) { ReportToolResult(placed); return; }
            ReportToolResult(placed);
        }
        catch (Exception exception)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-operation-failed", exception.Message));
        }
    }

    private static (BlockPos Min, BlockPos Max) BoundsFromVolume(
        IReadOnlySet<(int X, int Y, int Z)> volume, int dimension)
    {
        if (volume.Count == 0) throw new InvalidDataException("The transformed move region is empty.");
        var minX = int.MaxValue; var minY = int.MaxValue; var minZ = int.MaxValue;
        var maxX = int.MinValue; var maxY = int.MinValue; var maxZ = int.MinValue;
        foreach (var (x, y, z) in volume)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
        }
        return (new BlockPos(minX, minY, minZ, dimension), new BlockPos(maxX, maxY, maxZ, dimension));
    }

    private static BlockFacing? TransformFacing(BlockFacing? facing, int rotation, MirrorAxis mirror)
    {
        if (facing == null) return null;
        var x = facing.Normali.X;
        var y = facing.Normali.Y;
        var z = facing.Normali.Z;
        if (mirror == MirrorAxis.X) x = -x;
        else if (mirror == MirrorAxis.Y) y = -y;
        else if (mirror == MirrorAxis.Z) z = -z;
        var normalizedRotation = ((rotation % 4) + 4) % 4;
        return normalizedRotation switch
        {
            1 => BlockFacing.FromVector(z, y, -x),
            2 => BlockFacing.FromVector(-x, y, -z),
            3 => BlockFacing.FromVector(-z, y, x),
            _ => BlockFacing.FromVector(x, y, z)
        };
    }

    private void ExecuteDeleteTool()
    {
        if (!TryGetToolSelection(out var min, out var max) || worldOperations == null) return;
        ReportToolResult(worldOperations.ClearRegion(min, max));
    }

    private bool TryGetToolSelection(out BlockPos min, out BlockPos max)
    {
        min = null!;
        max = null!;
        if (!selection.HasSelection)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-requires-selection"));
            return false;
        }
        min = selection.Min!;
        max = selection.Max!;
        return true;
    }

    private void ReportToolResult(OperationResult result)
    {
        if (!result.Success)
        {
            capi?.ShowChatMessage("[Buildwright] " + result.Error);
            return;
        }
        if (result.Queued)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent"));
            return;
        }
        if (activeRenderer is { IsFloating: false }) BuildTracker();
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-operation-result", result.Placed, result.Cleared));
    }

    private void RegisterCommands(ICoreClientAPI api)
    {
        var parsers = api.ChatCommands.Parsers;
        var root = api.ChatCommands.Create("bw").WithDesc("Buildwright local blueprint tools");

        root.BeginSub("corner1").WithDesc("Set selection corner 1 and anchor")
            .HandleWith(_ => { SetCorner(1); return TextCommandResult.Success(); }).EndSub();
        root.BeginSub("corner2").WithDesc("Set selection corner 2")
            .HandleWith(_ => { SetCorner(2); return TextCommandResult.Success(); }).EndSub();
        root.BeginSub("status").WithDesc("Show selection or active build status")
            .HandleWith(_ => Status()).EndSub();
        root.BeginSub("clear").WithDesc("Clear selection")
            .HandleWith(_ => { ClearMovePreview(); selection.Clear(); ClearHighlights(); return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.selection-cleared")); }).EndSub();
        root.BeginSub("grow").WithDesc("Grow selection: direction amount")
            .WithArgs(parsers.Word("direction"), parsers.OptionalInt("amount", 1))
            .HandleWith(args => Resize(args, true)).EndSub();
        root.BeginSub("shrink").WithDesc("Shrink selection: direction amount")
            .WithArgs(parsers.Word("direction"), parsers.OptionalInt("amount", 1))
            .HandleWith(args => Resize(args, false)).EndSub();
        root.BeginSub("save").WithDesc("Save selection as .vsbp")
            .WithArgs(parsers.Word("name"), parsers.OptionalWord("overwrite"))
            .HandleWith(SaveCommand).EndSub();
        root.BeginSub("export").WithDesc("Export local blueprint as native schematic JSON")
            .WithArgs(parsers.Word("name"), parsers.Word("path"))
            .HandleWith(ExportCommand).EndSub();
        root.BeginSub("import").WithDesc("Import .vsbp or native schematic JSON from a local path")
            .WithArgs(parsers.Word("path"), parsers.OptionalWord("name"))
            .HandleWith(ImportCommand).EndSub();
        root.BeginSub("info").WithDesc("Show blueprint metadata, dependencies and compatibility")
            .WithArgs(parsers.Word("name"))
            .HandleWith(InfoCommand).EndSub();
        root.BeginSub("duplicate").WithDesc("Duplicate a local blueprint")
            .WithArgs(parsers.Word("source"), parsers.Word("target"))
            .HandleWith(DuplicateCommand).EndSub();
        root.BeginSub("template").WithDesc("Create a reusable template copy")
            .WithArgs(parsers.Word("source"), parsers.Word("target"))
            .HandleWith(TemplateCommand).EndSub();
        root.BeginSub("delete").WithDesc("Delete a local blueprint")
            .WithArgs(parsers.Word("name"))
            .HandleWith(DeleteCommand).EndSub();
        root.BeginSub("load").WithDesc("Load a local blueprint")
            .WithArgs(parsers.Word("name"), parsers.OptionalWord("matching"))
            .HandleWith(LoadCommand).EndSub();
        root.BeginSub("paste").WithDesc("Alias for load")
            .WithArgs(parsers.Word("name"), parsers.OptionalWord("matching"))
            .HandleWith(LoadCommand).EndSub();
        root.BeginSub("place").WithDesc("Lock or unlock active ghost")
            .HandleWith(_ => { TogglePlace(); return TextCommandResult.Success(); }).EndSub();
        root.BeginSub("rotate").WithDesc("Rotate active ghost by quarter turns")
            .WithArgs(parsers.OptionalInt("quarterTurns", 1))
            .HandleWith(args => RotateCommand(args)).EndSub();
        root.BeginSub("move").WithDesc("Move active ghost by dx dy dz blocks")
            .WithArgs(parsers.Int("dx"), parsers.Int("dy"), parsers.Int("dz"))
            .HandleWith(args => MoveCommand(args)).EndSub();
        root.BeginSub("move-relative").WithDesc("Move locked ghost relative to player: forward right up")
            .WithArgs(parsers.Int("forward"), parsers.Int("right"), parsers.Int("up"))
            .HandleWith(args => MoveRelativeCommand(args)).EndSub();
        root.BeginSub("display").WithDesc("Ghost display: full, missing, wrong or difference")
            .WithArgs(parsers.Word("mode"))
            .HandleWith(args => DisplayCommand(args)).EndSub();
        root.BeginSub("clip").WithDesc("Clip local blueprint axis: x|y|z min max")
            .WithArgs(parsers.Word("axis"), parsers.Int("min"), parsers.Int("max"))
            .HandleWith(args => ClipCommand(args)).EndSub();
        root.BeginSub("opacity").WithDesc("Set ghost opacity from 0.05 to 1")
            .WithArgs(parsers.Float("opacity"))
            .HandleWith(args => OpacityCommand(args)).EndSub();
        root.BeginSub("mirror").WithDesc("Cycle active ghost mirror")
            .HandleWith(_ => { CycleMirror(); return TextCommandResult.Success(); }).EndSub();
        root.BeginSub("mode").WithDesc("Set matching mode: loose, medium or strict")
            .WithArgs(parsers.Word("mode"))
            .HandleWith(args => ModeCommand(args)).EndSub();
        root.BeginSub("materials").WithDesc("Show required materials and inventory counts")
            .HandleWith(_ => MaterialsCommand()).EndSub();
        root.BeginSub("materials-sort").WithDesc("Sort materials: name, required, missing or completed")
            .WithArgs(parsers.Word("sort"))
            .HandleWith(MaterialSortCommand).EndSub();
        root.BeginSub("materials-copy").WithDesc("Copy material list to the clipboard")
            .HandleWith(_ => ExportMaterials("copy")).EndSub();
        root.BeginSub("materials-file").WithDesc("Export material list to a local text file")
            .WithArgs(parsers.Word("path"))
            .HandleWith(args => ExportMaterials("file", (string)args[0])).EndSub();
        root.BeginSub("list").WithDesc("List local blueprints")
            .HandleWith(_ => { ListBlueprints(); return TextCommandResult.Success(); }).EndSub();
        root.BeginSub("library").WithDesc("Open the local blueprint library")
            .HandleWith(_ => { OpenLibrary(); return TextCommandResult.Success(); }).EndSub();
        root.BeginSub("workspace").WithDesc("Open the Buildwright workbench")
            .HandleWith(_ => { OpenWorkspace(); return TextCommandResult.Success(); }).EndSub();
        root.BeginSub("restore").WithDesc("Restore the rolling backup")
            .WithArgs(parsers.Word("name"))
            .HandleWith(RestoreCommand).EndSub();
        root.BeginSub("cancel").WithDesc("Dismiss active ghost and clear selection")
            .HandleWith(_ => { Cancel(); return TextCommandResult.Success(); }).EndSub();
        root.BeginSub("execute").WithDesc("Place the locked blueprint through Buildwright Server; pass confirm")
            .WithArgs(parsers.Word("confirm"))
            .HandleWith(args => ExecuteActiveCommand(args, false, false)).EndSub();
        root.BeginSub("fill").WithDesc("Fill missing blueprint blocks through Buildwright Server; pass confirm")
            .WithArgs(parsers.Word("confirm"))
            .HandleWith(args => ExecuteActiveCommand(args, true, false)).EndSub();
        root.BeginSub("clear-extra").WithDesc("Clear extra cells inside the locked blueprint volume; pass confirm")
            .WithArgs(parsers.Word("confirm"))
            .HandleWith(args => ExecuteActiveCommand(args, false, true)).EndSub();
        root.BeginSub("clone").WithDesc("Clone selected region by dx dy dz through Buildwright Server; pass confirm")
            .WithArgs(parsers.Int("dx"), parsers.Int("dy"), parsers.Int("dz"), parsers.Word("confirm"))
            .HandleWith(args => RegionCloneCommand(args, move: false, rotate: 0, mirror: MirrorAxis.None)).EndSub();
        root.BeginSub("move-region").WithDesc("Move selected region by dx dy dz through Buildwright Server; pass confirm")
            .WithArgs(parsers.Int("dx"), parsers.Int("dy"), parsers.Int("dz"), parsers.Word("confirm"))
            .HandleWith(args => RegionCloneCommand(args, move: true, rotate: 0, mirror: MirrorAxis.None)).EndSub();
        root.BeginSub("rotate-region").WithDesc("Rotate selected region through Buildwright Server; pass confirm")
            .WithArgs(parsers.Int("quarterTurns"), parsers.Word("confirm"))
            .HandleWith(RegionRotateCommand).EndSub();
        root.BeginSub("mirror-region").WithDesc("Mirror selected region around anchor axis x, y or z; pass confirm")
            .WithArgs(parsers.Word("axis"), parsers.Word("confirm"))
            .HandleWith(RegionMirrorCommand).EndSub();
        root.BeginSub("clear-region").WithDesc("Clear selected region through Buildwright Server; pass confirm")
            .WithArgs(parsers.Word("confirm"))
            .HandleWith(ClearRegionCommand).EndSub();
    }

    private void SetCorner(int corner)
    {
        if (capi == null) return;
        var selectionHit = capi.World.Player?.CurrentBlockSelection;
        if (selectionHit?.Position == null)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.aim-block"));
            return;
        }
        if (config.ToolMode == BuildwrightToolMode.Move) ClearMovePreview();
        if (corner == 1) selection.SetCorner1(selectionHit.Position, selectionHit.Face);
        else selection.SetCorner2(selectionHit.Position);
        UpdateHighlights();
        var d = selection.Dimensions;
        capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.corner-set", corner, d.x, d.y, d.z, d.x * d.y * d.z));
    }

    private TextCommandResult Resize(TextCommandCallingArgs args, bool grow)
    {
        if (!selection.HasSelection) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.set-corner1-first"));
        var face = BlockFacing.FromCode(((string)args[0]).ToLowerInvariant());
        if (face == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.direction-invalid"));
        var amount = Math.Max(1, (int)args[1]);
        if (grow) selection.Grow(face, amount); else selection.Shrink(face, amount);
        if (config.ToolMode == BuildwrightToolMode.Move && toolMoveTarget != null) RebuildMovePreview();
        else UpdateHighlights();
        return Status();
    }

    private TextCommandResult Status()
    {
        if (capi == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.client-api-unavailable"));
        if (activeTracker != null)
        {
            var d = activeTracker.Current;
            return TextCommandResult.Success($"[Buildwright] {activeName}: {d.Progress:P0}, missing {d.MissingCount}, wrong {d.WrongCount}, extra {d.ExtraCount}.");
        }
        if (!selection.HasSelection) return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.no-active-selection"));
        var size = selection.Dimensions;
        return TextCommandResult.Success($"[Buildwright] Selection {size.x}x{size.y}x{size.z}; anchor {selection.Anchor}.");
    }

    private TextCommandResult SaveCommand(TextCommandCallingArgs args)
    {
        if (capi == null || repository == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.client-api-unavailable"));
        if (!selection.HasSelection) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.set-both-corners"));
        var name = (string)args[0];
        var overwrite = args.ArgCount > 1 && string.Equals(args[1]?.ToString(), "overwrite", StringComparison.OrdinalIgnoreCase);
        try
        {
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            repository.Save(document, name, BlueprintFileKind.BuildwrightZip, overwrite);
            capi.ShowChatMessage($"[Buildwright] Saved '{name}' ({document.Metadata.Dimensions}).");
            return TextCommandResult.Success();
        }
        catch (Exception ex)
        {
            capi.Logger.Error("[buildwright] save failed: {0}", ex);
            return TextCommandResult.Error($"[Buildwright] Save failed: {ex.Message}");
        }
    }

    private TextCommandResult ExportCommand(TextCommandCallingArgs args)
    {
        if (repository == null) return TextCommandResult.Error("[Buildwright] Repository unavailable.");
        try
        {
            var name = (string)args[0];
            var source = LoadDocument(name);
            var path = Path.GetFullPath((string)args[1]);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += ".json";
            new BlueprintCodec().Write(path, source, overwrite: true);
            return TextCommandResult.Success($"[Buildwright] Exported '{name}' to {path}.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Export failed: {ex.Message}"); }
    }

    private TextCommandResult ImportCommand(TextCommandCallingArgs args)
    {
        if (repository == null) return TextCommandResult.Error("[Buildwright] Repository unavailable.");
        try
        {
            var path = Path.GetFullPath((string)args[0]);
            if (!File.Exists(path)) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.import-file-missing"));
            var document = new BlueprintCodec().Read(path);
            var name = args.ArgCount > 1 && !string.IsNullOrWhiteSpace(args[1]?.ToString())
                ? (string)args[1]
                : Path.GetFileNameWithoutExtension(path);
            repository.Save(document, name, BlueprintFileKind.BuildwrightZip, overwrite: true);
            return TextCommandResult.Success($"[Buildwright] Imported '{name}' as a local .vsbp blueprint.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Import failed: {ex.Message}"); }
    }

    private TextCommandResult InfoCommand(TextCommandCallingArgs args)
    {
        if (capi == null) return TextCommandResult.Error("[Buildwright] Client API unavailable.");
        try
        {
            var info = BlueprintInspector.Inspect(LoadDocument((string)args[0]), capi.World, activeMode);
            var dependencies = info.Dependencies.Count == 0 ? "none" : string.Join(", ", info.Dependencies);
            var unknown = info.UnknownBlocks.Count == 0 ? "none" : string.Join(", ", info.UnknownBlocks.Take(8));
            var missing = info.MissingDependencies.Count == 0 ? "none" : string.Join(", ", info.MissingDependencies);
            return TextCommandResult.Success($"[Buildwright] {info.Name}: {info.Dimensions}, blocks {info.BlockCount}, materials {info.MaterialCount}, game {info.GameVersion} ({(info.VersionCompatible ? "compatible" : "version warning")}), deps {dependencies}, missing deps {missing}, unknown blocks {unknown}.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Info failed: {ex.Message}"); }
    }

    private TextCommandResult DuplicateCommand(TextCommandCallingArgs args)
    {
        if (repository == null) return TextCommandResult.Error("[Buildwright] Repository unavailable.");
        try
        {
            var document = LoadDocument((string)args[0]);
            repository.Save(document, (string)args[1], BlueprintFileKind.BuildwrightZip, overwrite: false);
            return TextCommandResult.Success($"[Buildwright] Created blueprint copy '{args[1]}'.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Duplicate failed: {ex.Message}"); }
    }

    private TextCommandResult TemplateCommand(TextCommandCallingArgs args)
    {
        if (repository == null) return TextCommandResult.Error("[Buildwright] Repository unavailable.");
        try
        {
            var document = LoadDocument((string)args[0]);
            if (!document.Metadata.Capabilities.Contains("template", StringComparer.OrdinalIgnoreCase))
                document.Metadata.Capabilities.Add("template");
            repository.Save(document, (string)args[1], BlueprintFileKind.BuildwrightZip, overwrite: false);
            return TextCommandResult.Success($"[Buildwright] Created template '{args[1]}'.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Template failed: {ex.Message}"); }
    }

    private TextCommandResult DeleteCommand(TextCommandCallingArgs args)
    {
        if (repository == null) return TextCommandResult.Error("[Buildwright] Repository unavailable.");
        try
        {
            var name = (string)args[0];
            var kind = repository.Exists(name) ? BlueprintFileKind.BuildwrightZip : BlueprintFileKind.NativeJson;
            repository.Delete(name, kind);
            return TextCommandResult.Success($"[Buildwright] Deleted '{name}'.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Delete failed: {ex.Message}"); }
    }

    private BlueprintDocument LoadDocument(string name)
    {
        if (repository == null) throw new InvalidOperationException("Repository unavailable.");
        var vsbp = repository.GetPath(name, BlueprintFileKind.BuildwrightZip);
        return new BlueprintCodec().Read(File.Exists(vsbp) ? vsbp : repository.GetPath(name, BlueprintFileKind.NativeJson));
    }

    private void ShowCompatibilityWarnings(BlueprintDocument document)
    {
        if (capi == null) return;
        var inspection = BlueprintInspector.Inspect(document, capi.World, activeMode);
        if (!inspection.VersionCompatible)
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.version-warning", inspection.GameVersion, GameVersion.ShortGameVersion));
        if (inspection.MissingDependencies.Count > 0)
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.missing-dependencies", string.Join(", ", inspection.MissingDependencies)));
        if (inspection.UnknownBlocks.Count > 0)
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.unknown-blocks", string.Join(", ", inspection.UnknownBlocks.Take(12))));
    }

    private TextCommandResult LoadCommand(TextCommandCallingArgs args)
    {
        if (capi == null || repository == null) return TextCommandResult.Error("[Buildwright] Client API unavailable.");
        var name = (string)args[0];
        var requested = args.ArgCount > 1 ? ParseMode(args[1]?.ToString()) : null;
        if (requested.HasValue) activeMode = requested.Value;
        try
        {
            BlueprintDocument document;
            var vsbpPath = repository.GetPath(name, BlueprintFileKind.BuildwrightZip);
            if (File.Exists(vsbpPath)) document = new BlueprintCodec().Read(vsbpPath);
            else document = new BlueprintCodec().Read(repository.GetPath(name, BlueprintFileKind.NativeJson));
            StartGhost(name, document);
            return TextCommandResult.Success($"[Buildwright] Loaded '{name}'. Aim at a block and use Ctrl+Shift+P to lock it.");
        }
        catch (Exception ex)
        {
            capi.Logger.Error("[buildwright] load failed: {0}", ex);
            return TextCommandResult.Error($"[Buildwright] Load failed: {ex.Message}");
        }
    }

    private void StartGhost(string name, BlueprintDocument document)
    {
        if (capi == null) return;
        ClearGhost();
        var schematic = ParseSchematic(document);
        var anchorOffset = document.Metadata.AnchorOffset ?? new Int3();
        var face = ResolveAnchorFace(document);
        var player = capi.World.Player?.Entity;
        var initial = player == null ? new BlockPos(0, 0, 0) : new BlockPos((int)player.Pos.X, (int)player.Pos.Y, (int)player.Pos.Z, player.Pos.Dimension);
        activeSchematic = schematic;
        activeDocument = document;
        displayMode = GhostDisplayMode.Full;
        clipAxis = null;
        activeAnchorOffset = anchorOffset;
        activeName = name;
        var entityClasses = document.BlockEntities.ToDictionary(record => RegionCapture.Pack(record.Position.X, record.Position.Y, record.Position.Z),
            record => record.EntityClass ?? string.Empty);
        var compatData = document.BlockEntities.ToDictionary(
            record => RegionCapture.Pack(record.Position.X, record.Position.Y, record.Position.Z),
            record => (IReadOnlyList<BuildwrightCompatPayload>)record.CompatData);
        var includeCell = BuildIncludeCell();
        var terrain = BlueprintTerrainProjection.TryCreate(capi, schematic, initial, includeCell);
        var beamGuides = BuildwrightSupportBeamGuideMesh.TryCreate(capi, document, schematic.SizeY, BuildClipCell());
        activeMesh = new BuildwrightGhostMesh(capi, schematic, Math.Clamp(config.GhostAlpha, 0.05f, 1f),
            includeCell, document.ChiselData, entityClasses, compatData,
            terrain == null ? null : BlueprintTerrainProjection.RequiresTerrainPipeline, initial);
        activeRenderer = new BuildwrightGhostRenderer(capi, activeMesh, initial, anchorOffset, face,
            config.RenderDistanceBlocks, terrain, beamGuides);
        activeRenderer.SetMatchingMode(activeMode);
        ShowCompatibilityWarnings(document);
        capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.ghost-ready", name));
    }

    private static BlockSchematic ParseSchematic(BlueprintDocument document)
    {
        var error = string.Empty;
        BlockSchematic.BlockRemaps ??= new Dictionary<string, Dictionary<string, string>>();
        BlockSchematic.ItemRemaps ??= new Dictionary<string, Dictionary<string, string>>();
        var schematic = BlockSchematic.LoadFromString(document.Schematic.ToString(Formatting.None), ref error);
        if (schematic == null) throw new InvalidDataException(error);
        EnsureCollections(schematic);
        return schematic;
    }

    private void TogglePlace()
    {
        if (config.ToolMode == BuildwrightToolMode.Move)
        {
            if (toolMoveRenderer != null) ToggleMovePreviewPlacement();
            else capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.tool-requires-move-target"));
            return;
        }
        if (capi == null || activeRenderer == null || activeSchematic == null || activeName == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
            return;
        }
        if (activeRenderer.IsFloating)
        {
            activeRenderer.Place();
            BuildTracker();
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.ghost-locked"));
        }
        else
        {
            activeRenderer.Unplace();
            ClearTracker();
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.ghost-unlocked"));
        }
    }

    private void ToggleMovePreviewPlacement()
    {
        if (toolMoveRenderer == null) return;
        if (toolMoveRenderer.IsFloating)
        {
            toolMoveRenderer.Place();
            toolMoveTarget = toolMoveRenderer.Origin.Copy();
        }
        else
        {
            toolMoveRenderer.Unplace();
        }
        UpdateHighlights();
    }

    private void BuildTracker()
    {
        if (capi == null || activeRenderer == null || activeSchematic == null || activeName == null) return;
        ClearTracker();
        activeTracker = new BuildwrightTracker(capi, activeSchematic, activeName,
            activeAnchorOffset,
            activeRenderer.Origin, activeRenderer.RotationQuarterTurns, activeRenderer.Mirror, activeMode,
            activeDocument?.ChiselData, activeDocument?.BlockEntities);
        activeHud = new BuildwrightHud(capi, activeTracker, activeName, WorkspaceCopyMaterials);
        progressHudHidden = false;
        activeHud.TryOpen();
        lastScan = 0;
        completionReported = false;
        if (tickListener == -1) tickListener = capi.Event.RegisterGameTickListener(OnTick, 50);
    }

    private void OnTick(float dt)
    {
        if (capi == null || activeTracker == null) return;
        if (activeTracker.IsScanning)
        {
            activeTracker.Scan(activeTracker.ScanSliceBudget);
            activeHud?.Refresh();
            return;
        }
        if (capi.ElapsedMilliseconds - lastScan < Math.Max(config.ScanIntervalMs, activeTracker.RecommendedScanIntervalMs)) return;
        lastScan = capi.ElapsedMilliseconds;
        activeTracker.Scan(activeTracker.ScanSliceBudget);
        activeHud?.Refresh();
        if (activeTracker.IsComplete && !completionReported)
        {
            completionReported = true;
            capi.TriggerIngameDiscovery(this, "buildwright-complete", "Blueprint complete");
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.complete", activeName ?? string.Empty));
        }
        else if (!activeTracker.IsComplete) completionReported = false;
    }

    private void CycleMirror()
    {
        var renderer = config.ToolMode == BuildwrightToolMode.Move ? toolMoveRenderer : activeRenderer;
        if (renderer == null) { capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost")); return; }
        renderer.CycleMirror();
        if (renderer == toolMoveRenderer) UpdateHighlights();
        else if (!renderer.IsFloating) BuildTracker();
    }

    private TextCommandResult RotateCommand(TextCommandCallingArgs args)
    {
        var renderer = config.ToolMode == BuildwrightToolMode.Move ? toolMoveRenderer : activeRenderer;
        if (renderer == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
        renderer.Rotate(args.ArgCount > 0 ? (int)args[0] : 1);
        if (renderer == toolMoveRenderer) UpdateHighlights();
        else if (!renderer.IsFloating) BuildTracker();
        return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.ghost-rotated", renderer.RotationQuarterTurns * 90));
    }

    private TextCommandResult MoveCommand(TextCommandCallingArgs args)
    {
        if (activeRenderer == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
        if (activeRenderer.IsFloating) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.lock-before-move"));
        activeRenderer.Move((int)args[0], (int)args[1], (int)args[2]);
        BuildTracker();
        return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.ghost-moved", args[0], args[1], args[2]));
    }

    private TextCommandResult MoveRelativeCommand(TextCommandCallingArgs args)
    {
        if (capi == null || activeRenderer == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
        if (activeRenderer.IsFloating) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.lock-before-move"));
        activeRenderer.MoveRelative(capi, (int)args[0], (int)args[1], (int)args[2]);
        BuildTracker();
        return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.ghost-moved-relative"));
    }

    private TextCommandResult ExecuteActiveCommand(TextCommandCallingArgs args, bool onlyMissing, bool clearExtra)
    {
        if (!HasConfirmation(args)) return TextCommandResult.Error("[Buildwright] Repeat with the literal word confirm.");
        if (activeDocument == null || activeRenderer == null || worldOperations == null)
            return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.load-lock-blueprint"));
        if (activeRenderer.IsFloating) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.lock-before-world"));
        try
        {
            var result = worldOperations.Place(activeDocument, activeRenderer.Origin, activeRenderer.RotationQuarterTurns,
                activeRenderer.Mirror, onlyMissing, clearExtra);
            if (!result.Success) return TextCommandResult.Error("[Buildwright] " + result.Error);
            if (result.Queued) return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent"));
            var unknown = result.UnknownBlocks.Count == 0 ? string.Empty : $" Unknown: {string.Join(", ", result.UnknownBlocks.Distinct().Take(6))}.";
            return TextCommandResult.Success($"[Buildwright] World operation placed {result.Placed} and cleared {result.Cleared}.{unknown}");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] World operation failed: {ex.Message}"); }
    }

    private TextCommandResult RegionCloneCommand(TextCommandCallingArgs args, bool move, int rotate, MirrorAxis mirror)
    {
        if (capi == null || worldOperations == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.client-api-unavailable"));
        if (!selection.HasSelection) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.select-region-first"));
        if (!HasConfirmation(args)) return TextCommandResult.Error("[Buildwright] Repeat with the literal word confirm.");
        try
        {
            var dx = (int)args[0]; var dy = (int)args[1]; var dz = (int)args[2];
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            var target = new BlockPos(selection.Anchor!.X + dx, selection.Anchor.Y + dy, selection.Anchor.Z + dz, selection.Anchor.dimension);
            var placed = move
                ? worldOperations.PlaceAndClearSource(document, target, rotate, mirror, onlyMissing: false,
                    clearExtra: true, selection.Min!, selection.Max!)
                : worldOperations.Place(document, target, rotate, mirror, onlyMissing: false, clearExtra: true);
            if (!placed.Success) return TextCommandResult.Error("[Buildwright] " + placed.Error);
            if (placed.Queued) return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent"));
            return TextCommandResult.Success($"[Buildwright] {(move ? "Moved" : "Cloned")} region: placed {placed.Placed}, cleared {placed.Cleared}.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Region operation failed: {ex.Message}"); }
    }

    private TextCommandResult RegionRotateCommand(TextCommandCallingArgs args)
    {
        if (capi == null || worldOperations == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.client-api-unavailable"));
        if (!selection.HasSelection) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.select-region-first"));
        if (!HasConfirmation(args)) return TextCommandResult.Error("[Buildwright] Repeat with the literal word confirm.");
        try
        {
            var turns = ((int)args[0] % 4 + 4) % 4;
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            var result = worldOperations.PlaceAndClearSource(document, selection.Anchor!, turns, MirrorAxis.None,
                false, true, selection.Min!, selection.Max!);
            if (!result.Success) return TextCommandResult.Error("[Buildwright] " + result.Error);
            if (result.Queued) return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent"));
            return TextCommandResult.Success($"[Buildwright] Rotated region {turns * 90} degrees: placed {result.Placed}, cleared {result.Cleared}.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Region rotation failed: {ex.Message}"); }
    }

    private TextCommandResult RegionMirrorCommand(TextCommandCallingArgs args)
    {
        if (capi == null || worldOperations == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.client-api-unavailable"));
        if (!selection.HasSelection) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.select-region-first"));
        if (!HasConfirmation(args)) return TextCommandResult.Error("[Buildwright] Repeat with the literal word confirm.");
        var axis = args[0]?.ToString()?.ToLowerInvariant() switch { "x" => MirrorAxis.X, "y" => MirrorAxis.Y, "z" => MirrorAxis.Z, _ => MirrorAxis.None };
        if (axis == MirrorAxis.None) return TextCommandResult.Error("[Buildwright] Axis must be x, y or z.");
        try
        {
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            var result = worldOperations.PlaceAndClearSource(document, selection.Anchor!, 0, axis, false, true,
                selection.Min!, selection.Max!);
            if (!result.Success) return TextCommandResult.Error("[Buildwright] " + result.Error);
            if (result.Queued) return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent"));
            return TextCommandResult.Success($"[Buildwright] Mirrored region on {axis}: placed {result.Placed}, cleared {result.Cleared}.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Region mirror failed: {ex.Message}"); }
    }

    private TextCommandResult ClearRegionCommand(TextCommandCallingArgs args)
    {
        if (!HasConfirmation(args)) return TextCommandResult.Error("[Buildwright] Repeat with the literal word confirm.");
        if (worldOperations == null || !selection.HasSelection) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.select-region-first"));
        var result = worldOperations.ClearRegion(selection.Min!, selection.Max!);
        return result.Success
            ? TextCommandResult.Success(result.Queued
                ? "[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent")
                : $"[Buildwright] Cleared {result.Cleared} blocks.")
            : TextCommandResult.Error("[Buildwright] " + result.Error);
    }

    private static bool HasConfirmation(TextCommandCallingArgs args) =>
        args.ArgCount > 0 && string.Equals(args[args.ArgCount - 1]?.ToString(), "confirm", StringComparison.OrdinalIgnoreCase);

    private TextCommandResult DisplayCommand(TextCommandCallingArgs args)
    {
        if (activeRenderer == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
        if (!Enum.TryParse<GhostDisplayMode>(args[0]?.ToString(), true, out var value))
            return TextCommandResult.Error("[Buildwright] Display mode must be full, missing, wrong, difference or layer.");
        displayMode = value;
        RebuildGhost();
        return TextCommandResult.Success($"[Buildwright] Ghost display mode: {displayMode}.");
    }

    private TextCommandResult ClipCommand(TextCommandCallingArgs args)
    {
        if (activeSchematic == null || activeRenderer == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
        var axis = args[0]?.ToString()?.ToLowerInvariant();
        if (axis is not ("x" or "y" or "z")) return TextCommandResult.Error("[Buildwright] Clip axis must be x, y or z.");
        var min = (int)args[1];
        var max = (int)args[2];
        var limit = axis == "x" ? activeSchematic.SizeX : axis == "y" ? activeSchematic.SizeY : activeSchematic.SizeZ;
        if (min < 0 || max < min || max >= limit) return TextCommandResult.Error($"[Buildwright] Clip range must be within 0..{limit - 1}.");
        clipAxis = axis[0]; clipMin = min; clipMax = max;
        RebuildGhost();
        return TextCommandResult.Success($"[Buildwright] Clipped {axis} to {min}..{max}.");
    }

    private TextCommandResult OpacityCommand(TextCommandCallingArgs args)
    {
        var value = Math.Clamp((float)args[0], 0.05f, 1f);
        config.GhostAlpha = value;
        if (capi != null) { try { capi.StoreModConfig(config, "Buildwright.json"); } catch { } }
        RebuildGhost();
        return TextCommandResult.Success($"[Buildwright] Ghost opacity: {value:0.00}.");
    }

    private System.Func<int, int, int, bool> BuildIncludeCell()
    {
        var includeClip = BuildClipCell();
        var expectedByLocal = new Dictionary<(int X, int Y, int Z), string?>();
        if (activeSchematic != null)
        {
            for (var i = 0; i < activeSchematic.Indices.Count && i < activeSchematic.BlockIds.Count; i++)
            {
                var local = RegionCapture.Unpack(activeSchematic.Indices[i]);
                if (activeSchematic.BlockCodes.TryGetValue(activeSchematic.BlockIds[i], out var code))
                    expectedByLocal[(local.X, local.Y, local.Z)] = code.ToString();
            }
        }
        return (x, y, z) =>
        {
            if (!includeClip(x, y, z)) return false;
            if (displayMode is GhostDisplayMode.Full or GhostDisplayMode.Layer || activeRenderer == null || activeRenderer.IsFloating) return true;
            if (activeSchematic == null || capi == null) return true;
            if (!expectedByLocal.TryGetValue((x, y, z), out var expected) || expected == null) return false;
            var world = BlueprintTransforms.CellToWorld(new Int3(x, y, z), activeAnchorOffset,
                new Int3(activeRenderer.Origin.X, activeRenderer.Origin.Y, activeRenderer.Origin.Z),
                activeRenderer.RotationQuarterTurns, activeRenderer.Mirror);
            var actualBlock = capi.World.BlockAccessor.GetBlock(new BlockPos(world.X, world.Y, world.Z, activeRenderer.Origin.dimension), BlockLayersAccess.Solid);
            var actual = actualBlock == null || actualBlock.Id == 0 || BlueprintBlockFilter.IsTransient(actualBlock)
                ? null
                : actualBlock.Code?.ToString();
            var one = new DiffEngine().Compare(new[] { (new Int3(world.X, world.Y, world.Z), (string?)expected) },
                new Dictionary<(int X, int Y, int Z), string?> { [(world.X, world.Y, world.Z)] = actual }, activeMode);
            var wrong = one.WrongCount > 0;
            var missing = one.MissingCount > 0;
            return displayMode switch
            {
                GhostDisplayMode.Missing => missing,
                GhostDisplayMode.Wrong => wrong,
                GhostDisplayMode.Difference => missing || wrong,
                _ => true
            };
        };
    }

    private System.Func<int, int, int, bool> BuildClipCell() => (x, y, z) =>
        (clipAxis != 'x' || x >= clipMin && x <= clipMax)
        && (clipAxis != 'y' || y >= clipMin && y <= clipMax)
        && (clipAxis != 'z' || z >= clipMin && z <= clipMax);

    private void RebuildGhost()
    {
        if (capi == null || activeRenderer == null || activeSchematic == null || activeDocument == null) return;
        var oldOrigin = activeRenderer.Origin.Copy();
        var oldFloating = activeRenderer.IsFloating;
        var oldRotation = activeRenderer.RotationQuarterTurns;
        var oldMirror = activeRenderer.Mirror;
        var oldLayers = activeRenderer.VisibleLayers;
        activeRenderer.Dispose();
        var entityClasses = activeDocument.BlockEntities.ToDictionary(record => RegionCapture.Pack(record.Position.X, record.Position.Y, record.Position.Z),
            record => record.EntityClass ?? string.Empty);
        var compatData = activeDocument.BlockEntities.ToDictionary(
            record => RegionCapture.Pack(record.Position.X, record.Position.Y, record.Position.Z),
            record => (IReadOnlyList<BuildwrightCompatPayload>)record.CompatData);
        var includeCell = BuildIncludeCell();
        var terrain = BlueprintTerrainProjection.TryCreate(capi, activeSchematic, oldOrigin, includeCell);
        var beamGuides = BuildwrightSupportBeamGuideMesh.TryCreate(capi, activeDocument,
            activeSchematic.SizeY, BuildClipCell());
        activeMesh = new BuildwrightGhostMesh(capi, activeSchematic, Math.Clamp(config.GhostAlpha, 0.05f, 1f),
            includeCell, activeDocument.ChiselData, entityClasses, compatData,
            terrain == null ? null : BlueprintTerrainProjection.RequiresTerrainPipeline, oldOrigin);
        activeRenderer = new BuildwrightGhostRenderer(capi, activeMesh, oldOrigin, activeAnchorOffset,
            ResolveAnchorFace(activeDocument), config.RenderDistanceBlocks, terrain, beamGuides);
        activeRenderer.SetMatchingMode(activeMode);
        activeRenderer.SetRotation(oldRotation);
        activeRenderer.SetMirror(oldMirror);
        if (!oldFloating) activeRenderer.Place();
        while (activeRenderer.VisibleLayers > oldLayers) activeRenderer.DecreaseLayer();
        if (!oldFloating) BuildTracker();
    }

    private static BlockFacing? ResolveAnchorFace(BlueprintDocument document) =>
        string.IsNullOrWhiteSpace(document.Metadata.AnchorFace) ? null : BlockFacing.FromCode(document.Metadata.AnchorFace);

    private TextCommandResult ModeCommand(TextCommandCallingArgs args)
    {
        var parsed = ParseMode(args[0]?.ToString());
        if (!parsed.HasValue) return TextCommandResult.Error("[Buildwright] Mode must be loose, medium or strict.");
        activeMode = parsed.Value;
        activeRenderer?.SetMatchingMode(activeMode);
        toolMoveRenderer?.SetMatchingMode(activeMode);
        if (activeTracker != null) BuildTracker();
        return TextCommandResult.Success($"[Buildwright] Matching mode: {activeMode}.");
    }

    private static MatchingMode? ParseMode(string? text) => text?.ToLowerInvariant() switch
    {
        "loose" => MatchingMode.Loose,
        "medium" => MatchingMode.Medium,
        "strict" => MatchingMode.Strict,
        _ => null
    };

    private TextCommandResult MaterialsCommand()
    {
        var requirements = GetMaterialRequirements();
        if (requirements == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.load-blueprint-first"));
        var text = requirements.Count == 0 ? "none" : string.Join(" | ", requirements.Select(x => $"{x.Code} {x.Completed}/{x.Required} (missing {x.Missing})"));
        return TextCommandResult.Success("[Buildwright] Materials: " + text);
    }

    private TextCommandResult MaterialSortCommand(TextCommandCallingArgs args)
    {
        materialSort = args[0]?.ToString()?.ToLowerInvariant() switch
        {
            "required" => MaterialCounter.SortMode.Required,
            "missing" => MaterialCounter.SortMode.Missing,
            "completed" => MaterialCounter.SortMode.Completed,
            "name" => MaterialCounter.SortMode.Name,
            _ => materialSort
        };
        if (args[0]?.ToString()?.ToLowerInvariant() is not ("name" or "required" or "missing" or "completed"))
            return TextCommandResult.Error("[Buildwright] Sort must be name, required, missing or completed.");
        return MaterialsCommand();
    }

    private TextCommandResult ExportMaterials(string target, string? path = null)
    {
        var rows = GetMaterialRequirements();
        if (rows == null) return TextCommandResult.Error("[Buildwright] " + BuildwrightLang.Get("chat.load-blueprint-first"));
        var text = "Buildwright material list\n" + string.Join(Environment.NewLine,
            rows.Select(row => $"{row.Code}\trequired={row.Required}\tavailable={row.Available}\tmissing={row.Missing}\tcompleted={row.Completed}"));
        try
        {
            if (target == "copy")
            {
                capi?.Forms.SetClipboardText(text);
                return TextCommandResult.Success("[Buildwright] " + BuildwrightLang.Get("chat.materials-copied"));
            }
            var destination = Path.GetFullPath(path!);
            if (!destination.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) destination += ".txt";
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, text);
            return TextCommandResult.Success($"[Buildwright] Material list exported to {destination}.");
        }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Material export failed: {ex.Message}"); }
    }

    private List<MaterialRequirement>? GetMaterialRequirements()
    {
        if (capi == null || activeSchematic == null) return null;
        var codes = new List<string?>();
        for (var i = 0; i < activeSchematic.Indices.Count && i < activeSchematic.BlockIds.Count; i++)
            if (activeSchematic.BlockCodes.TryGetValue(activeSchematic.BlockIds[i], out var code)
                && !BlueprintBlockFilter.IsTransient(capi.World.GetBlock(code))) codes.Add(code.ToString());
        var requirements = MaterialCounter.CountRequired(codes, activeMode);
        var stacks = new List<(string Code, int Count)>();
        var manager = capi.World.Player?.InventoryManager;
        if (manager != null)
        {
            var inventories = new[]
            {
                manager.GetHotbarInventory(),
                manager.GetOwnInventory(GlobalConstants.backpackInvClassName)
            }.Where(inventory => inventory != null).Distinct();
            foreach (var inventory in inventories)
            foreach (var slot in inventory)
            {
                var code = slot.Itemstack?.Collectible?.Code?.ToString();
                if (!string.IsNullOrWhiteSpace(code)) stacks.Add((code, slot.StackSize));
            }
        }
        MaterialCounter.MergeInventory(stacks, requirements, activeMode);
        MaterialCounter.Sort(requirements, materialSort);
        return requirements;
    }

    private TextCommandResult RestoreCommand(TextCommandCallingArgs args)
    {
        if (repository == null) return TextCommandResult.Error("[Buildwright] Repository unavailable.");
        try { repository.Restore((string)args[0]); return TextCommandResult.Success("[Buildwright] Backup restored."); }
        catch (Exception ex) { return TextCommandResult.Error($"[Buildwright] Restore failed: {ex.Message}"); }
    }

    private void ListBlueprints()
    {
        if (capi == null || repository == null) return;
        var list = repository.List().OrderByDescending(x => x.ModifiedUtc).ToList();
        capi.ShowChatMessage(list.Count == 0
            ? "[Buildwright] No local blueprints."
            : "[Buildwright] " + string.Join(" | ", list.Select(x => $"{x.Name} {x.Dimensions}")));
    }

    private void OpenLibrary()
    {
        if (capi == null) return;
        if (libraryDialog?.IsOpened() == true) { libraryDialog.TryClose(); return; }
        try
        {
            libraryDialog?.Dispose();
            libraryDialog = new BlueprintLibraryDialog(capi, this);
            libraryDialog.TryOpen();
        }
        catch (Exception exception)
        {
            libraryDialog?.Dispose();
            libraryDialog = null;
            capi.Logger.Error("[buildwright] Failed to open blueprint library: {0}", exception);
            capi.ShowChatMessage("[Buildwright] Blueprint library could not be opened. See client-main.log.");
        }
    }

    private void OpenWorkspace()
    {
        if (capi == null) return;
        if (workspaceDialog?.IsOpened() == true)
        {
            workspaceDialog.TryClose();
            CloseRegionOperationsWindow();
            return;
        }
        try
        {
            workspaceDialog?.Dispose();
            workspaceDialog = new BuildwrightWorkspaceDialog(capi, this, BuildwrightWorkspacePage.Dashboard);
            workspaceDialog.TryOpen();
            EnsureRegionOperationsWindow();
        }
        catch (Exception exception)
        {
            workspaceDialog?.Dispose();
            workspaceDialog = null;
            capi.Logger.Error("[buildwright] Failed to open workbench: {0}", exception);
            capi.ShowChatMessage("[Buildwright] Workbench could not be opened. See client-main.log.");
        }
    }

    private void EnsureRegionOperationsWindow()
    {
        if (capi == null || !IsCreativeMode) return;
        if (toolDialogs.TryGetValue(BuildwrightWorkspacePage.Region, out var existing) && existing.IsOpened()) return;
        try
        {
            existing?.Dispose();
            var dialog = new BuildwrightWorkspaceDialog(capi, this, BuildwrightWorkspacePage.Region);
            toolDialogs[BuildwrightWorkspacePage.Region] = dialog;
            dialog.TryOpen();
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[buildwright] Failed to open region operations window: {0}", exception);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("ui.workbench.open-failed"));
        }
    }

    internal void CloseRegionOperationsWindow()
    {
        if (!toolDialogs.TryGetValue(BuildwrightWorkspacePage.Region, out var dialog)) return;
        dialog.TryClose();
        dialog.Dispose();
        toolDialogs.Remove(BuildwrightWorkspacePage.Region);
    }

    internal BuildwrightWorkspaceState GetWorkspaceState()
    {
        var workspaceRenderer = config.ToolMode == BuildwrightToolMode.Move ? toolMoveRenderer : activeRenderer;
        var selectedDimensions = selection.Dimensions;
        var dimensions = new Int3(selectedDimensions.x, selectedDimensions.y, selectedDimensions.z);
        var cells = (long)dimensions.X * dimensions.Y * dimensions.Z;
        var diff = activeTracker?.Current;
        return new BuildwrightWorkspaceState
        {
            HasSelection = selection.HasSelection,
            Dimensions = dimensions,
            SelectionCells = cells,
            BlueprintName = activeName,
            HasGhost = workspaceRenderer != null,
            GhostLocked = workspaceRenderer is { IsFloating: false },
            RotationDegrees = (workspaceRenderer?.RotationQuarterTurns ?? 0) * 90,
            Mirror = workspaceRenderer?.Mirror ?? MirrorAxis.None,
            DisplayMode = displayMode,
            MatchingMode = activeMode,
            HasTracker = activeTracker != null,
            Progress = diff?.Progress ?? 0,
            Missing = diff?.MissingCount ?? 0,
            Wrong = diff?.WrongCount ?? 0,
            Extra = diff?.ExtraCount ?? 0
        };
    }

    internal void WorkspaceOpenTool(BuildwrightWorkspacePage page)
    {
        if (capi == null || page == BuildwrightWorkspacePage.Dashboard) return;
        if (page == BuildwrightWorkspacePage.Region && !IsCreativeMode) return;
        if (toolDialogs.TryGetValue(page, out var existing) && existing.IsOpened())
        {
            existing.TryClose();
            return;
        }

        try
        {
            existing?.Dispose();
            var dialog = new BuildwrightWorkspaceDialog(capi, this, page);
            toolDialogs[page] = dialog;
            dialog.TryOpen();
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[buildwright] Failed to open {0} tool window: {1}", page, exception);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("ui.workbench.open-failed"));
        }
    }

    internal void WorkspaceOpenMaterials() => OpenBuildInfo(BuildwrightBuildInfoKind.Materials);
    internal void WorkspaceOpenDiff() => OpenBuildInfo(BuildwrightBuildInfoKind.Differences);

    internal List<MaterialRequirement> GetWorkspaceMaterials()
    {
        var requirements = GetMaterialRequirements();
        return requirements?.Select(row => new MaterialRequirement
        {
            Code = row.Code,
            DisplayCode = row.DisplayCode,
            Required = row.Required,
            Available = row.Available,
            Completed = row.Completed
        }).ToList() ?? new List<MaterialRequirement>();
    }

    internal DiffResult? GetWorkspaceDiff() => activeTracker?.Current;

    private void OpenBuildInfo(BuildwrightBuildInfoKind kind)
    {
        if (capi == null) return;
        var existing = kind == BuildwrightBuildInfoKind.Materials ? materialsDialog : diffDialog;
        if (existing?.IsOpened() == true)
        {
            existing.TryClose();
            return;
        }

        try
        {
            existing?.Dispose();
            var dialog = new BuildwrightBuildInfoDialog(capi, this, kind);
            if (kind == BuildwrightBuildInfoKind.Materials) materialsDialog = dialog;
            else diffDialog = dialog;
            dialog.TryOpen();
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[buildwright] Failed to open {0} window: {1}", kind, exception);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("ui.workbench.open-failed"));
        }
    }

    internal void WorkspaceSetCorner1() => SetCorner(1);
    internal void WorkspaceSetCorner2() => SetCorner(2);

    internal void WorkspaceClearSelection()
    {
        ClearMovePreview();
        selection.Clear();
        ClearHighlights();
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.selection-cleared"));
    }

    internal void WorkspaceResize(string direction, int amount, bool grow)
    {
        if (!selection.HasSelection)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.set-corner1-first"));
            return;
        }
        var face = BlockFacing.FromCode(direction.ToLowerInvariant());
        if (face == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.direction-invalid"));
            return;
        }
        if (grow) selection.Grow(face, Math.Max(1, amount));
        else selection.Shrink(face, Math.Max(1, amount));
        if (config.ToolMode == BuildwrightToolMode.Move && toolMoveTarget != null) RebuildMovePreview();
        else UpdateHighlights();
    }

    internal void WorkspaceSaveBlueprint(string name)
    {
        if (capi == null || repository == null) return;
        if (!selection.HasSelection)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.set-both-corners"));
            return;
        }
        try
        {
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            repository.Save(document, name, BlueprintFileKind.BuildwrightZip, overwrite: false);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-saved", name, document.Metadata.Dimensions));
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[buildwright] workbench save failed: {0}", exception);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-save-failed", exception.Message));
        }
    }

    internal void WorkspaceLoadBlueprint(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.enter-blueprint-name"));
            return;
        }
        LoadBlueprintFromLibrary(name.Trim());
    }

    internal void WorkspaceOpenLibrary() => OpenLibrary();

    internal void WorkspaceImportBlueprint(string path, string name)
    {
        if (repository == null || capi == null) return;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Import file does not exist.", fullPath);
            var document = new BlueprintCodec().Read(fullPath);
            var destination = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fullPath) : name.Trim();
            repository.Save(document, destination, BlueprintFileKind.BuildwrightZip, overwrite: true);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-imported", destination));
        }
        catch (Exception exception)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-import-failed", exception.Message));
        }
    }

    internal void WorkspaceExportBlueprint(string name, BlueprintModelFormat? modelFormat = null)
    {
        if (capi == null) return;
        try
        {
            var document = string.IsNullOrWhiteSpace(name)
                ? activeDocument ?? throw new InvalidOperationException(BuildwrightLang.Get("ui.workbench.export-select-first"))
                : LoadDocument(name.Trim());
            if (modelFormat != null)
            {
                capi.ShowChatMessage("[Buildwright] " + BlueprintItemModelExport.Export(capi, ExportDirectory, document, modelFormat.Value));
                return;
            }
            var fullPath = BlueprintExporter.Export(ExportDirectory, document, BlueprintFileKind.NativeJson);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-exported", document.Metadata.Name, fullPath));
        }
        catch (Exception exception)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-export-failed", exception.Message));
        }
    }

    internal void WorkspaceDuplicateBlueprint(string source, string target, bool template)
    {
        if (repository == null || capi == null) return;
        try
        {
            var document = LoadDocument(source.Trim());
            if (template && !document.Metadata.Capabilities.Contains("template", StringComparer.OrdinalIgnoreCase))
                document.Metadata.Capabilities.Add("template");
            repository.Save(document, target.Trim(), BlueprintFileKind.BuildwrightZip, overwrite: false);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-saved", target,
                template ? "template" : "copy"));
        }
        catch (Exception exception)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-save-failed", exception.Message));
        }
    }

    internal void WorkspaceTogglePlacement() => TogglePlace();

    internal void WorkspaceRotate(int quarterTurns)
    {
        var renderer = config.ToolMode == BuildwrightToolMode.Move ? toolMoveRenderer : activeRenderer;
        if (renderer == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
            return;
        }
        renderer.Rotate(quarterTurns);
        if (renderer == toolMoveRenderer) UpdateHighlights();
        else if (!renderer.IsFloating) BuildTracker();
    }

    internal void WorkspaceSetMirror(MirrorAxis mirror)
    {
        var renderer = config.ToolMode == BuildwrightToolMode.Move ? toolMoveRenderer : activeRenderer;
        if (renderer == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.no-active-ghost"));
            return;
        }
        renderer.SetMirror(mirror);
        if (renderer == toolMoveRenderer) UpdateHighlights();
        else if (!renderer.IsFloating) BuildTracker();
    }

    internal void WorkspaceCycleMirror() => CycleMirror();

    internal void WorkspaceMoveRelative(int forward, int right, int up)
    {
        var renderer = config.ToolMode == BuildwrightToolMode.Move ? toolMoveRenderer : activeRenderer;
        if (capi == null || renderer == null || renderer.IsFloating)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.lock-before-move"));
            return;
        }
        renderer.MoveRelative(capi, forward, right, up);
        if (renderer == toolMoveRenderer)
        {
            toolMoveTarget = renderer.Origin.Copy();
            UpdateHighlights();
        }
        else BuildTracker();
    }

    internal void WorkspaceAdjustLayer(int amount) => AdjustLayer(amount);
    internal void WorkspaceToggleHud() => ToggleHud();
    internal void WorkspaceCancel() => Cancel();

    internal void WorkspaceSetOpacity(string value)
    {
        if (!float.TryParse(value, out var alpha))
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.opacity-invalid"));
            return;
        }
        config.GhostAlpha = Math.Clamp(alpha, 0.05f, 1f);
        try { capi?.StoreModConfig(config, "Buildwright.json"); } catch { }
        RebuildGhost();
        if (toolMoveRenderer != null) RebuildMovePreview();
    }

    internal void WorkspaceSetDisplay(string value)
    {
        if (!Enum.TryParse<GhostDisplayMode>(value, true, out var mode)) return;
        displayMode = mode;
        RebuildGhost();
    }

    internal void WorkspaceCycleDisplay()
    {
        displayMode = displayMode switch
        {
            GhostDisplayMode.Full => GhostDisplayMode.Missing,
            GhostDisplayMode.Missing => GhostDisplayMode.Wrong,
            GhostDisplayMode.Wrong => GhostDisplayMode.Difference,
            _ => GhostDisplayMode.Full
        };
        RebuildGhost();
    }

    internal void WorkspaceSetMatchingMode(string value)
    {
        var parsed = ParseMode(value);
        if (!parsed.HasValue) return;
        activeMode = parsed.Value;
        activeRenderer?.SetMatchingMode(activeMode);
        toolMoveRenderer?.SetMatchingMode(activeMode);
        if (activeTracker != null) BuildTracker();
    }

    internal void WorkspaceCopyMaterials()
    {
        if (GetMaterialRequirements() == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.load-blueprint-first"));
            return;
        }
        ExportMaterials("copy");
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.materials-copied"));
    }

    private void WorkspaceCopyMaterials(string text)
    {
        if (capi == null) return;
        capi.Forms.SetClipboardText(text);
        capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.materials-copied"));
    }

    internal void WorkspacePlace(bool onlyMissing, bool clearExtra)
    {
        if (activeDocument == null || activeRenderer == null || worldOperations == null || capi == null)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.load-lock-blueprint"));
            return;
        }
        if (activeRenderer.IsFloating)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.lock-before-world"));
            return;
        }
        var result = worldOperations.Place(activeDocument, activeRenderer.Origin, activeRenderer.RotationQuarterTurns,
            activeRenderer.Mirror, onlyMissing, clearExtra);
        if (!result.Success)
        {
            capi.ShowChatMessage("[Buildwright] " + result.Error);
            return;
        }
        if (result.Queued)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent"));
            return;
        }
        capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.world-operation-result", result.Placed, result.Cleared));
    }

    internal void WorkspaceCloneOrMoveRegion(bool move, int dx, int dy, int dz)
    {
        if (capi == null || worldOperations == null || !selection.HasSelection)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.select-region-first"));
            return;
        }
        try
        {
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            var target = new BlockPos(selection.Anchor!.X + dx, selection.Anchor.Y + dy, selection.Anchor.Z + dz, selection.Anchor.dimension);
            var placed = move
                ? worldOperations.PlaceAndClearSource(document, target, 0, MirrorAxis.None, false, true,
                    selection.Min!, selection.Max!)
                : worldOperations.Place(document, target, 0, MirrorAxis.None, false, true);
            if (!placed.Success) throw new InvalidOperationException(placed.Error);
            if (placed.Queued) { capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent")); return; }
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.region-operation-result",
                move ? "Moved" : "Cloned", placed.Placed, placed.Cleared));
        }
        catch (Exception exception)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.region-operation-failed", exception.Message));
        }
    }

    internal void WorkspaceRotateRegion(int quarterTurns)
    {
        if (capi == null || worldOperations == null || !selection.HasSelection)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.select-region-first"));
            return;
        }
        try
        {
            var turns = ((quarterTurns % 4) + 4) % 4;
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            var placed = worldOperations.PlaceAndClearSource(document, selection.Anchor!, turns, MirrorAxis.None,
                false, true, selection.Min!, selection.Max!);
            if (!placed.Success) throw new InvalidOperationException(placed.Error);
            if (placed.Queued) { capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent")); return; }
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.region-rotated", turns * 90));
        }
        catch (Exception exception)
        {
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.region-rotation-failed", exception.Message));
        }
    }

    internal void WorkspaceMirrorRegion(MirrorAxis mirror)
    {
        if (capi == null || worldOperations == null || !selection.HasSelection)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.select-region-first"));
            return;
        }
        try
        {
            var document = RegionCapture.Capture(capi.World, selection.Min!, selection.Max!, selection.Anchor!, selection.AnchorFace);
            var placed = worldOperations.PlaceAndClearSource(document, selection.Anchor!, 0, mirror, false, true,
                selection.Min!, selection.Max!);
            if (!placed.Success) throw new InvalidOperationException(placed.Error);
            if (placed.Queued) { capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.server-request-sent")); return; }
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.region-mirrored", mirror));
        }
        catch (Exception exception)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.region-mirror-failed", exception.Message));
        }
    }

    internal void WorkspaceClearRegion()
    {
        if (worldOperations == null || !selection.HasSelection)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.select-region-first"));
            return;
        }
        var result = worldOperations.ClearRegion(selection.Min!, selection.Max!);
        capi?.ShowChatMessage(result.Success
            ? "[Buildwright] " + (result.Queued
                ? BuildwrightLang.Get("chat.server-request-sent")
                : BuildwrightLang.Get("chat.region-cleared", result.Cleared))
            : "[Buildwright] " + result.Error);
    }

    internal List<BlueprintSummary> GetBlueprintSummaries(string query, int sortMode)
    {
        if (repository == null) return new List<BlueprintSummary>();
        IEnumerable<BlueprintSummary> list = repository.ListAll();
        if (!string.IsNullOrWhiteSpace(query))
            list = list.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        list = sortMode switch
        {
            1 => list.OrderByDescending(x => x.BlockCount).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            2 => list.OrderByDescending(x => x.MaterialCount).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            3 => list.OrderByDescending(x => x.ModifiedUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        };
        return list.ToList();
    }

    internal List<BlueprintLibraryEntry> GetBlueprintLibraryEntries(string directory, string query, int sortMode)
    {
        if (repository == null) return new List<BlueprintLibraryEntry>();
        IEnumerable<BlueprintLibraryEntry> list = repository.ListEntries(directory);
        if (!string.IsNullOrWhiteSpace(query))
            list = list.Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Summary?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

        var folders = list.Where(entry => entry.IsDirectory).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        var files = list.Where(entry => !entry.IsDirectory);
        IOrderedEnumerable<BlueprintLibraryEntry> orderedFiles = sortMode switch
        {
            1 => files.OrderByDescending(entry => entry.Summary?.BlockCount ?? 0).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            2 => files.OrderByDescending(entry => entry.Summary?.MaterialCount ?? 0).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            3 => files.OrderByDescending(entry => entry.ModifiedUtc).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        };
        return folders.Concat(orderedFiles).ToList();
    }

    internal LibraryOperationResult LibraryCreateFolder(string directory, string name)
    {
        if (repository == null) return LibraryOperationResult.Fail("蓝图库不可用。");
        try
        {
            repository.CreateDirectory(directory, name);
            return LibraryOperationResult.Ok($"已创建文件夹“{name.Trim()}”。");
        }
        catch (Exception exception) { return LibraryOperationResult.Fail("创建文件夹失败：" + exception.Message); }
    }

    internal LibraryOperationResult LibraryCopy(string source, string destinationDirectory)
    {
        if (repository == null) return LibraryOperationResult.Fail("蓝图库不可用。");
        try
        {
            var copied = repository.Copy(source, destinationDirectory);
            return LibraryOperationResult.Ok($"已粘贴为“{copied}”。");
        }
        catch (Exception exception) { return LibraryOperationResult.Fail("粘贴失败：" + exception.Message); }
    }

    internal LibraryOperationResult LibraryRename(string path, string newName)
    {
        if (repository == null) return LibraryOperationResult.Fail("蓝图库不可用。");
        try
        {
            var renamed = repository.Rename(path, newName);
            return LibraryOperationResult.Ok($"已重命名为“{renamed}”。");
        }
        catch (Exception exception) { return LibraryOperationResult.Fail("重命名失败：" + exception.Message); }
    }

    internal LibraryOperationResult LibraryDelete(string path)
    {
        if (repository == null) return LibraryOperationResult.Fail("蓝图库不可用。");
        try
        {
            repository.DeletePath(path, recursive: true);
            return LibraryOperationResult.Ok("已删除所选项目。");
        }
        catch (Exception exception) { return LibraryOperationResult.Fail("删除失败：" + exception.Message); }
    }

    private static string ExportDirectory => Path.Combine(GamePaths.DataPath, "Buildwright", "Exports");

    internal void OpenExportDirectory()
    {
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExportDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("ui.library.open-exports-failed", exception.Message));
        }
    }

    internal LibraryOperationResult ExportBlueprintFile(string path, BlueprintFileKind kind = BlueprintFileKind.BuildwrightZip,
        BlueprintModelFormat? modelFormat = null)
    {
        if (repository == null || capi == null) return LibraryOperationResult.Fail(BuildwrightLang.Get("ui.library.export-unavailable"));
        try
        {
            var document = repository.LoadPath(path);
            if (modelFormat != null) return LibraryOperationResult.Ok(BlueprintItemModelExport.Export(capi, ExportDirectory, document, modelFormat.Value));
            var output = BlueprintExporter.Export(ExportDirectory, document, kind);
            return LibraryOperationResult.Ok(BuildwrightLang.Get("ui.library.exported", output));
        }
        catch (Exception exception)
        {
            capi.Logger.Error("[buildwright] Failed to export blueprint: {0}", exception);
            return LibraryOperationResult.Fail(BuildwrightLang.Get("ui.library.export-failed", exception.Message));
        }
    }

    internal LibraryOperationResult ReplaceBlueprintMaterial(string path, string sourceCode, string targetCode)
    {
        if (repository == null) return LibraryOperationResult.Fail("蓝图库不可用。");
        try
        {
            var document = repository.LoadPath(path);
            var replacements = BlueprintEditor.ReplaceMaterial(document, sourceCode, targetCode);
            repository.SavePath(path, document, overwrite: true);
            return LibraryOperationResult.Ok(BuildwrightLang.Get("ui.library.edit-replaced", replacements));
        }
        catch (Exception exception) { return LibraryOperationResult.Fail("替换失败：" + exception.Message); }
    }

    internal List<BlueprintMaterialOption> GetBlueprintMaterialOptions(string path)
    {
        if (repository == null) return new List<BlueprintMaterialOption>();
        try
        {
            var document = repository.LoadPath(path);
            var codes = document.Schematic["BlockCodes"] as JObject;
            var ids = document.Schematic["BlockIds"] as JArray;
            if (codes == null || ids == null) return new List<BlueprintMaterialOption>();
            var counts = ids.Values<int>().GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
            foreach (var chisel in document.ChiselData)
            foreach (var id in chisel.BlockIds.Distinct()) counts[id] = counts.GetValueOrDefault(id) + 1;
            return codes.Properties()
                .Select(property => (Valid: int.TryParse(property.Name, out var id), Id: id,
                    Code: property.Value.Value<string>() ?? string.Empty))
                .Where(entry => entry.Valid && !string.IsNullOrWhiteSpace(entry.Code) && counts.GetValueOrDefault(entry.Id) > 0)
                .Select(entry =>
                {
                    var count = counts.GetValueOrDefault(entry.Id);
                    var name = GetBlockDisplayName(entry.Code);
                    return new BlueprintMaterialOption(entry.Code,
                        BuildwrightLang.Get("ui.library.edit-material-option", name, count, entry.Code), count);
                })
                .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<BlueprintMaterialOption>(); }
    }

    internal string? SampleAimedBlockCode()
    {
        if (capi?.World.Player?.CurrentBlockSelection?.Position is not { } position) return null;
        var block = capi.World.BlockAccessor.GetBlock(position, BlockLayersAccess.Solid);
        return block?.Id > 0 ? block.Code?.ToString() : null;
    }

    internal string? SampleHeldBlockCode()
    {
        var collectible = capi?.World.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible;
        return collectible is Block block && block.Id > 0 ? block.Code?.ToString() : null;
    }

    internal string GetBlockDisplayName(string code)
    {
        try
        {
            var block = capi?.World.GetBlock(new AssetLocation(code));
            return block == null || block.Id == 0 ? code : new ItemStack(block).GetName();
        }
        catch { return code; }
    }

    internal void LoadBlueprintFromLibrary(string name)
    {
        if (capi == null) return;
        try { StartGhost(name, LoadDocument(name)); }
        catch (Exception ex) { capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-load-failed", ex.Message)); }
    }

    internal void LoadBlueprintFromLibraryPath(string path)
    {
        if (capi == null || repository == null) return;
        try
        {
            var document = repository.LoadPath(path);
            StartGhost(document.Metadata.Name, document);
        }
        catch (Exception ex) { capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-load-failed", ex.Message)); }
    }

    internal void DeleteBlueprintFromLibrary(string name)
    {
        if (repository == null || capi == null) return;
        try
        {
            repository.Delete(name, repository.Exists(name) ? BlueprintFileKind.BuildwrightZip : BlueprintFileKind.NativeJson);
            capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-deleted", name));
        }
        catch (Exception ex) { capi.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.blueprint-delete-failed", ex.Message)); }
    }

    private void AdjustLayer(int amount)
    {
        if (activeRenderer == null) return;
        if (amount < 0) activeRenderer.DecreaseLayer(); else activeRenderer.IncreaseLayer();
    }

    private void ToggleHud()
    {
        if (activeHud == null) { capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.lock-ghost-first")); return; }
        progressHudHidden = !progressHudHidden;
        if (progressHudHidden) activeHud.TryClose(); else activeHud.TryOpen();
    }

    private void Cancel()
    {
        ClearGhost();
        selection.Clear();
        ClearHighlights();
        capi?.ShowChatMessage("[Buildwright] " + BuildwrightLang.Get("chat.cancelled"));
    }

    private void ClearGhost()
    {
        ClearMovePreview();
        ClearTracker();
        activeRenderer?.Dispose();
        activeRenderer = null;
        activeMesh = null;
        activeSchematic = null;
        activeName = null;
        activeAnchorOffset = new Int3();
        activeDocument = null;
        gridStart = null;
        gridEnd = null;
    }

    private void ClearTracker()
    {
        progressHudHidden = false;
        if (activeHud != null)
        {
            if (activeHud.IsOpened()) activeHud.TryClose();
            activeHud.Dispose();
            activeHud = null;
        }
        activeTracker?.Dispose();
        activeTracker = null;
        if (capi != null && tickListener != -1)
        {
            capi.Event.UnregisterGameTickListener(tickListener);
            tickListener = -1;
        }
    }

    private void UpdateHighlights()
    {
        if (capi == null || !selection.HasSelection) { ClearHighlights(); return; }
        var min = selection.Min!;
        var max = selection.Max!;
        var positions = new List<BlockPos>
        {
            min.Copy(),
            new(max.X + 1, max.Y + 1, max.Z + 1, min.dimension)
        };
        capi.World.HighlightBlocks(capi.World.Player, SelectionHighlightSlot, positions,
            new List<int> { ColorUtil.ColorFromRgba(245, 250, 255, 45) },
            EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
        var corner1 = selection.GetCorner(1);
        var corner2 = selection.GetCorner(2);
        var selectedCorner = selection.GetCorner(selection.SelectedCorner);
        var cornersOverlap = corner1 != null && corner2 != null && corner1.Equals(corner2);
        SetCornerHighlight(SelectionAnchorHighlightSlot, corner1, ColorUtil.ColorFromRgba(255, 70, 70, 230),
            selection.SelectedCorner != 1 && !cornersOverlap);
        SetCornerHighlight(SelectionCorner2HighlightSlot, corner2, ColorUtil.ColorFromRgba(70, 150, 255, 230),
            selection.SelectedCorner != 2 && !cornersOverlap);
        SetCornerHighlight(SelectionSelectedCornerHighlightSlot, selectedCorner,
            ColorUtil.ColorFromRgba(70, 235, 120, 235), selectedCorner != null);
        selectionRenderer?.SetBounds(min, max);
        UpdateMoveTargetHighlights();
    }

    private void SetCornerHighlight(int slot, BlockPos? corner, int color, bool visible)
    {
        if (capi == null) return;
        capi.World.HighlightBlocks(capi.World.Player, slot,
            visible && corner != null ? new List<BlockPos> { corner } : new List<BlockPos>(),
            visible && corner != null ? new List<int> { color } : new List<int>(),
            EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
    }

    private static List<BlockPos> BuildSelectionHighlightPositions(BlockPos min, BlockPos max)
    {
        return SelectionHighlightSampler.Build(new Int3(min.X, min.Y, min.Z), new Int3(max.X, max.Y, max.Z),
                MaxHighlightCells)
            .Select(point => new BlockPos(point.X, point.Y, point.Z, min.dimension))
            .ToList();
    }

    private void UpdateMoveTargetHighlights()
    {
        if (capi == null) return;
        if (config.ToolMode != BuildwrightToolMode.Move || toolMoveTarget == null || toolMoveDocument == null ||
            toolMoveRenderer == null || toolMoveRenderer.IsFloating)
        {
            ClearMoveTargetHighlights();
            return;
        }

        var (min, max) = BlueprintTransforms.BoxToWorldBounds(toolMoveDocument.Metadata.Dimensions,
            toolMoveDocument.Metadata.AnchorOffset, new Int3(toolMoveTarget.X, toolMoveTarget.Y, toolMoveTarget.Z),
            toolMoveRenderer.RotationQuarterTurns, toolMoveRenderer.Mirror);
        var targetMin = new BlockPos(min.X, min.Y, min.Z, toolMoveTarget.dimension);
        var targetMax = new BlockPos(max.X, max.Y, max.Z, toolMoveTarget.dimension);
        var positions = BuildSelectionHighlightPositions(targetMin, targetMax);
        capi.World.HighlightBlocks(capi.World.Player, MoveTargetHighlightSlot, positions,
            positions.Select(_ => ColorUtil.ColorFromRgba(255, 190, 60, 85)).ToList(),
            EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        capi.World.HighlightBlocks(capi.World.Player, MoveTargetAnchorHighlightSlot,
            new List<BlockPos> { toolMoveTarget }, new List<int> { ColorUtil.ColorFromRgba(255, 210, 60, 230) },
            EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
    }

    private void ClearMoveTargetHighlights()
    {
        if (capi == null) return;
        var empty = new List<BlockPos>();
        capi.World.HighlightBlocks(capi.World.Player, MoveTargetHighlightSlot, empty, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        capi.World.HighlightBlocks(capi.World.Player, MoveTargetAnchorHighlightSlot, empty, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
    }

    private void ClearHighlights()
    {
        selectionRenderer?.Clear();
        if (capi == null) return;
        var empty = new List<BlockPos>();
        capi.World.HighlightBlocks(capi.World.Player, SelectionHighlightSlot, empty, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        capi.World.HighlightBlocks(capi.World.Player, SelectionAnchorHighlightSlot, empty, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        capi.World.HighlightBlocks(capi.World.Player, SelectionSelectedCornerHighlightSlot, empty, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        capi.World.HighlightBlocks(capi.World.Player, SelectionCorner2HighlightSlot, empty, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        capi.World.HighlightBlocks(capi.World.Player, MoveTargetHighlightSlot, empty, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        capi.World.HighlightBlocks(capi.World.Player, MoveTargetAnchorHighlightSlot, empty, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
    }

    private static void EnsureCollections(BlockSchematic schematic)
    {
        schematic.BlockCodes ??= new Dictionary<int, AssetLocation>();
        schematic.ItemCodes ??= new Dictionary<int, AssetLocation>();
        schematic.Indices ??= new List<uint>();
        schematic.BlockIds ??= new List<int>();
        schematic.DecorIndices ??= new List<uint>();
        schematic.DecorIds ??= new List<long>();
        schematic.BlockEntities ??= new Dictionary<uint, string>();
        schematic.Entities ??= new List<string>();
    }

    public override void Dispose()
    {
        workspaceDialog?.Dispose();
        workspaceDialog = null;
        foreach (var dialog in toolDialogs.Values) dialog.Dispose();
        toolDialogs.Clear();
        materialsDialog?.Dispose();
        materialsDialog = null;
        diffDialog?.Dispose();
        diffDialog = null;
        libraryDialog?.Dispose();
        libraryDialog = null;
        if (capi != null) capi.Input.InWorldAction -= OnInWorldAction;
        if (capi != null)
        {
            capi.Event.MouseDown -= OnMouseDown;
            capi.Event.MouseWheelMove -= OnMouseWheelMove;
        }
        if (controlHudTickListener != -1 && capi != null)
        {
            capi.Event.UnregisterGameTickListener(controlHudTickListener);
            controlHudTickListener = -1;
        }
        controlHud?.TryClose();
        controlHud?.Dispose();
        controlHud = null;
        selectionRenderer?.Dispose();
        selectionRenderer = null;
        ClearGhost();
        ClearHighlights();
        capi = null;
        Windows?.Dispose();
        base.Dispose();
    }
}

internal readonly record struct LibraryOperationResult(bool Success, string Message)
{
    public static LibraryOperationResult Ok(string message) => new(true, message);
    public static LibraryOperationResult Fail(string message) => new(false, message);
}
