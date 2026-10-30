using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Buildwright.Client;

/// <summary>Original Cairo presentation retained for the in-game HUDs.</summary>
internal static class BuildwrightUiStyle
{
    // Use a regular CJK family explicitly, independently of the game's Light UI font.
    public static CairoFont HudFont(float size) => new CairoFont(size,
        OperatingSystem.IsWindows() ? "Microsoft YaHei" : OperatingSystem.IsMacOS() ? "PingFang SC" : "Noto Sans CJK SC")
        .WithWeight(FontWeight.Normal).WithColor(new[] { 1.0, 1.0, 1.0, 1.0 });
    public static CairoFont SectionFont() => HudFont(14).WithColor(new[] { 0.90, 0.92, 0.96, 1.0 });
    public static CairoFont BodyFont() => HudFont(13).WithColor(new[] { 0.88, 0.91, 0.94, 0.96 });
    public static CairoFont DetailFont() => HudFont(12).WithColor(new[] { 0.78, 0.82, 0.88, 0.94 });

    public static GuiComposer AddBuildwrightButton(this GuiComposer composer, string text, ActionConsumable action,
        ElementBounds bounds, EnumButtonStyle ignoredStyle, string key, bool highlighted = false)
    {
        composer.AddInteractiveElement(new BuildwrightFlatButton(composer.Api, text, action, bounds, highlighted), key);
        return composer;
    }

    public static void DrawHudBackground(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bounds.CalcWorldBounds();
        ctx.Rectangle(bounds.bgDrawX, bounds.bgDrawY, bounds.OuterWidth, bounds.OuterHeight);
        ctx.SetSourceRGBA(0.02, 0.02, 0.02, 0.34);
        ctx.Fill();
    }

}

internal sealed class BuildwrightFlatButton : GuiElementTextButton
{
    private readonly LoadedTexture normalBackground;
    private readonly LoadedTexture pressedBackground;
    private readonly LoadedTexture disabledBackground;
    private readonly bool highlighted;
    private bool pressed;
    private bool hovered;

    public BuildwrightFlatButton(ICoreClientAPI capi, string text, ActionConsumable clicked, ElementBounds bounds, bool highlighted)
        : base(capi, text,
            BuildwrightUiStyle.HudFont(14).WithOrientation(EnumTextOrientation.Center).WithColor(new[] { 0.96, 0.97, 1.0, 1.0 }),
            BuildwrightUiStyle.HudFont(14).WithOrientation(EnumTextOrientation.Center).WithColor(new[] { 0.02, 0.025, 0.032, 1.0 }),
            clicked, bounds, EnumButtonStyle.None)
    {
        this.highlighted = highlighted;
        normalBackground = new LoadedTexture(capi);
        pressedBackground = new LoadedTexture(capi);
        disabledBackground = new LoadedTexture(capi);
        SetOrientation(EnumTextOrientation.Center);
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();
        ComposeBackground(normalBackground, pressedState: false, disabledState: false);
        ComposeBackground(pressedBackground, pressedState: true, disabledState: false);
        ComposeBackground(disabledBackground, pressedState: false, disabledState: true);
        base.ComposeElements(ctxStatic, surfaceStatic);
    }

    private void ComposeBackground(LoadedTexture texture, bool pressedState, bool disabledState)
    {
        int width = Math.Max(1, Bounds.OuterWidthInt);
        int height = Math.Max(1, Bounds.OuterHeightInt);
        using ImageSurface surface = new(Format.Argb32, width, height);
        using Context context = new(surface);
        context.Rectangle(0, 0, width, height);
        context.SetSourceRGBA(pressedState ? 1.0 : 0.62,
            pressedState ? 1.0 : 0.66,
            pressedState ? 1.0 : 0.72,
            disabledState ? 0.14 : highlighted ? 0.36 : 0.22);
        context.Fill();
        GuiElement.GenerateTexture(api, surface, ref texture.TextureId);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        api.Render.Render2DTexturePremultipliedAlpha((!Enabled ? disabledBackground : pressed || hovered ? pressedBackground : normalBackground).TextureId, Bounds);
        base.RenderInteractiveElements(deltaTime);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        Bounds.CalcWorldBounds();
        hovered = args.X >= Bounds.renderX && args.X <= Bounds.renderX + Bounds.OuterWidth
            && args.Y >= Bounds.renderY && args.Y <= Bounds.renderY + Bounds.OuterHeight;
        base.OnMouseMove(api, args);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        if (Enabled) pressed = true;
    }

    public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
    {
        pressed = false;
        base.OnMouseUpOnElement(api, args);
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        pressed = false;
        base.OnMouseUp(api, args);
    }

    public override void OnFocusLost()
    {
        pressed = false;
        hovered = false;
        base.OnFocusLost();
    }

    public override void Dispose()
    {
        normalBackground.Dispose();
        pressedBackground.Dispose();
        disabledBackground.Dispose();
        base.Dispose();
    }
}
