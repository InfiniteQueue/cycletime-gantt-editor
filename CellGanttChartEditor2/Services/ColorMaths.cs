using System.Windows.Media;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// Colour as measured rather than colour as chosen: the conversions between CIELCh, CIELAB and sRGB,
/// where the edge of the sRGB gamut lies, and how far apart two colours look.
///
/// Nothing here knows what the fills are for. It answers questions with one right answer - what
/// colour is this, how bright is it, how different are these two - and leaves every question of
/// taste or policy to <see cref="ColorAllocator"/>: how colourful a fill should be, how far it must
/// stay from half luminance, how many there are. Keep that line. A constant that could reasonably be
/// argued about belongs on the other side of it.
/// </summary>
public static class ColorMaths
{
    /// <summary>D65, the white point sRGB is defined against.</summary>
    private const double WhiteX = 0.95047;
    private const double WhiteY = 1.0;
    private const double WhiteZ = 1.08883;

    /// <summary>The bend in the CIELAB transfer function, where it goes linear near black.</summary>
    private const double LabKnee = 6.0 / 29;

    /// <summary>How far a bisection is taken. Well past what whole bytes of output can show.</summary>
    public const int Steps = 18;

    /// <summary>Higher than any chroma sRGB holds, as a starting bracket for the search.</summary>
    private const double ChromaCeiling = 200;

    // ------------------------------------------------------------- CIELCh to sRGB

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

    // ------------------------------------------------------------- the edge of sRGB

    /// <summary>The most chroma a hue can carry at a lightness and still be a colour sRGB can show.</summary>
    public static double MaxChroma(double hue, double lightness)
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

    /// <summary>
    /// The most colourful sRGB gets for a hue, and the lightness it happens at - the corner of the
    /// gamut for that hue. A slice of the RGB cube at one hue rises to a single corner and falls
    /// away either side, so the peak is closed in on rather than scanned for: a golden-section
    /// search keeps whichever two thirds of the range still hold it, and reuses one of its two
    /// probes each time round. Worth doing well - the palette asks for a cusp per candidate hue,
    /// not per fill.
    /// </summary>
    public static (double Lightness, double Chroma) Cusp(double hue)
    {
        if (Cusps.TryGetValue(hue, out var cached))
            return cached;

        // 1/phi. The point of the ratio is that the probe kept from one round is correctly placed
        // for the next, so each step past the first costs one call to MaxChroma rather than two.
        const double Inverse = 0.6180339887498949;

        var low = 0.0;
        var high = 100.0;
        var left = high - Inverse * (high - low);
        var right = low + Inverse * (high - low);
        var atLeft = MaxChroma(hue, left);
        var atRight = MaxChroma(hue, right);

        // Down to a tenth of a unit of lightness, finer than a practical scan would resolve.
        while (high - low > 0.1)
        {
            if (atLeft >= atRight)
            {
                high = right;
                right = left;
                atRight = atLeft;
                left = high - Inverse * (high - low);
                atLeft = MaxChroma(hue, left);
            }
            else
            {
                low = left;
                left = right;
                atLeft = atRight;
                right = low + Inverse * (high - low);
                atRight = MaxChroma(hue, right);
            }
        }

        var best = atLeft >= atRight ? (left, atLeft) : (right, atRight);
        Cusps[hue] = best;
        return best;
    }

    private static readonly Dictionary<double, (double Lightness, double Chroma)> Cusps = new();

    private static bool InGamut(double lightness, double chroma, double hue)
    {
        var (r, g, b) = LinearFromLch(lightness, chroma, hue);
        return r is >= 0 and <= 1 && g is >= 0 and <= 1 && b is >= 0 and <= 1;
    }

    // ------------------------------------------------------------- sRGB back to Lab

