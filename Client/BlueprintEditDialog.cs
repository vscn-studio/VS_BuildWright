using System.Numerics;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Buildwright.Client;

internal sealed class BlueprintEditDialog : BuildwrightImGuiDialog
{
    private const string Key = "buildwright-material-replace";
    private readonly BuildwrightModSystem owner;
    private readonly string path;
    private readonly Action<string> completed;
    private readonly List<BlueprintMaterialOption> materials;
    private int sourceIndex;
    private string target;
    private string notice = string.Empty;

    protected override string WindowId => Key;
    protected override string WindowTitle => L("ui.library.replace");
    protected override Vector2 DefaultSize => new(600, 340);

    public BlueprintEditDialog(ICoreClientAPI capi, BuildwrightModSystem owner, string path,
        Action<string> completed) : base(capi)
    {
        this.owner = owner;
        this.path = path;
        this.completed = completed;
        materials = owner.GetBlueprintMaterialOptions(path);
        target = owner.SampleHeldBlockCode() ?? owner.SampleAimedBlockCode() ?? string.Empty;
    }

    protected override void DrawContent()
    {
        Text(Path.GetFileName(path));
        Combo("ui.library.edit-source", materials.Select(m => m.Label).ToArray(), sourceIndex, i => sourceIndex = i);
        Input("ui.library.edit-target", ref target);
        Button("ui.library.edit-aim", () => SampleTarget(true)); ImGui.SameLine();
        Button("ui.library.edit-held", () => SampleTarget(false));
        Text(notice);
        ImGui.BeginDisabled(materials.Count == 0);
        Button("ui.library.edit-replace", () => Apply());
        ImGui.EndDisabled(); ImGui.SameLine();
        Button("ui.library.edit-cancel", () => TryClose());
    }

    private bool SampleTarget(bool aimed)
    {
        var code = aimed ? owner.SampleAimedBlockCode() : owner.SampleHeldBlockCode();
        if (string.IsNullOrWhiteSpace(code))
        {
            notice = BuildwrightLang.Get(aimed ? "ui.library.edit-no-aim" : "ui.library.edit-no-held");
        }
        else
        {
            target = code;
            notice = BuildwrightLang.Get("ui.library.edit-target-selected", owner.GetBlockDisplayName(code));
        }
        return true;
    }

    private bool Apply()
    {
        if (materials.Count == 0) return true;
        var source = materials[Math.Clamp(sourceIndex, 0, materials.Count - 1)];
        if (string.IsNullOrWhiteSpace(target))
        {
            notice = BuildwrightLang.Get("ui.library.edit-target-required");
            return true;
        }
        if (string.Equals(source.Code, target, StringComparison.OrdinalIgnoreCase))
        {
            notice = BuildwrightLang.Get("ui.library.edit-same-material");
            return true;
        }
        var result = owner.ReplaceBlueprintMaterial(path, source.Code, target);
        notice = result.Message;
        if (result.Success)
        {
            completed(result.Message);
            TryClose();
        }
        return true;
    }
}

internal sealed record BlueprintMaterialOption(string Code, string Label, int Count);
