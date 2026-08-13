using System.Windows;
using System.Windows.Media;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// Hands out visually distinct fills. Solid colours are nine hues chosen off the CIELCh hue circle;
/// once those run out, entries get a checkered pattern of a primary out of those nine and one of
/// eight secondaries picked for it, which is seventy-two more fills.
///
/// The hues are worked in CIELCh rather than HSV because HSV's hue is a corner-to-corner walk round
/// the RGB cube, where 40 degrees covers a wide perceptual jump in one place and barely a shift in
/// another. Lightness and chroma are then chosen per hue, since neither the luminance rule below nor
/// the sRGB gamut leaves them free.
///
/// **Which nine hues is a search, not a step.** Spacing them evenly round the circle is the obvious
/// thing and it does not work: the colours that come out are only as far apart as the gamut lets
/// them be, and round the cyan-azure-blue arc sRGB has very little chroma to offer, so three evenly
/// spaced hues there all land near the neutral axis at much the same lightness and read as three
/// pale blues. So the nine are picked to maximise the smallest difference between any two of the
/// colours actually produced - see <see cref="BuildHues"/>.
/// </summary>
public sealed class ColorAllocator
{
    private const int HueCount = 9;

    /// <summary>
    /// How much of a hue's strongest colour to actually use. Every hue is taken at the lightness
    /// where sRGB lets it be most colourful, which is a different lightness for each of them - a
    /// yellow is at its best pale and a blue dark, and holding them all at one lightness would give
    /// nine muddy colours of much the same brightness. Backing off the very edge of the gamut
    /// leaves them looking like paint rather than signal lights.
    /// </summary>
    private const double ChromaFraction = 0.78;

    /// <summary>
    /// How finely the hue circle is tried. The whole cost of <see cref="BuildHues"/> scales with
    /// this and the returns fall away fast: at five degrees the closest pair of fills comes out
    /// around 27, at two around 28, at one around 30, for 41, 67 and 115 milliseconds of work.
    /// </summary>
    private const double HueStep = 2;

    /// <summary>
    /// Cusps found so far. Declared here rather than beside <see cref="Cusp"/> because static
    /// initialisers run down the file and <see cref="BuildHues"/>, below, works colours out.
    /// </summary>
    private static readonly Dictionary<double, (double Lightness, double Chroma)> Cusps = new();

    private static readonly double[] Hues = BuildHues();

