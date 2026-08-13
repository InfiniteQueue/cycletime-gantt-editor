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

    /// <summary>
    /// What kind of work each bar is. Only reachable in the chronological view - the other two put a
    /// region or a robot on every row, which then decides the colouring.
    /// </summary>
    Category,
}

public sealed record NamedOption<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

public static class ViewOptions
{
    public static IReadOnlyList<NamedOption<ChartGroupMode>> GroupModes { get; } = new[]
    {
        new NamedOption<ChartGroupMode>(ChartGroupMode.Region, "Robot"), //Todo: fix this stupid hacky reversed display name situation
        new NamedOption<ChartGroupMode>(ChartGroupMode.Robot, "Region"),
        new NamedOption<ChartGroupMode>(ChartGroupMode.Chronological, "Chronological"),
    };

    public static IReadOnlyList<NamedOption<ChartColorBy>> ColorModes { get; } = new[]
    {
        new NamedOption<ChartColorBy>(ChartColorBy.Region, "Region"),
        new NamedOption<ChartColorBy>(ChartColorBy.Robot, "Robot"),
        new NamedOption<ChartColorBy>(ChartColorBy.Category, "Category"),
    };
}
