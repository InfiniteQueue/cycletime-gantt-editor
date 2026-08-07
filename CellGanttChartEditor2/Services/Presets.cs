namespace CellGanttChartEditor2.Services;

/// <summary>
/// Hardcoded suggestions offered by the editable comboboxes.
/// PLACEHOLDER VALUES - replace these lists with the real preset names.
/// </summary>
public static class Presets
{

    /// <summary>Presets first, then anything already used in the document that is not a preset.</summary>
    public static List<string> Combine(IReadOnlyList<string> presets, IEnumerable<string> existing)
    {
        var result = new List<string>(presets);
        var seen = new HashSet<string>(presets, StringComparer.OrdinalIgnoreCase);
        foreach (var value in existing)
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                result.Add(value);
        return result;
    }
}
