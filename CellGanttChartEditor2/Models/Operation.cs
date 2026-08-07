namespace CellGanttChartEditor2.Models;

/// <summary>
/// A single bar on the chart: one robot performing one operation inside one region.
/// </summary>
public sealed class Operation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string RobotName { get; set; } = string.Empty;

    public Guid RegionId { get; set; }

    /// <summary>
    /// Absolute start time. May legitimately exceed the cycle time - the chart wraps it for
    /// display, but the stored value is never rewritten when the cycle time changes.
    /// </summary>
    public double Start { get; set; }

    public double Duration { get; set; }

    public double End => Start + Duration;

    public override string ToString() => Name;
}
