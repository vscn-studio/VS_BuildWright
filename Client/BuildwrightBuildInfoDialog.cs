using System.Numerics;
using ImGuiNET;
using Buildwright.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Client;

namespace Buildwright.Client;

internal enum BuildwrightBuildInfoKind
{
    Materials,
    Differences
}

internal enum BuildwrightDiffFilter
{
    All,
    Missing,
    Wrong,
    Extra
}

/// <summary>
/// A focused table for build materials or the current blueprint/world diff.
/// Snapshots refresh on explicit controls to keep tracker work off the render loop.
/// </summary>
internal sealed class BuildwrightBuildInfoDialog : BuildwrightImGuiDialog
{
    private const int RowsPerPage = 15;
    private readonly BuildwrightModSystem owner;
    private readonly BuildwrightBuildInfoKind kind;
    private readonly string key;
    private readonly Dictionary<string, ItemStack?> stackCache = new(StringComparer.OrdinalIgnoreCase);
    private int pageIndex;
    private int materialSort;
    private BuildwrightDiffFilter diffFilter;

    private List<MaterialRequirement> materialRows = [];
    private DiffResult? currentDiff;
    private List<DiffCell> diffCells = [];
    protected override string WindowId => key;
    protected override string WindowTitle => L(kind == BuildwrightBuildInfoKind.Materials ? "ui.materials.title" : "ui.diff.title");
    protected override Vector2 DefaultSize => new(900, 620);

    public BuildwrightBuildInfoDialog(ICoreClientAPI capi, BuildwrightModSystem owner, BuildwrightBuildInfoKind kind) : base(capi)
    {
        this.owner = owner;
        this.kind = kind;
        key = kind == BuildwrightBuildInfoKind.Materials ? "buildwright-materials" : "buildwright-diff";
        RefreshData();
    }

    private void RefreshData()
    {
        materialRows = SortMaterials(owner.GetWorkspaceMaterials()).ToList();
        currentDiff = owner.GetWorkspaceDiff();
        diffCells = currentDiff == null ? [] : FilterCells(currentDiff.Cells).ToList();
    }

