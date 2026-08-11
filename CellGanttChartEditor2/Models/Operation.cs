namespace CellGanttChartEditor2.Models;

/// <summary>
/// A single bar on the chart: one robot performing one operation, usually inside one region.
/// </summary>
public sealed class Operation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Every operation is carried out by a robot; that much is required.</summary>
    public string RobotName { get; set; } = string.Empty;

    /// <summary>
    /// The region the work happens in, or <see cref="Guid.Empty"/> for none. Not every operation
    /// belongs to a place on the image.
    /// </summary>
    public Guid RegionId { get; set; }

    public bool HasRegion => RegionId != Guid.Empty;

    /// <summary>
    /// Absolute start time. May legitimately exceed the cycle time - the chart wraps it for
    /// display, but the stored value is never rewritten when the cycle time changes.
    /// </summary>
    public double Start { get; set; }

    public double Duration { get; set; }

    public double End => Start + Duration;

    public override string ToString() => Name;
}