    /// <summary>
    /// The reverse of the trip above: a colour as sRGB holds it, back to the Lab coordinates
    /// <see cref="Difference"/> is defined on.
    /// </summary>
    public static (double L, double A, double B) Lab(Color color)
    {
        var r = Decode(color.R);
        var g = Decode(color.G);
        var b = Decode(color.B);

        var x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / WhiteX;
        var y = (0.2126729 * r + 0.7151522 * g + 0.0721750 * b) / WhiteY;
        var z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / WhiteZ;

        var fx = ToLabTransfer(x);
        var fy = ToLabTransfer(y);
        var fz = ToLabTransfer(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double Decode(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double ToLabTransfer(double t) =>
        t > LabKnee * LabKnee * LabKnee ? Math.Cbrt(t) : t / (3 * LabKnee * LabKnee) + 4.0 / 29;

    // ------------------------------------------------------------- how different two colours look

    /// <summary>
    /// CIEDE2000 between two Lab colours - how different they look, as the standard has it. The
    /// corrections it carries over a plain distance are exactly the ones that matter for choosing a
    /// palette: it pulls back differences between strongly coloured pairs, which a plain distance
    /// flatters, and it treats a hue shift in the blues more carefully than one elsewhere.
    /// </summary>
    public static double Difference((double L, double A, double B) p, (double L, double A, double B) q)
    {
        const double deg = Math.PI / 180;
        var (l1, a1, b1) = p;
        var (l2, a2, b2) = q;

        var chroma1 = Math.Sqrt(a1 * a1 + b1 * b1);
        var chroma2 = Math.Sqrt(a2 * a2 + b2 * b2);
        var meanChroma = (chroma1 + chroma2) / 2;
        var g = 0.5 * (1 - Math.Sqrt(Pow7(meanChroma) / (Pow7(meanChroma) + Pow7(25))));

        var ap1 = (1 + g) * a1;
        var ap2 = (1 + g) * a2;
        var cp1 = Math.Sqrt(ap1 * ap1 + b1 * b1);
        var cp2 = Math.Sqrt(ap2 * ap2 + b2 * b2);
        var hp1 = cp1 == 0 ? 0 : (Math.Atan2(b1, ap1) / deg + 360) % 360;
        var hp2 = cp2 == 0 ? 0 : (Math.Atan2(b2, ap2) / deg + 360) % 360;

        var dL = l2 - l1;
        var dC = cp2 - cp1;

        var dh = 0.0;
        if (cp1 * cp2 != 0)
        {
            dh = hp2 - hp1;
            if (dh > 180) dh -= 360;
            else if (dh < -180) dh += 360;
        }
        var dH = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(dh / 2 * deg);

        var meanL = (l1 + l2) / 2;
        var meanCp = (cp1 + cp2) / 2;
        double meanH;
        if (cp1 * cp2 == 0) meanH = hp1 + hp2;
        else if (Math.Abs(hp1 - hp2) <= 180) meanH = (hp1 + hp2) / 2;
        else meanH = (hp1 + hp2 + (hp1 + hp2 < 360 ? 360 : -360)) / 2;

        var t = 1 - 0.17 * Math.Cos((meanH - 30) * deg)
                  + 0.24 * Math.Cos(2 * meanH * deg)
                  + 0.32 * Math.Cos((3 * meanH + 6) * deg)
                  - 0.20 * Math.Cos((4 * meanH - 63) * deg);

        var offset = (meanL - 50) * (meanL - 50);
        var sl = 1 + 0.015 * offset / Math.Sqrt(20 + offset);
        var sc = 1 + 0.045 * meanCp;
        var sh = 1 + 0.015 * meanCp * t;

        // The rotation term, which is what stops two blues being credited with a difference the eye
        // does not see - it only bites around hue 275.
        var fromBlue = (meanH - 275) / 25;
        var rotation = -Math.Sin(2 * 30 * Math.Exp(-fromBlue * fromBlue) * deg)
                       * 2 * Math.Sqrt(Pow7(meanCp) / (Pow7(meanCp) + Pow7(25)));

        var lightness = dL / sl;
        var chroma = dC / sc;
        var hue = dH / sh;
        return Math.Sqrt(lightness * lightness + chroma * chroma + hue * hue
                         + rotation * chroma * hue);
    }

    private static double Pow7(double value)
    {
        var square = value * value;
        return square * square * square * value;
    }

    // ------------------------------------------------------------- odds and ends

    /// <summary>
    /// How bright a colour reads, on the weighting that has served television since Rec. 601. It is
    /// taken off the encoded bytes rather than off linear light, which is not what a physicist would
    /// do, but it tracks the impression of brightness well enough for deciding what text goes on top.
    /// </summary>
    public static double Luminance(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    public static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor),
        (byte)(color.G * factor),
        (byte)(color.B * factor));
}
