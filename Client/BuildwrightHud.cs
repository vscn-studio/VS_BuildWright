using Buildwright.Core;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Buildwright.Client;

/// <summary>
/// Build progress HUD. Values are pushed after each tracker scan instead of
/// recomposing a rich-text texture every render frame.
/// </summary>
internal sealed class BuildwrightHud : HudElement
{
    private const string ComposerKey = "buildwright-progress-hud";
    private const int ChatTopOffset = 210;
    private const int HudMaterialOffsetRows = 5;
    private const int ScreenTopMargin = 8;
    private const int MaxMaterialViewportHeight = 620;
    private const int TopPadding = 8;
    private const int BottomPadding = 8;
    private const int TextLineHeight = 19;
    private const int ProgressBarTop = 8;
    private const int ProgressBarHeight = 26;
    private const int MinimumProgressBarStart = 110;
    private const int MaximumProgressBarStart = 360;
    private const int TitleProgressGap = 8;
    private const int ProgressBarMinimumWidth = 140;
    private const int CopyButtonWidth = 52;
    private const int CopyButtonHeight = 28;
    private const int CopyButtonGap = 6;
    private const int MaterialsTop = 48;
    internal const int MaterialRowHeight = 34;
    private const int MaxMaterialRows = 14;
    private readonly BuildwrightTracker tracker;
    private readonly string title;
    private readonly Action<string> copyMaterials;
    private readonly Dictionary<string, ItemStack?> materialStacks = new(StringComparer.OrdinalIgnoreCase);
    private int width;
    private int height;
    private int progressBarStart;
    private int scrollOffset;
    private DiffResult current = new();
    private long lastMaterialRefreshMs;

    // Draw above the chat overlay; the panel is intentionally positioned just
    // above the chat window at the lower-left edge.
    public override double DrawOrder => 0.45;
    public override bool Focusable => false;
    public override bool ShouldReceiveKeyboardEvents() => false;

    public BuildwrightHud(ICoreClientAPI capi, BuildwrightTracker tracker, string title, Action<string> copyMaterials) : base(capi)
    {
        this.tracker = tracker;
        this.title = title;
        this.copyMaterials = copyMaterials;
        Compose();
        Refresh();
    }

    private void Compose()
    {
        width = CalculateWidth();
        height = CalculateHeight();
        progressBarStart = CalculateProgressBarStart();
        // LeftBottom already accounts for the element height. Only offset the
        // bottom edge by the chat window height; subtracting height again would
        // push large material panels off the top of the screen.
        var root = ElementBounds.Fixed(EnumDialogArea.LeftBottom, 0,
            -(ChatTopOffset + HudMaterialOffsetRows * MaterialRowHeight), width, height);
        var content = ElementBounds.Fixed(0, 0, width, height);
        Composers[ComposerKey]?.Dispose();
        Composers.Remove(ComposerKey);
        Composers[ComposerKey] = capi.Gui.CreateCompo(ComposerKey, root)
                .AddStaticCustomDraw(ElementBounds.Fill, BuildwrightUiStyle.DrawHudBackground)
                .BeginChildElements(content)
                .AddStaticText(BuildwrightLang.Get("ui.hud.title", title), BuildwrightUiStyle.SectionFont(),
                    ElementBounds.Fixed(14, TopPadding, progressBarStart - 14 - TitleProgressGap, 22))
                .AddDynamicCustomDraw(ElementBounds.Fixed(progressBarStart, ProgressBarTop,
                    Math.Max(ProgressBarMinimumWidth, width - progressBarStart - 14 - CopyButtonGap - CopyButtonWidth), ProgressBarHeight), DrawProgressBar, "progress-bar")
                .AddBuildwrightButton("Copy",
                    () => { copyMaterials(BuildMaterialClipboardText()); return true; },
                    ElementBounds.Fixed(width - 14 - CopyButtonWidth, ProgressBarTop - 1, CopyButtonWidth, CopyButtonHeight), EnumButtonStyle.None, "copy-materials")
                .AddDynamicText(string.Empty, BuildwrightUiStyle.DetailFont().WithOrientation(EnumTextOrientation.Center),
                    ElementBounds.Fixed(14, MaterialsTop, width - 28, DiffLineCount() * TextLineHeight), "diff-text")
                .AddInteractiveElement(new BuildwrightMaterialList(capi,
                    ElementBounds.Fixed(14, MaterialsY(), width - 28, MaterialViewportHeight()),
                    () => tracker.Materials, () => scrollOffset, value => scrollOffset = value,
                    MaximumScrollOffset, ResolveMaterialStack), "materials")
            .EndChildElements()
            .Compose();
    }

