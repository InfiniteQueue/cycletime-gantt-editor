using System.Windows.Media;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// The dark colour scheme, carried over from the previous editor so the two look the same.
/// The XAML side of it lives in Themes/Dark.xaml; keep the two in step.
/// </summary>
public static class Palette
{
    // Surfaces
    public static readonly Color ChartSurface = Rgb(0x15, 0x17, 0x1C);
    public static readonly Color RowA = Rgb(0x1A, 0x1D, 0x23);
    public static readonly Color RowB = Rgb(0x1F, 0x22, 0x29);
    public static readonly Color ImageSurface = Rgb(0x20, 0x23, 0x2A);
    public static readonly Color Panel = Rgb(0x23, 0x26, 0x2E);
    public static readonly Color Window = Rgb(0x1A, 0x1D, 0x23);

    // Lines
    public static readonly Color Grid = Rgb(0x2C, 0x30, 0x39);
    public static readonly Color CycleEdge = Rgb(0x55, 0x5C, 0x6B);
    public static readonly Color BarOutline = Rgb(0x10, 0x12, 0x16);
    public static readonly Color Divider = Rgb(0x37, 0x3C, 0x46);

    // Text
    public static readonly Color Text = Rgb(0xD6, 0xDA, 0xE3);
    public static readonly Color Muted = Rgb(0x8A, 0x8F, 0x98);
    public static readonly Color Link = Rgb(0xE8, 0xEC, 0xF4);
    public static readonly Color Accent = Rgb(0x1F, 0x6F, 0xEB);

    /// <summary>Backing plate for the region title drawn over the image.</summary>
    public static readonly Color LabelPlate = Color.FromArgb(200, 0x10, 0x12, 0x16);

    // Row groups. The collapsed block is deliberately colourless: it stands for several regions or
    // robots at once, so borrowing any one of their hues would misread.
    public static readonly Color GroupBand = Rgb(0x26, 0x2A, 0x33);
    public static readonly Color GroupEdge = Rgb(0x4A, 0x51, 0x60);
    public static readonly Color GroupBlock = Rgb(0x8A, 0x92, 0xA3);

    //Todo: make this whiter
    /// <summary>
    /// A robot's crosshair on the image while nothing is colouring robots. Deliberately the same
    /// neutral as the collapsed group block: it marks a thing the chart is not currently keying on.
    /// </summary>
    public static readonly Color Crosshair = Rgb(0xC2, 0xC8, 0xD4);

    /// <summary>Where a dragged row would land.</summary>
    public static readonly Color DropIndicator = Rgb(0x3D, 0xDC, 0x84);

    /// <summary>Opacity for bars that are only on screen because they link to something in the filter.</summary>
    public const double DimmedOpacity = 0.32;

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static Pen Pen(Color color, double thickness)
    {
        var pen = new Pen(Brush(color), thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Concentric fading strokes that stand in for the white DropShadowEffect the old editor put on
    /// selected bars and highlighted regions. Effects attach to elements; this surface is drawn by
    /// hand, so the glow is painted instead.
    /// </summary>
    public static Pen[] GlowPens(double innerThickness) => new[]
    {
        //Pen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), innerThickness + 10),
        Pen(Color.FromArgb(0x46, 0xFF, 0xFF, 0xFF), innerThickness + 6),
        Pen(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF), innerThickness + 3),
    };
}
