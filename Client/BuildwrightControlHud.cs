using Cairo;
using Vintagestory.API.Client;

namespace Buildwright.Client;

/// <summary>
/// Edge-pinned tool status display. It uses custom drawing so important state
/// can remain readable through deliberate red, green, and blue text accents.
/// </summary>
internal sealed class BuildwrightControlHud : HudElement
{
    private const string ComposerKey = "buildwright-control-hud";
    private const int MaxWidth = 900;
    private const int HorizontalPadding = 20;
    private const int VerticalPadding = 8;

    private readonly Func<BuildwrightControlHudSnapshot> snapshotProvider;
    private BuildwrightControlHudSnapshot snapshot = new(Array.Empty<BuildwrightControlHudLine>());
    private int width;
    private int height;

    public override double DrawOrder => 0.19;
    public override bool Focusable => false;
    public override bool ShouldReceiveKeyboardEvents() => false;

    public BuildwrightControlHud(ICoreClientAPI capi, Func<BuildwrightControlHudSnapshot> snapshotProvider) : base(capi)
    {
        this.snapshotProvider = snapshotProvider;
        snapshot = snapshotProvider();
        Compose();
        Refresh();
    }

    internal double ReservedHeight => height;
    internal double ReservedWidth => width;

    public void Refresh()
    {
        snapshot = snapshotProvider();
        var nextWidth = CalculateWidth(snapshot);
        var nextHeight = CalculateHeight(snapshot);
        if (nextWidth != width || nextHeight != height)
        {
            Compose();
            return;
        }
        Composers[ComposerKey]?.GetCustomDraw("controls")?.Redraw();
    }

    private void Compose()
    {
        width = CalculateWidth(snapshot);
        height = CalculateHeight(snapshot);
        Composers[ComposerKey]?.Dispose();
        Composers.Remove(ComposerKey);

        // Keep the text surface flush with the lower-right screen edges.
        var root = ElementBounds.Fixed(EnumDialogArea.RightBottom, 0, 0, width, height);
        var content = ElementBounds.Fixed(0, 0, width, height);
        Composers[ComposerKey] = capi.Gui.CreateCompo(ComposerKey, root)
            .AddStaticCustomDraw(ElementBounds.Fill, BuildwrightUiStyle.DrawHudBackground)
            .AddDynamicCustomDraw(content, DrawText, "controls")
            .Compose();
    }

    private void DrawText(Context context, ImageSurface surface, ElementBounds bounds)
    {
        _ = surface;
        _ = bounds;
        var xStart = GuiElement.scaled(HorizontalPadding / 2d);
        var maxX = GuiElement.scaled(width - HorizontalPadding / 2d);
        var y = GuiElement.scaled(22);

        foreach (var line in snapshot.Lines)
        {
            var fontSize = line.IsTitle ? 16f : 14f;
            var lineHeight = GuiElement.scaled(line.IsTitle ? 26 : 24);
            var font = BuildwrightUiStyle.HudFont(fontSize);
            font.SetupContext(context);
            var lineX = xStart;
            foreach (var segment in line.Segments)
            {
                var remaining = segment.Text;
                while (remaining.Length > 0)
                {
                    if (lineX >= maxX)
                    {
                        lineX = xStart;
                        y += lineHeight;
                    }

                    var length = FittingPrefixLength(context, remaining, maxX - lineX);
                    if (length == 0)
                    {
                        lineX = xStart;
                        y += lineHeight;
                        continue;
                    }

                    var text = remaining[..length];
                    context.SetSourceRGBA(0.0, 0.0, 0.0, 0.76);
                    context.MoveTo(lineX + GuiElement.scaled(1), y + GuiElement.scaled(1));
                    context.ShowText(text);
                    context.SetSourceRGBA(segment.Color[0], segment.Color[1], segment.Color[2], segment.Color[3]);
                    context.MoveTo(lineX, y);
                    context.ShowText(text);
                    lineX += context.TextExtents(text).XAdvance;
                    remaining = remaining[length..];
                }
            }
            y += lineHeight;
        }
    }

    private static int CalculateWidth(BuildwrightControlHudSnapshot value)
    {
        var maxWidth = value.Lines.Count == 0
            ? 0
            : value.Lines.Max(line => line.Segments.Sum(segment => EstimateTextWidth(segment.Text)));
        return Math.Clamp((int)Math.Ceiling(maxWidth) + HorizontalPadding, HorizontalPadding + 24, MaxWidth);
    }

    private static double EstimateTextWidth(string value)
    {
        var width = 0d;
        foreach (var character in value)
        {
            width += character <= 0x7f ? 8 : 14;
        }
        return width;
    }

    private static int CalculateHeight(BuildwrightControlHudSnapshot value)
    {
        var lineCount = value.Lines.Count == 0 ? 1 : value.Lines.Count;
        return VerticalPadding + 26 + Math.Max(0, lineCount - 1) * 24;
    }

    private static int FittingPrefixLength(Context context, string value, double availableWidth)
    {
        if (availableWidth <= 0 || value.Length == 0) return 0;
        if (context.TextExtents(value).XAdvance <= availableWidth) return value.Length;

        var length = value.Length;
        while (length > 1 && context.TextExtents(value[..length]).XAdvance > availableWidth)
        {
            length--;
        }
        return context.TextExtents(value[..length]).XAdvance <= availableWidth ? length : 0;
    }
}

internal sealed record BuildwrightControlHudSnapshot(IReadOnlyList<BuildwrightControlHudLine> Lines);

internal sealed record BuildwrightControlHudLine(bool IsTitle, IReadOnlyList<BuildwrightControlHudSegment> Segments)
{
    public static BuildwrightControlHudLine Title(string text) => new(true,
        new[] { new BuildwrightControlHudSegment(text, BuildwrightControlHudColors.Blue) });

    public static BuildwrightControlHudLine Normal(params BuildwrightControlHudSegment[] segments) => new(false, segments);
}

internal readonly record struct BuildwrightControlHudSegment(string Text, double[] Color);

internal static class BuildwrightControlHudColors
{
    public static readonly double[] Text = { 0.90, 0.93, 0.97, 0.98 };
    public static readonly double[] Blue = { 0.46, 0.78, 1.00, 0.98 };
    public static readonly double[] Green = { 0.62, 1.00, 0.68, 0.98 };
    public static readonly double[] Red = { 1.00, 0.58, 0.55, 0.98 };
}