    private int CalculateWidth()
    {
        var progressStart = CalculateProgressBarStart();
        var progressLabel = BuildwrightLang.Get("ui.hud.progress", current.Progress.ToString("P0"),
            current.MatchedCount, current.ExpectedCount);
        var progressWidth = progressStart + Math.Max(ProgressBarMinimumWidth,
            EstimateTextWidth(progressLabel) + 24) + 14;
        progressWidth += CopyButtonGap + CopyButtonWidth;
        var diffWidth = EstimateTextWidth(DiffText()) + 28;
        var materialWidth = tracker.Materials.Count == 0
            ? 0
            : tracker.Materials.Max(row =>
            {
                var stack = ResolveMaterialStack(row.DisplayCode.Length > 0 ? row.DisplayCode : row.Code);
                var name = stack?.GetName() ?? row.Code;
                var id = stack?.Id ?? 0;
                var text = BuildwrightLang.Get("ui.hud.material-missing", name, id, row.Missing);
                return EstimateTextWidth(text) + 60;
            });
        var required = Math.Max(progressWidth, Math.Max(diffWidth, materialWidth));
        var screenWidth = Math.Max(320, (int)Math.Round(capi.Render.FrameWidth / Math.Max(1d, GuiElement.scaled(1))) - 16);
        return Math.Clamp(required, 260, screenWidth);
    }

    private static int EstimateTextWidth(string value)
    {
        var width = 0;
        foreach (var character in value) width += character <= 0x7f ? 8 : 14;
        return width;
    }

    private int CalculateProgressBarStart()
    {
        var text = BuildwrightLang.Get("ui.hud.title", title);
        using var surface = new ImageSurface(Format.Argb32, 1, 1);
        using var context = new Context(surface);
        var font = BuildwrightUiStyle.SectionFont();
        font.SetupContext(context);
        var textWidth = context.TextExtents(text).XAdvance;
        return Math.Clamp(14 + (int)Math.Ceiling(textWidth) + TitleProgressGap,
            MinimumProgressBarStart, MaximumProgressBarStart);
    }

    private int CalculateHeight()
    {
        return MaterialsY() + MaterialViewportHeight() + BottomPadding;
    }

    private int DiffLineCount()
    {
        return Math.Max(1, DiffText().Count(character => character == '\n') + 1);
    }

    private int MaterialsY() => MaterialsTop + DiffLineCount() * TextLineHeight + 4;

    private int MaterialViewportHeight()
    {
        var availableHeight = Math.Max(MaterialRowHeight,
            ScreenLogicalHeight() - ChatTopOffset - ScreenTopMargin - MaterialsY());
        var materialRows = Math.Max(1, tracker.Materials.Count);
        var contentHeight = materialRows * MaterialRowHeight;
        var maximumHeight = Math.Min(MaxMaterialViewportHeight, MaxMaterialRows * MaterialRowHeight);
        return Math.Min(maximumHeight, Math.Min(contentHeight, availableHeight));
    }

    private int ScreenLogicalHeight()
    {
        var frameHeight = capi.Render.FrameHeight;
        return frameHeight > 0
            ? (int)Math.Round(frameHeight / Math.Max(1d, GuiElement.scaled(1)))
            : 720;
    }

