using System.Windows;
using System.Windows.Media;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// Hands out visually distinct fills. Solid colours are nine hues spaced evenly around the CIELCh
/// hue circle; once those run out, entries get a checkered pattern built from two colours out of the
/// same pool.
///
/// The hues are picked in CIELCh rather than HSV because equal steps there are meant to be equal
/// steps in perceived hue, which equal steps in HSV are not - HSV's hue is a corner-to-corner walk
/// round the RGB cube, so 40 degrees of it covers a wide perceptual jump in one place and barely a
/// shift in another. Lightness and chroma are chosen per hue, since neither the luminance rule below
/// nor the sRGB gamut leaves them free.
/// </summary>
public sealed class ColorAllocator
{
    private const int HueCount = 9;

    /// <summary>
    /// Where the ring starts and how far round each step goes. Four steps at a time is what spaces
    /// consecutive slots: nine and four share no factor, so it still reaches every hue, but two
    /// items allocated one after the other land most of the circle apart rather than side by side.
    /// </summary>
    private const double HueStart = 25;
    private const int HueStride = 4;

    /// <summary>
    /// How much of a hue's strongest colour to actually use. Every hue is taken at the lightness
    /// where sRGB lets it be most colourful, which is a different lightness for each of them - a
    /// yellow is at its best pale and a blue dark, and holding them all at one lightness would give
    /// nine muddy colours of much the same brightness. Backing off the very edge of the gamut
    /// leaves them looking like paint rather than signal lights.
    /// </summary>
    private const double ChromaFraction = 0.78;

    private static readonly double[] Hues = BuildHues();

    private static double[] BuildHues()
    {
        var hues = new double[HueCount];
        for (var i = 0; i < HueCount; i++)
            hues[i] = (HueStart + i * HueStride % HueCount * (360.0 / HueCount)) % 360;
        return hues;
    }

    /// <summary>
    /// How far a fill's luminance is kept clear of the midpoint. Black and white text are both
    /// marginal on a colour sitting at half luminance, so no fill is allowed to sit there: each hue
    /// is taken to whichever side it was already nearer. It is what makes
    /// <see cref="GetTextBrush"/> a decision rather than a guess.
    /// </summary>
    private const double MidLuminance = 0.5;
    private const double LuminanceMargin = 0.1;

    /// <summary>
    /// Aimed at a shade past the margin, because the answer has to come back as whole bytes:
    /// rounding three channels can move a luminance by up to half a byte's worth, which without
    /// this would be enough to land a colour just inside the band it was moved out of.
    /// </summary>
    private const double RoundingAllowance = 0.5 / 255;

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

