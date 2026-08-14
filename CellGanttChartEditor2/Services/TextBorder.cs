using System.Windows;
using System.Windows.Media;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// The thin border drawn around text that sits on a checkered fill, in whichever of black and white
/// the text itself is not.
///
/// A flat fill needs none: <see cref="ColorAllocator.TextOn(Color)"/> already picked the colour that
/// reads on it, and a border would only thicken the letters. A checkered pair is the case that rule
/// cannot fully answer - one colour is chosen for the two of them, so a letter landing on the wrong
/// tile has less to work with - and the border is what carries it across.
///
/// Everything drawing a label on an allocated fill comes through here, so the bars and the filter
/// chips cannot disagree about when a label is bordered.
/// </summary>
public static class TextBorder
{
    /// <summary>
    /// How far the border extends beyond the glyphs. Zero draws every label plain.
    /// </summary>
    public const double Thickness = 0.7;

    /// <summary>
    /// Stroked along the glyph outlines and so centred on them, with the fill drawn over the top
    /// hiding the inner half - hence the doubling, which leaves <see cref="Thickness"/> showing
    /// outside the letters.
    /// </summary>
    private static readonly Pen BlackPen = MakePen(Brushes.Black);
    private static readonly Pen WhitePen = MakePen(Brushes.White);

    /// <summary>
    /// Draws <paramref name="text"/> at <paramref name="origin"/> in <paramref name="brush"/>, over
    /// a border when the surface under it is checkered.
    /// </summary>
    public static void Draw(DrawingContext dc, FormattedText text, Point origin, Brush brush,
        bool checkered)
    {
        if (checkered && Thickness > 0)
            dc.DrawGeometry(null, PenFor(brush), text.BuildGeometry(origin));
        dc.DrawText(text, origin);
    }

    /// <summary>The border for a label, in whichever of black and white the text itself is not.</summary>
    private static Pen PenFor(Brush text) =>
        text is SolidColorBrush solid && solid.Color.R < 128 ? WhitePen : BlackPen;

    private static Pen MakePen(Brush brush)
    {
        var pen = new Pen(brush, Thickness * 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }
}
