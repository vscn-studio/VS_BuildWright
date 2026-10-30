namespace Buildwright.Client;

/// <summary>
/// Mirrors Litematica's tool-mode set. The modes describe how the configured
/// in-world tool interprets mouse input; they do not bypass world permissions.
/// </summary>
public enum BuildwrightToolMode
{
    AreaSelection,
    SchematicPlacement,
    Fill,
    ReplaceBlock,
    PasteSchematic,
    GridPaste,
    Move,
    Delete,
    Rebuild,
    Disabled
}

public static class BuildwrightToolModes
{
    public const int Count = 10;

    public static bool IsDisabled(this BuildwrightToolMode mode) => mode == BuildwrightToolMode.Disabled;

    public static bool IsCreativeOnly(this BuildwrightToolMode mode) => mode is
        BuildwrightToolMode.Fill or
        BuildwrightToolMode.ReplaceBlock or
        BuildwrightToolMode.PasteSchematic or
        BuildwrightToolMode.GridPaste or
        BuildwrightToolMode.Move or
        BuildwrightToolMode.Delete;

    public static bool UsesSelection(this BuildwrightToolMode mode) => mode is
        BuildwrightToolMode.AreaSelection or
        BuildwrightToolMode.Fill or
        BuildwrightToolMode.ReplaceBlock or
        BuildwrightToolMode.Move or
        BuildwrightToolMode.Delete;

    public static bool UsesProjection(this BuildwrightToolMode mode) => mode is
        BuildwrightToolMode.SchematicPlacement or
        BuildwrightToolMode.PasteSchematic or
        BuildwrightToolMode.GridPaste or
        BuildwrightToolMode.Rebuild;

    public static bool IsExecutable(this BuildwrightToolMode mode) => mode is
        BuildwrightToolMode.Fill or
        BuildwrightToolMode.ReplaceBlock or
        BuildwrightToolMode.PasteSchematic or
        BuildwrightToolMode.GridPaste or
        BuildwrightToolMode.Move or
        BuildwrightToolMode.Delete or
        BuildwrightToolMode.Rebuild;

    public static string LanguageKey(this BuildwrightToolMode mode) => mode switch
    {
        BuildwrightToolMode.AreaSelection => "ui.tool.mode.area-selection",
        BuildwrightToolMode.SchematicPlacement => "ui.tool.mode.schematic-placement",
        BuildwrightToolMode.Fill => "ui.tool.mode.fill",
        BuildwrightToolMode.ReplaceBlock => "ui.tool.mode.replace-block",
        BuildwrightToolMode.PasteSchematic => "ui.tool.mode.paste-schematic",
        BuildwrightToolMode.GridPaste => "ui.tool.mode.grid-paste",
        BuildwrightToolMode.Move => "ui.tool.mode.move",
        BuildwrightToolMode.Delete => "ui.tool.mode.delete",
        BuildwrightToolMode.Rebuild => "ui.tool.mode.rebuild",
        _ => "ui.tool.mode.disabled"
    };

    public static string HelpLanguageKey(this BuildwrightToolMode mode) => mode switch
    {
        BuildwrightToolMode.AreaSelection => "ui.tool.help.area-selection",
        BuildwrightToolMode.SchematicPlacement => "ui.tool.help.schematic-placement",
        BuildwrightToolMode.Fill => "ui.tool.help.fill",
        BuildwrightToolMode.ReplaceBlock => "ui.tool.help.replace-block",
        BuildwrightToolMode.PasteSchematic => "ui.tool.help.paste-schematic",
        BuildwrightToolMode.GridPaste => "ui.tool.help.grid-paste",
        BuildwrightToolMode.Move => "ui.tool.help.move",
        BuildwrightToolMode.Delete => "ui.tool.help.delete",
        BuildwrightToolMode.Rebuild => "ui.tool.help.rebuild",
        _ => "ui.tool.help.disabled"
    };

    public static string ConfirmLanguageKey(this BuildwrightToolMode mode) => mode switch
    {
        BuildwrightToolMode.Fill => "ui.tool.confirm.fill",
        BuildwrightToolMode.ReplaceBlock => "ui.tool.confirm.replace-block",
        BuildwrightToolMode.PasteSchematic => "ui.tool.confirm.paste-schematic",
        BuildwrightToolMode.GridPaste => "ui.tool.confirm.grid-paste",
        BuildwrightToolMode.Move => "ui.tool.confirm.move",
        BuildwrightToolMode.Delete => "ui.tool.confirm.delete",
        _ => "ui.tool.confirm.rebuild"
    };

    public static BuildwrightToolMode Cycle(this BuildwrightToolMode mode, bool forward, bool creative)
    {
        var values = Enum.GetValues<BuildwrightToolMode>();
        var index = Array.IndexOf(values, mode);
        for (var attempt = 0; attempt < values.Length; attempt++)
        {
            index = (index + (forward ? 1 : -1) + values.Length) % values.Length;
            var next = values[index];
            if (creative || !next.IsCreativeOnly()) return next;
        }
        return mode;
    }
}