    /// <summary>
    /// The nine hues, chosen so that the smallest difference between any two of the colours they
    /// produce is as large as it can be, and ordered so that each slot is as far as possible from
    /// every slot handed out before it - a document using three colours gets three that are easy to
    /// tell apart, not the first three off a ring.
    ///
    /// Difference is CIEDE2000 rather than the straight distance in Lab, because the straight
    /// distance is the very assumption that fails here: it reads two saturated colours as further
    /// apart than the eye does, which is what lets a washed-out pair look acceptable on paper.
    ///
    /// This runs once, on the first colour anything asks for, and costs about seventy milliseconds -
    /// it works out a colour for every candidate hue, and each of those is a stack of gamut
    /// bisections. That is paid deliberately rather than kept as a table of angles, so that changing
    /// <see cref="ChromaFraction"/> or <see cref="LuminanceMargin"/> re-picks the palette to suit
    /// instead of silently spoiling it.
    /// </summary>
    private static double[] BuildHues()
    {
        var hues = new List<double>();
        for (var hue = 0.0; hue < 360; hue += HueStep)
            hues.Add(hue);

        var labs = hues.Select(h => Lab(FromHue(h))).ToArray();
        var gap = new double[hues.Count, hues.Count];
        for (var i = 0; i < hues.Count; i++)
        for (var j = i + 1; j < hues.Count; j++)
            gap[i, j] = gap[j, i] = Difference(labs[i], labs[j]);

        // How close a candidate would sit to the nearest thing in a set, which is what every step
        // below maximises. Something already in the set scores nothing, so nothing is taken twice.
        double Nearest(int candidate, IEnumerable<int> set) =>
            set.Select(i => gap[candidate, i]).DefaultIfEmpty(double.MaxValue).Min();

        // Start from the two furthest apart of all, then take whatever is furthest from what is
        // already held. That alone leaves the last few cramped, so each is then given up in turn
        // for whatever would sit furthest from the rest, until no swap helps.
        var chosen = new List<int>();
        var widest = (Gap: -1.0, A: 0, B: 0);
        for (var i = 0; i < hues.Count; i++)
        for (var j = i + 1; j < hues.Count; j++)
            if (gap[i, j] > widest.Gap)
                widest = (gap[i, j], i, j);
        chosen.Add(widest.A);
        chosen.Add(widest.B);

        while (chosen.Count < HueCount)
            chosen.Add(Enumerable.Range(0, hues.Count).MaxBy(c => Nearest(c, chosen)));

        for (var pass = 0; pass < ImprovementPasses; pass++)
        {
            var improved = false;
            for (var slot = 0; slot < chosen.Count; slot++)
            {
                var rest = chosen.Where((_, n) => n != slot).ToList();
                var best = Enumerable.Range(0, hues.Count).MaxBy(c => Nearest(c, rest));
                if (Nearest(best, rest) > Nearest(chosen[slot], rest) + Tolerance)
                {
                    chosen[slot] = best;
                    improved = true;
                }
            }
            if (!improved)
                break;
        }

        // Re-order the nine the same way they were gathered, since the swaps above pay no attention
        // to the order and the order is what spaces consecutive allocations.
        var order = new List<int>();
        var first = chosen.SelectMany(a => chosen.Select(b => (A: a, B: b)))
            .MaxBy(p => p.A == p.B ? -1 : gap[p.A, p.B]);
        order.Add(first.A);
        order.Add(first.B);
        while (order.Count < chosen.Count)
            order.Add(chosen.Except(order).MaxBy(c => Nearest(c, order)));

        return order.Select(i => hues[i]).ToArray();
    }

    /// <summary>Enough passes to settle; the loop leaves early, and normally after two or three.</summary>
    private const int ImprovementPasses = 50;

    /// <summary>A swap has to be worth more than rounding noise, or the loop never settles.</summary>
    private const double Tolerance = 1e-6;

    /// <summary>
    /// How far a fill's luminance is kept clear of the midpoint. Black and white text are both
    /// marginal on a colour sitting at half luminance, so no fill is allowed to sit there: each hue
    /// is taken to whichever side it was already nearer. It is what makes
    /// <see cref="GetTextBrush"/> a decision rather than a guess.
    /// </summary>
    private const double MidLuminance = 0.5;
    private const double LuminanceMargin = 0.15;

    /// <summary>
    /// Aimed at a shade past the margin, because the answer has to come back as whole bytes:
    /// rounding three channels can move a luminance by up to half a byte's worth, which without
    /// this would be enough to land a colour just inside the band it was moved out of.
    /// </summary>
    private const double RoundingAllowance = 0.5 / 255;

    /// <summary>How far from half luminance a colour has to be, allowance included.</summary>
    private const double Margin = LuminanceMargin + RoundingAllowance;

    /// <summary>
    /// How many secondaries each of the nine primaries is given, and so how many checkered fills
    /// there are before they start round again: nine times this.
    /// </summary>
    private const int SecondaryCount = 8;

    /// <summary>
    /// The grid a secondary is chosen off. Hues step by a multiple of <see cref="HueStep"/> so the
    /// cusps are the ones <see cref="BuildHues"/> has already worked out and cached; the luminance
    /// levels run from the near edge of the allowed band out towards black or white, stopping short
    /// of either, since the last stretch is all but colourless and would only offer near-duplicates.
    /// </summary>
    private const double SecondaryHueStep = 12;
    private const int LuminanceLevels = 4;
    private const double LuminanceLimit = 0.08;

