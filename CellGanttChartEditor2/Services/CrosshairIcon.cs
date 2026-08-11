using System.Windows;
using System.Windows.Media;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// The crosshair: a ring with four spokes, and the app's one symbol for "point at something on the
/// image". It marks a robot's place on the image, and it is the face of every button that asks the
/// user to pick a point or draw an area there.
///
/// Every copy of the shape comes from here. If it needs to change, it changes once.
/// </summary>
public static class CrosshairIcon
{
    /// <summary>Ring radius as a fraction of the mark's reach, and where the spokes start.</summary>
    private const double RingFraction = 0.45;
    private const double SpokeGapFraction = 0.61;

    /// <summary>
    /// The shape at the size a button wants it, centred in a 14 by 14 box. Stroked rather than
    /// filled, so the caller sets the colour and weight to suit whatever it sits on.
    /// </summary>
    public static Geometry ButtonGeometry { get; } = Build(new Point(7, 7), 6.5);

    /// <summary>The shape centred anywhere, at any reach. Frozen, so it is safe to hand around.</summary>
    public static Geometry Geometry(Point centre, double reach) => Build(centre, reach);

    /// <summary>
    /// Strokes the mark straight onto a drawing context, which is how the image view draws it.
    /// Taking a pen rather than a brush lets the caller lay a keyline down first.
    /// </summary>
    public static void Draw(DrawingContext dc, Point centre, double reach, Pen pen)
    {
        var ring = reach * RingFraction;
        var gap = reach * SpokeGapFraction;

        dc.DrawEllipse(null, pen, centre, ring, ring);
        dc.DrawLine(pen, new Point(centre.X - reach, centre.Y), new Point(centre.X - gap, centre.Y));
        dc.DrawLine(pen, new Point(centre.X + gap, centre.Y), new Point(centre.X + reach, centre.Y));
        dc.DrawLine(pen, new Point(centre.X, centre.Y - reach), new Point(centre.X, centre.Y - gap));
        dc.DrawLine(pen, new Point(centre.X, centre.Y + gap), new Point(centre.X, centre.Y + reach));
    }

    private static Geometry Build(Point centre, double reach)
    {
        var ring = reach * RingFraction;
        var gap = reach * SpokeGapFraction;

        var group = new GeometryGroup();
        group.Children.Add(new EllipseGeometry(centre, ring, ring));
        Spoke(group, centre, new Vector(-1, 0), gap, reach);
        Spoke(group, centre, new Vector(1, 0), gap, reach);
        Spoke(group, centre, new Vector(0, -1), gap, reach);
        Spoke(group, centre, new Vector(0, 1), gap, reach);
        group.Freeze();
        return group;
    }

    private static void Spoke(GeometryGroup group, Point centre, Vector direction, double from, double to) =>
        group.Children.Add(new LineGeometry(centre + direction * from, centre + direction * to));
}