    protected override void DrawContent()
    {
        int count = kind == BuildwrightBuildInfoKind.Materials ? materialRows.Count : diffCells.Count;
        int pageCount = Math.Max(1, (count + RowsPerPage - 1) / RowsPerPage);
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);
        if (kind == BuildwrightBuildInfoKind.Materials) DrawMaterials(); else DrawDifferences();
        ImGui.Separator();
        ImGui.BeginDisabled(pageIndex == 0);
        Button("ui.library.previous", () => PreviousPage());
        ImGui.EndDisabled(); ImGui.SameLine();
        Text(BuildwrightLang.Get("ui.library.page", pageIndex + 1, pageCount)); ImGui.SameLine();
        ImGui.BeginDisabled(pageIndex >= pageCount - 1);
        Button("ui.library.next", () => NextPage());
        ImGui.EndDisabled(); ImGui.SameLine();
        Button("ui.materials.refresh", RefreshData);
    }

    private void DrawMaterials()
    {
        Text(BuildwrightLang.Get("ui.materials.summary", materialRows.Sum(r => r.Completed), materialRows.Sum(r => r.Required), materialRows.Sum(r => r.Missing)));
        Button("ui.materials.copy", () => CopyMaterials()); ImGui.SameLine();
        Button("ui.materials.hud", () => ToggleHud());
        string[] headers = ["name", "required", "available", "missing", "done"];
        Combo("ui.materials.sort", headers.Select(h => L("ui.materials.header." + h)).ToArray(), materialSort, i => SortMaterialsBy(i));
        if (!ImGui.BeginTable("materials", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable)) return;
        try
        {
            for (int i = 0; i < headers.Length; i++)
                ImGui.TableSetupColumn(L("ui.materials.header." + headers[i]), ImGuiTableColumnFlags.WidthStretch, i == 0 ? 4 : 1);
            ImGui.TableHeadersRow();
            foreach (var row in materialRows.Skip(pageIndex * RowsPerPage).Take(RowsPerPage))
            {
                string code = row.DisplayCode.Length > 0 ? row.DisplayCode : row.Code;
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                Text(CollectibleLabel(ResolveStack(code), code));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Required.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Available.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Missing.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{row.Completed}/{row.Required}");
            }
        }
        finally { ImGui.EndTable(); }
    }

    private void DrawDifferences()
    {
        Text(currentDiff == null ? L("ui.diff.waiting") : BuildwrightLang.Get("ui.diff.summary", currentDiff.Progress.ToString("P0"), currentDiff.MissingCount, currentDiff.WrongCount, currentDiff.ExtraCount));
        string[] filters = ["all", "missing", "wrong", "extra"];
        Combo("ui.diff.filter", filters.Select(f => L("ui.diff." + f)).ToArray(), (int)diffFilter, i => SetDiffFilter((BuildwrightDiffFilter)i));
        if (!ImGui.BeginTable("differences", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable)) return;
        try
        {
            foreach (var column in new[] { "kind", "position", "expected", "actual" }) ImGui.TableSetupColumn(L("ui.diff.header." + column));
            ImGui.TableHeadersRow();
            foreach (var cell in diffCells.Skip(pageIndex * RowsPerPage).Take(RowsPerPage))
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn(); Text(DiffKindText(cell.Kind));
                ImGui.TableNextColumn(); Text(DisplayPosition(cell.Position));
                ImGui.TableNextColumn(); Text(CollectibleLabel(ResolveStack(cell.ExpectedCode), cell.ExpectedCode));
                ImGui.TableNextColumn(); Text(CollectibleLabel(ResolveStack(cell.ActualCode), cell.ActualCode));
            }
        }
        finally { ImGui.EndTable(); }
    }

    private IEnumerable<MaterialRequirement> SortMaterials(List<MaterialRequirement> rows) => materialSort switch
    {
        1 => rows.OrderByDescending(row => row.Required).ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase),
        2 => rows.OrderByDescending(row => row.Available).ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase),
        3 => rows.OrderByDescending(row => row.Missing).ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase),
        4 => rows.OrderByDescending(row => row.Completed).ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(row => row.Code, StringComparer.OrdinalIgnoreCase)
    };

    private IEnumerable<DiffCell> FilterCells(IEnumerable<DiffCell> cells) => diffFilter switch
    {
        BuildwrightDiffFilter.Missing => cells.Where(cell => cell.Kind == DiffKind.Missing),
        BuildwrightDiffFilter.Wrong => cells.Where(cell => cell.Kind == DiffKind.Wrong),
        BuildwrightDiffFilter.Extra => cells.Where(cell => cell.Kind == DiffKind.Extra),
        _ => cells
    };

    private string DisplayPosition(Int3 position)
    {
        // Vintage Story's player-facing coordinates use the default spawn as
        // the X/Z origin, while the block accessor and highlights use absolute
        // map coordinates internally.
        var spawn = capi.World.DefaultSpawnPosition;
        return $"({position.X - (int)spawn.X},{position.Y},{position.Z - (int)spawn.Z})";
    }

    private ItemStack? ResolveStack(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == "-") return null;
        if (stackCache.TryGetValue(code, out var cached)) return cached;
        ItemStack? stack = null;
        var requested = code.Contains(':') ? new AssetLocation(code) : null;
        var block = requested == null
            ? capi.World.Blocks.FirstOrDefault(value => MatchesCode(value.Code, code))
            : capi.World.Blocks.FirstOrDefault(value => string.Equals(value.Code?.ToString(), requested.ToString(), StringComparison.OrdinalIgnoreCase));
        if (block != null) stack = BlockEntityCapture.CreateDisplayStack(capi, block);
        else
        {
            var item = requested == null
                ? capi.World.Items.FirstOrDefault(value => MatchesCode(value.Code, code))
                : capi.World.Items.FirstOrDefault(value => string.Equals(value.Code?.ToString(), requested.ToString(), StringComparison.OrdinalIgnoreCase));
            if (item != null) stack = new ItemStack(item);
        }
        stackCache[code] = stack;
        return stack;
    }

    private static bool MatchesCode(AssetLocation? candidate, string requested) => candidate != null
        && (string.Equals(candidate.Path, requested, StringComparison.OrdinalIgnoreCase)
            || candidate.Path.StartsWith(requested + "-", StringComparison.OrdinalIgnoreCase));

    private static string CollectibleLabel(ItemStack? stack, string? fallback)
    {
        if (stack == null) return fallback ?? "-";
        return $"{stack.GetName()} ID:{stack.Id}";
    }

    private bool SortMaterialsBy(int sort)
    {
        materialSort = sort;
        pageIndex = 0;
        RefreshData();
        return true;
    }

    private bool SetDiffFilter(BuildwrightDiffFilter filter)
    {
        diffFilter = filter;
        pageIndex = 0;
        RefreshData();
        return true;
    }

    private bool PreviousPage()
    {
        if (pageIndex > 0) pageIndex--;
        return true;
    }

    private bool NextPage()
    {
        pageIndex++;
        return true;
    }

    private bool CopyMaterials()
    {
        owner.WorkspaceCopyMaterials();
        return true;
    }

    private bool ToggleHud()
    {
        owner.WorkspaceToggleHud();
        return true;
    }

    private static string DiffKindText(DiffKind kind) => kind switch
    {
        DiffKind.Missing => BuildwrightLang.Get("ui.diff.missing"),
        DiffKind.Wrong => BuildwrightLang.Get("ui.diff.wrong"),
        DiffKind.Extra => BuildwrightLang.Get("ui.diff.extra"),
        _ => kind.ToString()
    };
}