    private static readonly (Color Primary, Color Secondary)[] CheckerPairs = BuildPairs();

    /// <summary>Checker tile size for fills, and a smaller one so the pattern still reads in a border.</summary>
    private const double FillTile = 14;
    private const double StrokeTile = 10;

    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Brush> _brushes = new();
    private readonly Dictionary<int, Brush> _strokeBrushes = new();

    public static int SolidCount => Hues.Length;

    /// <summary>
    /// Every checkered fill, worked out up front and ordered so the pairs whose two colours are
    /// furthest apart are handed out first.
    ///
    /// A secondary is no longer one of the nine solids bent to fit. Taking a hue and dragging it to
    /// the primary's exact luminance is what used to spoil these: reaching the luminance of a lime
    /// green costs a purple almost all its chroma, so lime's purple, orange and pink all arrived as
    /// much the same pale wash. A secondary is instead chosen freely off the grid above, holding
    /// only to what the pairing actually needs - that it sit the same side of half luminance as its
    /// primary, so one colour of text is right across the whole pattern and no tile glares beside
    /// its neighbour. That is a weaker rule than matching luminance outright, not a stronger one, so
    /// nothing that could be paired before is lost.
    ///
    /// The eight are chosen the way the hues are: maximise the smallest difference within the set,
    /// counting the primary as a member of it, so each secondary stands apart from the other seven
    /// and from the colour it will be laid against.
    ///
    /// **A fill is an unordered pair.** The tile is half one colour and half the other, so swapping
    /// them draws the same board shifted half a step - not a similar fill, the same one. Picking the
    /// eight a primary at a time cannot see that, since it never compares one primary's pairings with
    /// another's, and nothing stops a secondary landing on some other primary's colour. So the whole
    /// set is gone over afterwards by <see cref="Separate"/>, measuring one fill against another the
    /// way the eye takes them: as two colours in no particular order.
    /// </summary>
    private static (Color Primary, Color Secondary)[] BuildPairs()
    {
        var table = BuildTable();

        // Which colours each primary may draw a secondary from: the ones on its own side of half
        // luminance. The primaries themselves are in the table too, so they are candidates - the
        // separation below is what stops that turning into two fills that are the same pair twice.
        var pools = new List<int>[HueCount];
        var fills = new List<(int Primary, int Secondary)>();
        for (var primary = 0; primary < HueCount; primary++)
        {
            pools[primary] = Enumerable.Range(HueCount, table.Colors.Length - HueCount)
                .Where(i => table.Above[i] == table.Above[primary]).ToList();
            foreach (var secondary in Choose(SecondaryCount, pools[primary], primary, table))
                fills.Add((primary, secondary));
        }

        var separated = fills.ToArray();
        Separate(separated, pools, table);

        return separated
            .OrderByDescending(f => table.Gap[f.Primary, f.Secondary])
            .Select(f => (table.Colors[f.Primary], table.Colors[f.Secondary]))
            .ToArray();
    }

    /// <summary>
    /// Every colour a checkered fill can be built from, with the difference between each two of them
    /// worked out once. The nine primaries come first, so an index below <see cref="HueCount"/> is
    /// one of them; the rest is the grid a secondary is chosen off, both sides of half luminance in
    /// the one table so that a fill on one side can still be measured against a fill on the other.
    /// </summary>
    private sealed record Table(Color[] Colors, bool[] Above, double[,] Gap);