        var (primary, secondary) = ColorsForSlot(slot);
        var brush = Checker(primary, secondary, StrokeTile);
        brush.Freeze();
        _strokeBrushes[slot] = brush;
        return brush;
    }

    /// <summary>Single representative colour - the checkered pattern's first colour when tiled.</summary>
    public Color GetPrimaryColor(string key) => ColorsForSlot(Slot(key)).Primary;

    public Color GetSecondaryColor(string key) => ColorsForSlot(Slot(key)).Secondary;

    /// <summary>
    /// The one or two colours a slot is drawn in. A checkered slot's second hue is taken to the
    /// first one's luminance, so the pattern reads as one brightness in two hues rather than a
    /// bright tile beside a dark one - and text laid over it is the same choice on either tile.
    /// </summary>
    private static (Color Primary, Color Secondary) ColorsForSlot(int slot)
    {
        // Working a colour out means two nested bisections, and the chart asks for these while it
        // draws, so a slot is worked out once. They depend on nothing but the slot.
        if (SlotColors.TryGetValue(slot, out var cached))
            return cached;

        (Color, Color) colors;
        if (slot < Hues.Length)
        {
            var solid = FromHue(Hues[slot]);
            colors = (solid, solid);
        }
        else
        {
            var pair = CheckerPairs[(slot - Hues.Length) % CheckerPairs.Length];
            var primary = FromHue(Hues[pair.A]);
            colors = (primary, AtLuminance(Hues[pair.B], Luminance(primary)));
        }

        SlotColors[slot] = colors;
        return colors;
    }

    private static readonly Dictionary<int, (Color Primary, Color Secondary)> SlotColors = new();

    /// <summary>A darker version of the fill, for outlines.</summary>
    public Brush GetOutlineBrush(string key)
    {
        var brush = new SolidColorBrush(Darken(GetPrimaryColor(key), 0.55));
        brush.Freeze();
        return brush;
    }

    /// <summary>Black or white, whichever stays readable on the fill.</summary>
    public Brush GetTextBrush(string key) => TextOn(GetPrimaryColor(key), GetSecondaryColor(key));

    /// <summary>
    /// The same choice for a colour that was not allocated here - the neutral fill a bar with
    /// nothing to colour by takes, or the white a conflict paints over one. Anything drawing text
    /// on a coloured surface should come through here, so one rule decides all of it.
    /// </summary>
    public static Brush TextOn(Color color) => TextOn(color, color);

    public static Brush TextOn(Color a, Color b) =>
        (Luminance(a) + Luminance(b)) / 2 > 0.55 ? Brushes.Black : Brushes.White;

    public bool IsCheckered(string key) => Slot(key) >= Hues.Length;

    private Brush BrushForSlot(int slot)
    {
        if (_brushes.TryGetValue(slot, out var cached))
            return cached;

        var (primary, secondary) = ColorsForSlot(slot);
        var brush = slot < Hues.Length
            ? new SolidColorBrush(primary)
            : Checker(primary, secondary, FillTile);

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

    /// <summary>
    /// A CIELCh hue as this allocator hands it out: the colour it comes out as at the standard
    /// lightness, moved off the midpoint if it happened to land there.
    /// </summary>
    public static Color FromHue(double hue) => AtLuminance(hue, Luminance(AtLightness(hue, Cusp(hue).Lightness)));

    /// <summary>Pushes a luminance clear of the band either side of the midpoint, the shorter way.</summary>
    private static double Clear(double luminance)
    {
        var margin = LuminanceMargin + RoundingAllowance;
        if (Math.Abs(luminance - MidLuminance) >= margin)
            return luminance;
        return luminance < MidLuminance ? MidLuminance - margin : MidLuminance + margin;
    }

    /// <summary>
    /// A hue at a chosen luminance. Lightness is closed in on rather than solved: in CIELCh the
    /// luminance this class measures by is not a straight line in any one of the three coordinates,
    /// because the chroma has to be pulled in to stay inside sRGB as the lightness moves. It is
    /// still strictly rising with lightness - black at nothing, white at everything - so a bisection
    /// gets there in a fixed handful of steps, and the answer is cached per slot.
    /// </summary>
    private static Color AtLuminance(double hue, double wanted)
    {
        // Every colour this class hands out comes through here, so this is where the band about the
        // midpoint is kept clear - of a hue's own luminance, and of one matched to another's.
        var target = Clear(wanted);

        var low = 0.0;
        var high = 100.0;
        for (var i = 0; i < Steps; i++)
        {
            var mid = (low + high) / 2;
            if (Luminance(AtLightness(hue, mid)) < target)
                low = mid;
            else
                high = mid;
        }

        return AtLightness(hue, (low + high) / 2);
    }

    /// <summary>How far a bisection is taken. Well past what whole bytes of output can show.</summary>
    private const int Steps = 18;

    /// <summary>
    /// The hue at a given lightness: as colourful as this hue is ever asked to be, or as colourful
    /// as sRGB can manage at that lightness if that is less.
    /// </summary>
    private static Color AtLightness(double hue, double lightness) =>
        FromLch(lightness, Math.Min(Cusp(hue).Chroma * ChromaFraction, MaxChroma(hue, lightness)), hue);

    /// <summary>The most chroma a hue can carry at a lightness and still be a colour sRGB can show.</summary>
    private static double MaxChroma(double hue, double lightness)
    {
        var low = 0.0;
        var high = ChromaCeiling;
        for (var i = 0; i < Steps; i++)
        {
            var mid = (low + high) / 2;
            if (InGamut(lightness, mid, hue))
                low = mid;
            else
                high = mid;
        }
        return low;
    }

    /// <summary>Higher than any chroma sRGB holds, as a starting bracket for the search.</summary>
    private const double ChromaCeiling = 200;

    /// <summary>
    /// The most colourful sRGB gets for a hue, and the lightness it happens at - the corner of the
    /// gamut for that hue. Coarse steps first, since the shape has one peak and no local traps, then
    /// a finer pass around it. Worth caching: nine hues, worked out once.
    /// </summary>
    private static (double Lightness, double Chroma) Cusp(double hue)
    {
        if (Cusps.TryGetValue(hue, out var cached))
            return cached;

        var best = (Lightness: 0.0, Chroma: 0.0);
        for (var lightness = 1.0; lightness < 100; lightness += 1)
        {
            var chroma = MaxChroma(hue, lightness);
            if (chroma > best.Chroma)
                best = (lightness, chroma);
        }

        for (var lightness = best.Lightness - 1; lightness <= best.Lightness + 1; lightness += 0.1)
        {
            var chroma = MaxChroma(hue, lightness);
            if (chroma > best.Chroma)
                best = (lightness, chroma);
        }

        Cusps[hue] = best;
        return best;
    }

    private static readonly Dictionary<double, (double Lightness, double Chroma)> Cusps = new();

    private static bool InGamut(double lightness, double chroma, double hue)
    {
        var (r, g, b) = LinearFromLch(lightness, chroma, hue);
        return r is >= 0 and <= 1 && g is >= 0 and <= 1 && b is >= 0 and <= 1;
    }

    // ------------------------------------------------------- CIELCh to sRGB

    /// <summary>D65, the white point sRGB is defined against.</summary>
    private const double WhiteX = 0.95047;
    private const double WhiteY = 1.0;
    private const double WhiteZ = 1.08883;

    /// <summary>The bend in the CIELAB transfer function, where it goes linear near black.</summary>
    private const double LabKnee = 6.0 / 29;

    public static Color FromLch(double lightness, double chroma, double hue)
    {
        var (r, g, b) = LinearFromLch(lightness, chroma, hue);
        return Color.FromRgb(Encode(r), Encode(g), Encode(b));
    }

    /// <summary>
    /// LCh through Lab and XYZ to linear sRGB, left unclamped so the caller can see whether the
    /// colour asked for is one sRGB actually has.
    /// </summary>
    private static (double R, double G, double B) LinearFromLch(double lightness, double chroma, double hue)
    {
        var radians = hue * Math.PI / 180;
        var a = chroma * Math.Cos(radians);
        var b = chroma * Math.Sin(radians);

        var fy = (lightness + 16) / 116;
        var fx = fy + a / 500;
        var fz = fy - b / 200;

        var x = WhiteX * FromLabTransfer(fx);
        var y = WhiteY * FromLabTransfer(fy);
        var z = WhiteZ * FromLabTransfer(fz);

        return (
            3.2404542 * x - 1.5371385 * y - 0.4985314 * z,
            -0.9692660 * x + 1.8760108 * y + 0.0415560 * z,
            0.0556434 * x - 0.2040259 * y + 1.0572252 * z);
    }

    private static double FromLabTransfer(double t) =>
        t > LabKnee ? t * t * t : 3 * LabKnee * LabKnee * (t - 4.0 / 29);

    /// <summary>Linear light to a byte, through the sRGB transfer curve.</summary>
    private static byte Encode(double linear)
    {
        var encoded = linear <= 0.0031308
            ? 12.92 * linear
            : 1.055 * Math.Pow(Math.Max(0, linear), 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(encoded, 0, 1) * 255);
    }

    public static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor),
        (byte)(color.G * factor),
        (byte)(color.B * factor));

    private static double Luminance(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
