namespace Buildwright.Core;

public interface IDiffEngine
{
    DiffResult Compare(IEnumerable<(Int3 Position, string? ExpectedCode)> expected,
        IReadOnlyDictionary<(int X, int Y, int Z), string?> actual, MatchingMode mode);
}

public sealed class DiffEngine : IDiffEngine
{
    public DiffResult Compare(IEnumerable<(Int3 Position, string? ExpectedCode)> expected,
        IReadOnlyDictionary<(int X, int Y, int Z), string?> actual, MatchingMode mode)
    {
        var result = new DiffResult();
        var expectedPositions = new HashSet<(int X, int Y, int Z)>();
        var normalizedCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in expected)
        {
            var key = (item.Position.X, item.Position.Y, item.Position.Z);
            expectedPositions.Add(key);
            result.ExpectedCount++;
            actual.TryGetValue(key, out var actualCode);
            var kind = Classify(item.ExpectedCode, actualCode, mode, normalizedCodes);
            Add(result, kind);
            if (kind == DiffKind.Matched)
            {
                result.MatchedPositions.Add(key);
                if (!string.IsNullOrWhiteSpace(item.ExpectedCode))
                    result.MatchedByExpectedCode[item.ExpectedCode!] = result.MatchedByExpectedCode.GetValueOrDefault(item.ExpectedCode!) + 1;
            }
            else
            {
                result.Cells.Add(new DiffCell { Position = item.Position, ExpectedCode = item.ExpectedCode, ActualCode = actualCode, Kind = kind });
            }
        }

        foreach (var pair in actual)
        {
            if (expectedPositions.Contains(pair.Key)) continue;
            // The tracker supplies every cell in the blueprint volume. Empty cells
            // are not extras; only a real block where the blueprint expects air is.
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;
            result.ExtraCount++;
            result.Cells.Add(new DiffCell
            {
                Position = new Int3(pair.Key.X, pair.Key.Y, pair.Key.Z),
                ActualCode = pair.Value,
                Kind = DiffKind.Extra
            });
        }
        return result;
    }

    private static DiffKind Classify(string? expected, string? actual, MatchingMode mode,
        IDictionary<string, string> normalizedCodes)
    {
        if (string.IsNullOrWhiteSpace(expected)) return string.IsNullOrWhiteSpace(actual) ? DiffKind.Matched : DiffKind.Wrong;
        if (string.IsNullOrWhiteSpace(actual)) return DiffKind.Missing;
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return DiffKind.Matched;
        if (mode == MatchingMode.Strict) return DiffKind.Wrong;

        var expectedFamily = Normalize(expected, mode, normalizedCodes);
        var actualFamily = Normalize(actual, mode, normalizedCodes);
        if (mode == MatchingMode.Loose && (expectedFamily == actualFamily || expectedFamily.Contains(actualFamily) || actualFamily.Contains(expectedFamily))) return DiffKind.Matched;
        if (mode == MatchingMode.Medium && expectedFamily == actualFamily) return DiffKind.Matched;
        return DiffKind.Wrong;
    }

    private static string Normalize(string code, MatchingMode mode, IDictionary<string, string> cache)
    {
        if (cache.TryGetValue(code, out var normalized)) return normalized;
        normalized = mode == MatchingMode.Loose ? Family(code) : MaterialFamily(code);
        cache[code] = normalized;
        return normalized;
    }

    private static string Family(string code)
    {
        var path = code.Split(':').Last();
        var dash = path.IndexOf('-');
        return (dash > 0 ? path[..dash] : path).ToLowerInvariant();
    }

    private static string MaterialFamily(string code)
    {
        var path = code.Split(':').Last().ToLowerInvariant();
        foreach (var suffix in new[] { "-north", "-south", "-east", "-west", "-up", "-down", "-rotation0", "-rotation1", "-rotation2", "-rotation3" })
            path = path.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase);
        return path;
    }

    private static void Add(DiffResult result, DiffKind kind)
    {
        switch (kind)
        {
            case DiffKind.Matched: result.MatchedCount++; break;
            case DiffKind.Missing: result.MissingCount++; break;
            case DiffKind.Wrong: result.WrongCount++; break;
            case DiffKind.Unknown: result.UnknownCount++; break;
        }
    }
}
