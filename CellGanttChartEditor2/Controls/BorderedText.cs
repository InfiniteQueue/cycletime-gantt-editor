using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Controls;

/// <summary>
/// A run of text drawn the way <see cref="ChartView"/> draws a bar's label, so a filter chip and the
/// bars it filters treat a checkered fill the same way. A TextBlock cannot stroke its glyphs, so the
/// chips carry one of these rather than a style.
///
/// Font family and size come from whatever the element is placed in; the weight matches the bars.
/// </summary>
public sealed class BorderedText : FrameworkElement
{
    public string Text { get; init; } = string.Empty;

    public Brush Foreground { get; init; } = Brushes.Black;

    /// <summary>Whether the surface behind it is a checkered pair, which is what earns the border.</summary>
    public bool Checkered { get; init; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Build();
        return new Size(text.Width, text.Height);
    }

    protected override void OnRender(DrawingContext dc) =>
        TextBorder.Draw(dc, Build(), new Point(0, 0), Foreground, Checkered);

    /// <summary>
    /// Built fresh each time rather than cached: a chip is thrown away and rebuilt whenever the
    /// filters change, so there is nothing here that outlives its text.
    /// </summary>
    private FormattedText Build() => new(
        Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold,
            FontStretches.Normal),
        TextElement.GetFontSize(this), Foreground, null,
        // Display, as ThemedWindow asks of every other control - a TextBlock would have taken it
        // from there, and a chip should not start measuring its text differently to its neighbours.
        TextFormattingMode.Display,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
