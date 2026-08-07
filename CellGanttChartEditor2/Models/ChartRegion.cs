using System.Windows;

namespace CellGanttChartEditor2.Models;

/// <summary>
/// A rectangular area of the reference image. Operations are defined against a region,
/// and the region's allocated colour is used to colour code the chart.
/// </summary>
public sealed class ChartRegion
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Bounds in image pixel coordinates, so swapping the image leaves regions in place.</summary>
    public Rect Bounds { get; set; }

    public RegionCategory Category { get; set; } = RegionCategory.Uncategorised;

    /// <summary>Key used by the colour allocator.</summary>
    public string ColorKey => Id.ToString("N");

    public override string ToString() => Name;
}
