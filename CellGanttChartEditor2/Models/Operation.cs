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
    /// What kind of work this is. Every operation has one - <see cref="OperationCategory.Uncategorised"/>
    /// until the user says otherwise - so unlike a region or a robot there is no catch-all case and
    /// nothing to test for having none.
    /// </summary>
    public OperationCategory Category { get; set; } = OperationCategory.Uncategorised;

    public string CategoryColorKey => OperationCategoryInfo.ColorKey(Category);

    /// <summary>
    /// Absolute start time. May legitimately exceed the cycle time - the chart wraps it for
    /// display, but the stored value is never rewritten when the cycle time changes.
    /// </summary>
    public double Start { get; set; }

    public double Duration { get; set; }

    public double End => Start + Duration;

    /// <summary>
    /// Operations this one is deliberately carried out alongside, and therefore never conflicts with
    /// however much they overlap. Two robots handing a part between them share a region on purpose;
    /// saying so here is how the user tells the chart that the overlap is the design, not a mistake.
    ///
    /// The relation is symmetric and <see cref="GanttDocument"/> keeps it that way - both operations
    /// name each other - so nothing has to guess which side of a pair to ask.
    /// </summary>
    public List<Guid> SimultaneousWith { get; } = new();

    public string? Notes { get; set; }

    public bool IsSimultaneousWith(Operation other) => SimultaneousWith.Contains(other.Id);

    public override string ToString() => Name;
}
