namespace CellGanttChartEditor2.Models;

/// <summary>
/// Ties the start of <see cref="TargetId"/> to the end of <see cref="SourceId"/> with a fixed
/// separation: target.Start == source.End + Lag.
/// The source is the bar that was dropped onto; the target is the bar that was dragged.
/// </summary>
public sealed class OperationLink
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SourceId { get; set; }

    public Guid TargetId { get; set; }

    /// <summary>Time between the end of the source and the start of the target.</summary>
    public double Lag { get; set; }
}
