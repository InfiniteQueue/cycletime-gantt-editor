namespace CellGanttChartEditor2.Services;

/// <summary>
/// Helpers for the looping time axis. A bar that runs past the right edge of the chart
/// continues from the left edge, so a single operation can occupy several bands.
/// </summary>
public static class TimeMath
{
    public const double Epsilon = 1e-9;

    public static double Wrap(double t, double cycle)
    {
        if (cycle <= 0)
            return 0;
        var r = t % cycle;
        return r < 0 ? r + cycle : r;
    }

    /// <summary>
    /// Bands occupied by an operation inside [0, cycle]. Returns at most two bands, except that
    /// an operation at least a full cycle long simply covers the whole width.
    /// The first band always begins at the operation's real start, the last always ends at its end.
    /// </summary>
    public static List<(double From, double To)> Bands(double start, double duration, double cycle)
    {
        var result = new List<(double, double)>();
        if (cycle <= 0 || duration <= Epsilon)
        {
            if (cycle > 0 && duration > -Epsilon)
                result.Add((Wrap(start, cycle), Wrap(start, cycle)));
            return result;
        }

        if (duration >= cycle - Epsilon)
        {
            result.Add((0, cycle));
            return result;
        }

        var from = Wrap(start, cycle);
        var to = from + duration;
        if (to <= cycle + Epsilon)
        {
            result.Add((from, Math.Min(to, cycle)));
        }
        else
        {
            result.Add((from, cycle));
            result.Add((0, to - cycle));
        }
        return result;
    }

    /// <summary>Every positive-length intersection between two band lists.</summary>
    public static List<(double From, double To)> Intersect(
        IReadOnlyList<(double From, double To)> a,
        IReadOnlyList<(double From, double To)> b)
    {
        var result = new List<(double, double)>();
        foreach (var x in a)
        foreach (var y in b)
        {
            var from = Math.Max(x.From, y.From);
            var to = Math.Min(x.To, y.To);
            if (to - from > Epsilon)
                result.Add((from, to));
        }
        return result;
    }

    /// <summary>Sorts and coalesces overlapping bands.</summary>
    public static List<(double From, double To)> Merge(IEnumerable<(double From, double To)> bands)
    {
        var sorted = bands.Where(b => b.To - b.From > Epsilon).OrderBy(b => b.From).ToList();
        var result = new List<(double From, double To)>();
        foreach (var band in sorted)
        {
            if (result.Count > 0 && band.From <= result[^1].To + Epsilon)
            {
                if (band.To > result[^1].To)
                    result[^1] = (result[^1].From, band.To);
            }
            else
            {
                result.Add(band);
            }
        }
        return result;
    }

    public static double Length(IEnumerable<(double From, double To)> bands) =>
        bands.Sum(b => b.To - b.From);

    /// <summary>Compact display for a time value.</summary>
    public static string Format(double value) => value.ToString("0.###");
}
