namespace Buildwright.Core;

public static class MaterialCounter
{
    public enum SortMode { Name, Required, Missing, Completed }

    public static void Sort(IList<MaterialRequirement> rows, SortMode mode)
    {
        var sorted = mode switch
        {
            SortMode.Required => rows.OrderByDescending(x => x.Required).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase),
            SortMode.Missing => rows.OrderByDescending(x => x.Missing).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase),
            SortMode.Completed => rows.OrderByDescending(x => x.Completed).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
        };
        var values = sorted.ToArray();
        for (var i = 0; i < values.Length; i++) rows[i] = values[i];
    }

    public static List<MaterialRequirement> CountRequired(IEnumerable<string?> codes, MatchingMode mode)
    {
        var map = new Dictionary<string, MaterialRequirement>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            // Vintage Story stores multiblock child cells as synthetic game
            // blocks. They are implementation details of the owning mod and
            // must not appear as separate material requirements.
            if (IsSyntheticMultiblock(code)) continue;
            var key = Key(code, mode);
            if (!map.TryGetValue(key, out var row))
            {
                row = new MaterialRequirement { Code = key, DisplayCode = code };
                map[key] = row;
            }
            row.Required++;
        }
        return map.Values.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsSyntheticMultiblock(string code)
    {
        var separator = code.IndexOf(':');
        var path = separator >= 0 ? code[(separator + 1)..] : code;
        return code.StartsWith("game:", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith("multiblock-monolithic-", StringComparison.OrdinalIgnoreCase);
    }

    public static string Key(string code, MatchingMode mode)
    {
        var path = code.Split(':').Last().ToLowerInvariant();
        if (mode == MatchingMode.Strict) return code.ToLowerInvariant();
        var dash = path.IndexOf('-');
        if (mode == MatchingMode.Loose) return dash > 0 ? path[..dash] : path;
        foreach (var suffix in new[] { "-north", "-south", "-east", "-west", "-up", "-down", "-rotation0", "-rotation1", "-rotation2", "-rotation3" })
            path = path.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase);
        return path;
    }

    public static void MergeInventory(IEnumerable<(string Code, int Count)> stacks, IList<MaterialRequirement> requirements, MatchingMode mode)
    {
        var available = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var stack in stacks)
        {
            var key = Key(stack.Code, mode);
            available[key] = available.GetValueOrDefault(key) + Math.Max(0, stack.Count);
        }
        foreach (var requirement in requirements)
        {
            requirement.Available = available.GetValueOrDefault(requirement.Code);
            requirement.Completed = Math.Min(requirement.Required, requirement.Available);
        }
    }
}