    private static Table BuildTable()
    {
        var colors = new List<Color>();
        var above = new List<bool>();

        foreach (var hue in Hues)
        {
            var primary = FromHue(hue);
            colors.Add(primary);
            above.Add(Luminance(primary) > MidLuminance);
        }

        foreach (var up in new[] { true, false })
        {
            var near = up ? MidLuminance + Margin : MidLuminance - Margin;
            var far = up ? 1 - LuminanceLimit : LuminanceLimit;

            for (var hue = 0.0; hue < 360; hue += SecondaryHueStep)
            for (var level = 0; level < LuminanceLevels; level++)
            {
                colors.Add(AtLuminance(hue, near + (far - near) * level / (LuminanceLevels - 1.0)));
                above.Add(up);
            }
        }

        var labs = colors.Select(Lab).ToArray();
        var gap = new double[labs.Length, labs.Length];
        for (var i = 0; i < labs.Length; i++)
        for (var j = i + 1; j < labs.Length; j++)
            gap[i, j] = gap[j, i] = Difference(labs[i], labs[j]);

        return new Table(colors.ToArray(), above.ToArray(), gap);
    }

    /// <summary>
    /// Takes <paramref name="count"/> colours out of <paramref name="pool"/>, keeping them as far
    /// from each other and from <paramref name="seed"/> as they can be got. Same two steps as the
    /// hues: take what is furthest from everything held so far, then give each up for anything
    /// better. Returns them furthest from the seed first.
    /// </summary>
    private static List<int> Choose(int count, List<int> pool, int seed, Table table)
    {
        // Anything already held is at no distance from itself, so nothing is taken twice.
        double Nearest(int candidate, IEnumerable<int> set) => Math.Min(table.Gap[candidate, seed],
            set.Select(i => table.Gap[candidate, i]).DefaultIfEmpty(double.MaxValue).Min());

        var chosen = new List<int>();
        while (chosen.Count < count)
            chosen.Add(pool.MaxBy(c => Nearest(c, chosen)));

        for (var pass = 0; pass < ImprovementPasses; pass++)
        {
            var improved = false;
            for (var slot = 0; slot < chosen.Count; slot++)
            {
                var rest = chosen.Where((_, n) => n != slot).ToList();
                var best = pool.MaxBy(c => Nearest(c, rest));
                if (Nearest(best, rest) > Nearest(chosen[slot], rest) + Tolerance)
                {
                    chosen[slot] = best;
                    improved = true;
                }
            }
            if (!improved)
                break;
        }

        return chosen.OrderByDescending(c => table.Gap[c, seed]).ToList();
    }

    /// <summary>
    /// How unlike one fill is to another, taking each as the two colours it is made of and nothing
    /// more. The pair can be lined up two ways round and the eye is free to take either, so the
    /// answer is the closer of the two - which reads a swapped pair as no difference at all, and
    /// makes a near-swap nearly none.
    /// </summary>
    private static double Apart((int Primary, int Secondary) a, (int Primary, int Secondary) b, Table table) =>
        Math.Min(
            Math.Max(table.Gap[a.Primary, b.Primary], table.Gap[a.Secondary, b.Secondary]),
            Math.Max(table.Gap[a.Primary, b.Secondary], table.Gap[a.Secondary, b.Primary]));

