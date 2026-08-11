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

    /// <summary>
    /// Bounds in image pixel coordinates, so swapping the image leaves regions in place. Empty for a
    /// region that has not been given an area yet - it is a perfectly good region, it simply is not
    /// drawn on the image.
    /// </summary>
    public Rect Bounds { get; set; }

    /// <summary>
    /// False for a region that is not drawn on the image. Note that WPF's <see cref="Rect.Empty"/>
    /// carries a negative width, so anything that measures these bounds has to ask this first.
    /// </summary>
    public bool HasArea => Bounds.Width > 0 && Bounds.Height > 0;

    public RegionCategory Category { get; set; } = RegionCategory.Uncategorised;

    /// <summary>Key used by the colour allocator.</summary>
    public string ColorKey => Id.ToString("N");

    public override string ToString() => Name;
}
