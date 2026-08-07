namespace CellGanttChartEditor2.Services;

/// <summary>
/// Hardcoded suggestions offered by the editable comboboxes.
/// PLACEHOLDER VALUES - replace these lists with the real preset names.
/// </summary>
public static class Presets
{
    public static IReadOnlyList<string> OperationNames { get; } = new[]
    {
        "Placeholder operation 1",
        "Placeholder operation 2",
        "Placeholder operation 3",
        "Placeholder operation 4",
        "Placeholder operation 5",
    };

    public static IReadOnlyList<string> RobotNames { get; } = new[]
    {
        "Placeholder robot 1",
        "Placeholder robot 2",
        "Placeholder robot 3",
        "Placeholder robot 4",
    };

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
