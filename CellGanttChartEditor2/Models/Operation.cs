namespace CellGanttChartEditor2.Models;

/// <summary>
/// A single bar on the chart: one robot performing one operation, usually inside one region.
/// </summary>
public sealed class Operation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The robot carrying the work out, or empty for none. Not every operation has one - a wait or
    /// a machine's own cycle is work the chart has to show without a robot to hang it on.
    /// </summary>
    public string RobotName { get; set; } = string.Empty;

    public bool HasRobot => !string.IsNullOrWhiteSpace(RobotName);

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
