using System.Windows;
using System.Windows.Media;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// Hands out visually distinct fills. Solid colours are drawn from nine hues spaced 40/360 apart -
/// the most that constraint allows. Once those run out, entries get a checkered pattern built from
/// two colours out of the same pool.
/// </summary>
public sealed class ColorAllocator
{
    /// <summary>Nine hues, every pair at least 40 degrees apart, ordered for contrast between neighbours.</summary>
    private static readonly double[] Hues = { 210, 10, 130, 50, 290, 170, 90, 250, 330 };

    private const double Saturation = 0.72;
    private const double Value = 0.86;

    private static readonly (int A, int B)[] CheckerPairs = BuildPairs();

    /// <summary>Checker tile size for fills, and a smaller one so the pattern still reads in a border.</summary>
    private const double FillTile = 14;
    private const double StrokeTile = 10;

    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Brush> _brushes = new();
    private readonly Dictionary<int, Brush> _strokeBrushes = new();

    public static int SolidCount => Hues.Length;

    private static (int, int)[] BuildPairs()
    {
        var pairs = new List<(int, int)>();
        // Widest hue separation first, so early checkered entries are the easiest to tell apart.
        for (var gap = Hues.Length - 1; gap >= 1; gap--)
        for (var i = 0; i + gap < Hues.Length; i++)
            pairs.Add((i, i + gap));
        return pairs.ToArray();
    }

    /// <summary>
    /// Assigns slots to <paramref name="keys"/> (in order) and releases slots for anything absent,
    /// so deleting a region frees its colour for reuse.
    /// </summary>
    public void Sync(IEnumerable<string> keys)
    {
        var ordered = keys.ToList();
        var alive = new HashSet<string>(ordered, StringComparer.Ordinal);

        foreach (var key in _slots.Keys.ToList())
            if (!alive.Contains(key))
                _slots.Remove(key);

        foreach (var key in ordered)
            Slot(key);
    }

    public int Slot(string key)
    {
        if (_slots.TryGetValue(key, out var slot))
            return slot;

        var used = new HashSet<int>(_slots.Values);
        slot = 0;
        while (used.Contains(slot))
            slot++;
        _slots[key] = slot;
        return slot;
    }

    public Brush GetBrush(string key) => BrushForSlot(Slot(key));

    /// <summary>
    /// The same colour as <see cref="GetBrush"/>, but a checkered entry uses a finer tile so the
    /// pattern is still legible when it is painted into a border rather than a filled bar.
    /// </summary>
    public Brush GetStrokeBrush(string key)
    {
        var slot = Slot(key);
        if (slot < Hues.Length)
            return BrushForSlot(slot);

        if (_strokeBrushes.TryGetValue(slot, out var cached))
            return cached;

        var pair = CheckerPairs[(slot - Hues.Length) % CheckerPairs.Length];
        var brush = Checker(FromHue(Hues[pair.A]), FromHue(Hues[pair.B]), StrokeTile);
        brush.Freeze();
        _strokeBrushes[slot] = brush;
        return brush;
    }

    /// <summary>Single representative colour - the checkered pattern's first colour when tiled.</summary>
    public Color GetPrimaryColor(string key)
    {
        var slot = Slot(key);
        return slot < Hues.Length
            ? FromHue(Hues[slot])
            : FromHue(Hues[CheckerPairs[(slot - Hues.Length) % CheckerPairs.Length].A]);
    }

    public Color GetSecondaryColor(string key)
    {
        var slot = Slot(key);
        return slot < Hues.Length
            ? FromHue(Hues[slot])
            : FromHue(Hues[CheckerPairs[(slot - Hues.Length) % CheckerPairs.Length].B]);
    }

    /// <summary>A darker version of the fill, for outlines.</summary>
    public Brush GetOutlineBrush(string key)
    {
        var brush = new SolidColorBrush(Darken(GetPrimaryColor(key), 0.55));
        brush.Freeze();
        return brush;
    }

    /// <summary>Black or white, whichever stays readable on the fill.</summary>
    public Brush GetTextBrush(string key)
    {
        var a = GetPrimaryColor(key);
        var b = GetSecondaryColor(key);
        var luminance = (Luminance(a) + Luminance(b)) / 2;
        return luminance > 0.55 ? Brushes.Black : Brushes.White;
    }

    public bool IsCheckered(string key) => Slot(key) >= Hues.Length;

    private Brush BrushForSlot(int slot)
    {
        if (_brushes.TryGetValue(slot, out var cached))
            return cached;

        Brush brush;
        if (slot < Hues.Length)
        {
            brush = new SolidColorBrush(FromHue(Hues[slot]));
        }
        else
        {
            var pair = CheckerPairs[(slot - Hues.Length) % CheckerPairs.Length];
            brush = Checker(FromHue(Hues[pair.A]), FromHue(Hues[pair.B]), FillTile);
        }

        brush.Freeze();
        _brushes[slot] = brush;
        return brush;
    }

    private static Brush Checker(Color a, Color b, double tile)
    {
        var half = tile / 2;

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(a), null,
            new RectangleGeometry(new Rect(0, 0, tile, tile))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(b), null,
            new RectangleGeometry(new Rect(half, 0, half, half))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(b), null,
            new RectangleGeometry(new Rect(0, half, half, half))));

        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, tile, tile),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
    }

    public static Color FromHue(double hue) => FromHsv(hue, Saturation, Value);

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    public static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor),
        (byte)(color.G * factor),
        (byte)(color.B * factor));

    private static double Luminance(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