    /// <summary>
    /// Pulls the closest two fills apart, over and over, until nothing can be bettered. Each round
    /// finds the two least unlike each other and tries to re-cut one of them from its own primary's
    /// pool, taking the first cut that clears the figure those two are stuck at.
    ///
    /// What is asked of a re-cut is that **every** pairing the re-cut fill is now part of clears that
    /// figure - not that the whole set's worst improves. Those come apart when two pairs are stuck at
    /// the same figure, which is exactly the case this exists for: two swapped duplicates both sit at
    /// no difference at all, and mending one would leave the set's worst still at nothing and see the
    /// mend refused. Asking only about the fill being re-cut takes the pairs one at a time, and since
    /// no cut is allowed to introduce anything as close as the figure it cleared, the set cannot go
    /// backwards and the loop must settle.
    ///
    /// A replacement also has to stand apart from the primary it goes under, or a fill would stop
    /// reading as two colours - which is the one thing the measure above cannot see, since it
    /// compares fills with each other and never looks inside one.
    /// </summary>
    private static void Separate((int Primary, int Secondary)[] fills, List<int>[] pools, Table table)
    {
        var apart = new double[fills.Length, fills.Length];
        for (var i = 0; i < fills.Length; i++)
        for (var j = i + 1; j < fills.Length; j++)
            apart[i, j] = apart[j, i] = Apart(fills[i], fills[j], table);

        for (var round = 0; round < SeparationRounds; round++)
        {
            var worst = (Apart: double.MaxValue, A: 0, B: 0);
            for (var i = 0; i < fills.Length; i++)
            for (var j = i + 1; j < fills.Length; j++)
                if (apart[i, j] < worst.Apart)
                    worst = (apart[i, j], i, j);

            if (!Recut(worst.A, worst.Apart) && !Recut(worst.B, worst.Apart))
                break;
        }

        // Tries to re-cut one fill so that nothing it is part of sits as close as floor any more.
        bool Recut(int fill, double floor)
        {
            foreach (var candidate in pools[fills[fill].Primary])
            {
                if (candidate == fills[fill].Secondary)
                    continue;

                // Starting from the distance to its own primary keeps a fill reading as two colours.
                var trial = (fills[fill].Primary, candidate);
                var lowest = table.Gap[trial.Primary, candidate];
                for (var other = 0; other < fills.Length && lowest > floor + Tolerance; other++)
                    if (other != fill)
                        lowest = Math.Min(lowest, Apart(trial, fills[other], table));

                if (lowest <= floor + Tolerance)
                    continue;

                fills[fill] = trial;
                for (var other = 0; other < fills.Length; other++)
                    if (other != fill)
                        apart[fill, other] = apart[other, fill] = Apart(trial, fills[other], table);
                return true;
            }
            return false;
        }
    }

    /// <summary>Enough rounds to settle. The loop leaves as soon as neither fill can be bettered.</summary>
    private const int SeparationRounds = 500;

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
    /// The one or two colours a slot is drawn in. Both colours of a checkered slot sit the same side
    /// of half luminance, so no tile glares beside its neighbour and text laid over the pattern is
    /// the same choice on either of them.
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
            colors = (pair.Primary, pair.Secondary);
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
        if (Math.Abs(luminance - MidLuminance) >= Margin)
            return luminance;
        return luminance < MidLuminance ? MidLuminance - Margin : MidLuminance + Margin;
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
    /// gamut for that hue. A slice of the RGB cube at one hue rises to a single corner and falls
    /// away either side, so the peak is closed in on rather than scanned for: a golden-section
    /// search keeps whichever two thirds of the range still hold it, and reuses one of its two
    /// probes each time round. Worth doing well - <see cref="BuildHues"/> asks for a cusp per
    /// candidate hue, not per fill.
    /// </summary>
    private static (double Lightness, double Chroma) Cusp(double hue)
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

        // Down to a tenth of a unit of lightness, which is finer than the old scan resolved.
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

    // ------------------------------------------------------- sRGB back to Lab, and difference

    /// <summary>
    /// The reverse of the trip above: a colour as sRGB holds it, back to the Lab coordinates the
    /// difference below is defined on. Only <see cref="BuildHues"/> needs this, and only once.
    /// </summary>
    private static (double L, double A, double B) Lab(Color color)
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

    /// <summary>
    /// CIEDE2000 between two Lab colours - how different they look, as the standard has it. The
    /// corrections it carries over a plain distance are exactly the ones that matter here: it pulls
    /// back differences between strongly coloured pairs, which a plain distance flatters, and it
    /// treats a hue shift in the blues more carefully than one elsewhere.
    /// </summary>
    private static double Difference((double L, double A, double B) p, (double L, double A, double B) q)
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
        // does not see - it only bites around hue 275, and that is the arc this palette lives on.
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

    public static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor),
        (byte)(color.G * factor),
        (byte)(color.B * factor));

    private static double Luminance(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