    public void Refresh()
    {
        current = tracker.Current;
        if (width != CalculateWidth()
            || height != CalculateHeight())
        {
            Compose();
        }
        ClampScrollOffset();
        var composer = Composers[ComposerKey];
        if (composer == null) return;

        composer.GetDynamicText("diff-text")?.SetNewText(DiffText(current));
        composer.GetCustomDraw("progress-bar")?.Redraw();
        if (capi.ElapsedMilliseconds - lastMaterialRefreshMs >= 250)
        {
            lastMaterialRefreshMs = capi.ElapsedMilliseconds;
            (composer.GetElement("materials") as BuildwrightMaterialList)?.Refresh();
        }
    }

    private int MaximumScrollOffset()
    {
        var visibleRows = Math.Max(1, MaterialViewportHeight() / MaterialRowHeight);
        return Math.Max(0, tracker.Materials.Count - visibleRows);
    }

    private void ClampScrollOffset() => scrollOffset = Math.Clamp(scrollOffset, 0, MaximumScrollOffset());

    internal bool IsMouseOverMaterials()
    {
        var composer = Composers[ComposerKey];
        if (composer == null) return false;
        composer.Bounds.CalcWorldBounds();
        var x = capi.Input.MouseX;
        var y = capi.Input.MouseY;
        var left = composer.Bounds.renderX + GuiElement.scaled(14);
        var top = composer.Bounds.renderY + GuiElement.scaled(MaterialsY());
        return x >= left && x <= left + GuiElement.scaled(width - 28)
            && y >= top && y <= top + GuiElement.scaled(MaterialViewportHeight());
    }

    private void DrawProgressBar(Context context, ImageSurface surface, ElementBounds bounds)
    {
        _ = surface;
        var barWidth = bounds.OuterWidth;
        var barHeight = bounds.OuterHeight;
        context.SetSourceRGBA(0.08, 0.10, 0.13, 0.94);
        context.Rectangle(0, 0, barWidth, barHeight);
        context.Fill();
        var fillWidth = barWidth * Math.Clamp(current.Progress, 0d, 1d);
        if (fillWidth > 0)
        {
            context.SetSourceRGBA(0.34, 0.78, 0.48, 0.82);
            context.Rectangle(0, 0, fillWidth, barHeight);
            context.Fill();
        }

        var label = BuildwrightLang.Get("ui.hud.progress", current.Progress.ToString("P0"), current.MatchedCount, current.ExpectedCount);
        var font = BuildwrightUiStyle.BodyFont().WithOrientation(EnumTextOrientation.Center);
        font.SetupContext(context);
        var extents = context.TextExtents(label);
        var x = (barWidth - extents.XAdvance) / 2d - extents.XBearing;
        var y = (barHeight - context.FontExtents.Height) / 2d + context.FontExtents.Ascent;
        context.SetSourceRGBA(0, 0, 0, 0.8);
        context.MoveTo(x + GuiElement.scaled(1), y + GuiElement.scaled(1));
        context.ShowText(label);
        context.SetSourceRGBA(0.96, 0.98, 1.0, 1.0);
        context.MoveTo(x, y);
        context.ShowText(label);
    }

    private ItemStack? ResolveMaterialStack(string code)
    {
        if (materialStacks.TryGetValue(code, out var cached)) return cached;
        ItemStack? stack = null;
        var requested = code.Contains(':') ? new AssetLocation(code) : null;
        var block = requested == null
            ? FindMaterialBlock(code)
            : FindExactBlock(requested);
        if (block != null) stack = BlockEntityCapture.CreateDisplayStack(capi, block);
        else
        {
            var item = requested == null
                ? FindMaterialItem(code)
                : FindExactItem(requested);
            if (item != null) stack = new ItemStack(item);
        }
        materialStacks[code] = stack;
        return stack;
    }

