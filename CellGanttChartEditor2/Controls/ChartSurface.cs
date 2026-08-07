using System.Windows;
using System.Windows.Media;

namespace CellGanttChartEditor2.Controls;

/// <summary>
/// Bare drawing surface for <see cref="ChartView"/>. All the drawing and hit testing lives on the
/// owner; this exists so the view can host WPF children (the inline row-header editor, scroll bars)
/// on top of a fully custom-rendered chart.
/// </summary>
public sealed class ChartSurface : FrameworkElement
{
    public ChartSurface()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    internal ChartView? Owner { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        Owner?.RenderChart(dc, new Size(ActualWidth, ActualHeight));
    }
}
