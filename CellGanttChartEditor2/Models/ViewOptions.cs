namespace CellGanttChartEditor2.Models;

/// <summary>What the chart rows are grouped by. This also decides the colour coding.</summary>
public enum ChartGroupMode
{
    /// <summary>Rows are robots, bars are coloured by region.</summary>
    Region,

    /// <summary>Rows are regions, bars are coloured by robot.</summary>
    Robot,

    /// <summary>Every operation gets its own row, ordered by start time.</summary>
    Chronological,
}

/// <summary>Colour source. Only selectable in chronological mode; otherwise implied by the grouping.</summary>
public enum ChartColorBy
{
    Region,
    Robot,
}

public sealed record NamedOption<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

public static class ViewOptions
{
    public static IReadOnlyList<NamedOption<ChartGroupMode>> GroupModes { get; } = new[]
    {
        new NamedOption<ChartGroupMode>(ChartGroupMode.Region, "Region"),
        new NamedOption<ChartGroupMode>(ChartGroupMode.Robot, "Robot"),
        new NamedOption<ChartGroupMode>(ChartGroupMode.Chronological, "Chronological"),
    };

    public static IReadOnlyList<NamedOption<ChartColorBy>> ColorModes { get; } = new[]
    {
        new NamedOption<ChartColorBy>(ChartColorBy.Region, "Region"),
        new NamedOption<ChartColorBy>(ChartColorBy.Robot, "Robot"),
    };
}