    private Block? FindMaterialBlock(string code)
    {
        return capi.World.Blocks
            .Where(value => MatchesMaterialCode(value.Code, code))
            .OrderBy(value => string.Equals(value.Code?.Domain, "game", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(value => value.Code?.Path.Length ?? 0)
            .FirstOrDefault();
    }

    private Item? FindMaterialItem(string code)
    {
        return capi.World.Items
            .Where(value => MatchesMaterialCode(value.Code, code))
            .OrderBy(value => string.Equals(value.Code?.Domain, "game", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(value => value.Code?.Path.Length ?? 0)
            .FirstOrDefault();
    }

    private Block? FindExactBlock(AssetLocation requested)
    {
        return capi.World.Blocks.FirstOrDefault(value =>
            string.Equals(value.Code?.ToString(), requested.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private Item? FindExactItem(AssetLocation requested)
    {
        return capi.World.Items.FirstOrDefault(value =>
            string.Equals(value.Code?.ToString(), requested.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesMaterialCode(AssetLocation? candidate, string requested)
    {
        if (candidate == null) return false;
        return string.Equals(candidate.Path, requested, StringComparison.OrdinalIgnoreCase)
            || candidate.Path.StartsWith(requested + "-", StringComparison.OrdinalIgnoreCase);
    }

    public override void Dispose()
    {
        base.Dispose();
    }

    private string DiffText(DiffResult current)
    {
        return BuildwrightLang.Get("ui.hud.diff",
            current.MissingCount, current.WrongCount, current.ExtraCount, current.UnknownCount);
    }

    private string DiffText() => DiffText(tracker.Current);

    private string BuildMaterialClipboardText()
    {
        var rows = tracker.Materials;
        if (rows.Count == 0) return BuildwrightLang.Get("ui.hud.no-materials");
        var lines = new List<string> { $"Buildwright material list: {title}" };
        foreach (var row in rows)
        {
            var requestedCode = row.DisplayCode.Length > 0 ? row.DisplayCode : row.Code;
            var stack = ResolveMaterialStack(requestedCode);
            var name = stack?.GetName() ?? row.Code;
            var id = stack?.Id ?? 0;
            var code = stack?.Collectible?.Code?.ToString() ?? requestedCode;
            lines.Add($"{name} ID:{id} Code:{code} required={row.Required} available={row.Available} missing={row.Missing} completed={row.Completed}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class BuildwrightMaterialList : GuiElementControl
{
    private readonly System.Func<IReadOnlyList<MaterialRequirement>> rowsProvider;
    private readonly System.Func<int> scrollProvider;
    private readonly Action<int> scrollChanged;
    private readonly System.Func<int> maximumScrollProvider;
    private readonly System.Func<string, ItemStack?> stackResolver;
    private readonly LoadedTexture textTexture;
    private bool draggingScrollbar;

    public BuildwrightMaterialList(ICoreClientAPI capi, ElementBounds bounds,
        System.Func<IReadOnlyList<MaterialRequirement>> rowsProvider, System.Func<int> scrollProvider,
        Action<int> scrollChanged, System.Func<int> maximumScrollProvider,
        System.Func<string, ItemStack?> stackResolver) : base(capi, bounds)
    {
        this.rowsProvider = rowsProvider;
        this.scrollProvider = scrollProvider;
        this.scrollChanged = scrollChanged;
        this.maximumScrollProvider = maximumScrollProvider;
        this.stackResolver = stackResolver;
        textTexture = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context context, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        RebuildTextTexture();
    }

    public void Refresh() => RebuildTextTexture();

    public override void RenderInteractiveElements(float deltaTime)
    {
        Bounds.CalcWorldBounds();
        api.Render.Render2DTexturePremultipliedAlpha(textTexture.TextureId, Bounds);
        var rows = rowsProvider();
        if (rows.Count == 0) return;

        var viewportRows = Math.Max(1, (int)(Bounds.OuterHeightInt / Math.Max(1, GuiElement.scaled(BuildwrightHud.MaterialRowHeight))));
        var first = Math.Clamp(scrollProvider(), 0, Math.Max(0, rows.Count - viewportRows));
        var last = Math.Min(rows.Count, first + viewportRows);
        for (var index = first; index < last; index++)
        {
            var stack = stackResolver(rows[index].DisplayCode.Length > 0 ? rows[index].DisplayCode : rows[index].Code);
            if (stack == null) continue;
            var rowY = (index - first) * BuildwrightHud.MaterialRowHeight;
            var iconSize = (float)GuiElement.scaled(22);
            api.Render.RenderItemstackToGui(new DummySlot(stack),
                Bounds.renderX + GuiElement.scaled(13 + 11),
                Bounds.renderY + GuiElement.scaled(rowY + 17),
                100, iconSize, unchecked((int)0xffffffff), true, false, false);
        }
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (rowsProvider().Count == 0 || args.IsHandled || !IsMouseInside(api)) return;
        var amount = args.delta != 0 ? args.delta : Math.Sign(args.deltaPrecise);
        if (amount == 0) return;
        scrollChanged(Math.Clamp(scrollProvider() - Math.Sign(amount), 0, maximumScrollProvider()));
        RebuildTextTexture();
        args.SetHandled(true);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left || maximumScrollProvider() <= 0) return;
        Bounds.CalcWorldBounds();
        var localX = args.X - Bounds.renderX;
        var localY = args.Y - Bounds.renderY;
        var trackX = Bounds.OuterWidth - GuiElement.scaled(8);
        if (localX < trackX) return;
        var rows = rowsProvider();
        var viewportRows = Math.Max(1, (int)(Bounds.OuterHeight / Math.Max(1, GuiElement.scaled(BuildwrightHud.MaterialRowHeight))));
        var thumbHeight = Math.Max(GuiElement.scaled(18), Bounds.OuterHeight * viewportRows / (double)Math.Max(1, rows.Count));
        var thumbY = (Bounds.OuterHeight - thumbHeight) * scrollProvider() / Math.Max(1, maximumScrollProvider());
        if (localY >= thumbY && localY <= thumbY + thumbHeight)
        {
            draggingScrollbar = true;
            args.Handled = true;
        }
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!draggingScrollbar) return;
        Bounds.CalcWorldBounds();
        var rows = rowsProvider();
        var viewportRows = Math.Max(1, (int)(Bounds.OuterHeight / Math.Max(1, GuiElement.scaled(BuildwrightHud.MaterialRowHeight))));
        var thumbHeight = Math.Max(GuiElement.scaled(18), Bounds.OuterHeight * viewportRows / (double)Math.Max(1, rows.Count));
        var travel = Math.Max(1, Bounds.OuterHeight - thumbHeight);
        var localY = Math.Clamp(args.Y - Bounds.renderY - thumbHeight / 2d, 0, travel);
        scrollChanged((int)Math.Round(localY / travel * maximumScrollProvider()));
        RebuildTextTexture();
        args.Handled = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        draggingScrollbar = false;
        base.OnMouseUpOnElement(api, args);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        draggingScrollbar = false;
        base.OnMouseUp(api, args);
    }

    private bool IsMouseInside(ICoreClientAPI api)
    {
        Bounds.CalcWorldBounds();
        return api.Input.MouseX >= Bounds.renderX
            && api.Input.MouseX <= Bounds.renderX + Bounds.OuterWidth
            && api.Input.MouseY >= Bounds.renderY
            && api.Input.MouseY <= Bounds.renderY + Bounds.OuterHeight;
    }

    public override void Dispose()
    {
        textTexture.Dispose();
        base.Dispose();
    }

    private void RebuildTextTexture()
    {
        var width = Math.Max(1, Bounds.OuterWidthInt);
        var height = Math.Max(1, Bounds.OuterHeightInt);
        using var image = new ImageSurface(Format.Argb32, width, height);
        using var context = new Context(image);
        var rows = rowsProvider();
        var rowHeight = GuiElement.scaled(BuildwrightHud.MaterialRowHeight);
        var viewportRows = Math.Max(1, height / Math.Max(1, (int)rowHeight));
        var first = Math.Clamp(scrollProvider(), 0, Math.Max(0, rows.Count - viewportRows));
        var font = BuildwrightUiStyle.DetailFont();
        font.SetupContext(context);
        if (rows.Count == 0)
        {
            DrawText(context, BuildwrightLang.Get("ui.hud.no-materials"), GuiElement.scaled(4), GuiElement.scaled(20), width - GuiElement.scaled(8));
        }
        else
        {
            var last = Math.Min(rows.Count, first + viewportRows);
            for (var index = first; index < last; index++)
            {
                var stack = stackResolver(rows[index].DisplayCode.Length > 0 ? rows[index].DisplayCode : rows[index].Code);
                var name = stack?.GetName() ?? rows[index].Code;
                var id = stack?.Id ?? 0;
                var text = BuildwrightLang.Get("ui.hud.material-missing", name, id, rows[index].Missing);
                DrawFullText(context, text,
                    GuiElement.scaled(56),
                    GuiElement.scaled((index - first) * BuildwrightHud.MaterialRowHeight + 22),
                    width - GuiElement.scaled(60));
            }
        }

        if (maximumScrollProvider() > 0)
        {
            var trackX = width - GuiElement.scaled(4);
            var thumbHeight = Math.Max(GuiElement.scaled(18), height * viewportRows / (double)Math.Max(1, rows.Count));
            var thumbY = (height - thumbHeight) * first / Math.Max(1, maximumScrollProvider());
            context.SetSourceRGBA(0.08, 0.10, 0.13, 0.85);
            context.Rectangle(trackX, 0, GuiElement.scaled(3), height);
            context.Fill();
            context.SetSourceRGBA(0.66, 0.82, 0.94, 0.8);
            context.Rectangle(trackX, thumbY, GuiElement.scaled(3), thumbHeight);
            context.Fill();
        }
        GuiElement.GenerateTexture(api, image, ref textTexture.TextureId);
    }

    private static void DrawFullText(Context context, string text, double x, double y, double maxWidth)
    {
        var fontSize = 12f;
        var font = BuildwrightUiStyle.DetailFont().WithFontSize(fontSize);
        font.SetupContext(context);
        while (fontSize > 8f && context.TextExtents(text).XAdvance > maxWidth)
        {
            fontSize -= 0.5f;
            font = BuildwrightUiStyle.DetailFont().WithFontSize(fontSize);
            font.SetupContext(context);
        }

        context.Save();
        context.Rectangle(x, y - GuiElement.scaled(18), maxWidth, GuiElement.scaled(BuildwrightHud.MaterialRowHeight));
        context.Clip();
        context.SetSourceRGBA(0, 0, 0, 0.72);
        context.MoveTo(x + GuiElement.scaled(1), y + GuiElement.scaled(1));
        context.ShowText(text);
        context.SetSourceRGBA(0.88, 0.91, 0.94, 0.98);
        context.MoveTo(x, y);
        context.ShowText(text);
        context.Restore();
    }

    private static void DrawText(Context context, string text, double x, double y, double maxWidth)
    {
        var font = BuildwrightUiStyle.DetailFont();
        font.SetupContext(context);
        var fitted = FitText(context, text, maxWidth);
        context.SetSourceRGBA(0, 0, 0, 0.72);
        context.MoveTo(x + GuiElement.scaled(1), y + GuiElement.scaled(1));
        context.ShowText(fitted);
        context.SetSourceRGBA(0.88, 0.91, 0.94, 0.98);
        context.MoveTo(x, y);
        context.ShowText(fitted);
    }

    private static string FitText(Context context, string text, double maxWidth)
    {
        if (context.TextExtents(text).XAdvance <= maxWidth) return text;
        const string ellipsis = "...";
        var length = text.Length;
        while (length > 0 && context.TextExtents(text[..length] + ellipsis).XAdvance > maxWidth) length--;
        return length == 0 ? ellipsis : text[..length] + ellipsis;
    }
}
