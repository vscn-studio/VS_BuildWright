using ImGuiNET;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Common;

namespace Buildwright.Client;

/// <summary>An independent atlas avoids rebuilding or modifying VSImGui's shared fonts.</summary>
internal sealed class BuildwrightImGuiFont : IDisposable
{
    private ImFontAtlasPtr atlas;
    private ImFontPtr font;
    private int texture;
    private bool attempted;
    private bool ready;

    public bool Push(ILogger logger)
    {
        if (!attempted)
        {
            attempted = true;
            try { Load(); }
            catch (Exception e)
            {
                logger.Warning("[Buildwright] Custom font unavailable; using ImGui font: {0}", e.Message);
                Dispose();
            }
        }
        if (!ready) return false;
        ImGui.PushFont(font);
        return true;
    }

    private unsafe void Load()
    {
        string? path = FindFont();
        if (path == null) return;
        atlas = new ImFontAtlasPtr(ImGuiNative.ImFontAtlas_ImFontAtlas());
        font = atlas.AddFontFromFileTTF(path, 18, new ImFontConfigPtr(), atlas.GetGlyphRangesChineseFull());
        atlas.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        int previousActive = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        int previousUnpack = GL.GetInteger(GetPName.UnpackAlignment);
        try
        {
            if (width > GL.GetInteger(GetPName.MaxTextureSize) || height > GL.GetInteger(GetPName.MaxTextureSize))
                throw new InvalidOperationException("Font atlas exceeds GPU texture size limit.");
            texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            atlas.SetTexID(texture);
            atlas.ClearTexData();
            ready = true;
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, previousUnpack);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.ActiveTexture((TextureUnit)previousActive);
        }
    }

    private static string? FindFont()
    {
        string? configured = Environment.GetEnvironmentVariable("BUILDWRIGHT_FONT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string[] paths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simhei.ttf"),
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
            "/System/Library/Fonts/PingFang.ttc"
        ];
        return paths.FirstOrDefault(File.Exists);
    }

    public unsafe void Dispose()
    {
        if (texture != 0) { GL.DeleteTexture(texture); texture = 0; }
        if (atlas.NativePtr != null) { atlas.Destroy(); atlas = default; }
        ready = false;
    }
}
