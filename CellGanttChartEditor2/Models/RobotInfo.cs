using System.Windows;

namespace CellGanttChartEditor2.Models;

/// <summary>
/// What the document knows about a robot beyond its name. Robots themselves stay implicit - one
/// exists for as long as an operation names it - so this is only ever extra detail hung off a name,
/// and it goes when the last operation using that name does.
/// </summary>
public sealed class RobotInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Where the robot sits on the reference image, in image pixels like a region's bounds, so
    /// swapping the image leaves it in place. Null until the user places it.
    /// </summary>
    public Point? Location { get; set; }

    public override string ToString() => Name;
}
