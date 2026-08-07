using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CellGanttChartEditor2.Dialogs;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Controls;

/// <summary>
/// The looping Gantt chart. Everything inside the surface is drawn by hand: row headers, the time
/// ruler, wrapped bars, overlap markers and links. Bars can be dragged to reschedule, or dropped
/// onto another bar to link them.
/// </summary>
public partial class ChartView : UserControl
{
    private const double HeaderWidth = 168;
    private const double RulerHeight = 28;
    private const double RowHeight = 38;
    private const double BarInset = 6;
    private const double DragSlop = 4;

    /// <summary>How close a drag must land to another bar's end for that end to win over the grid.</summary>
    private const double BarEndSnapWindow = 0.1;

    /// <summary>Grab zone on a bar's trailing edge, and the narrowest bar that gets one.</summary>
    private const double ResizeGripWidth = 6;
    private const double MinResizeGripBar = 16;

    private const double MinOverlapWidth = 5;
    private const double LinkElbow = 12;

    private static readonly Brush SurfaceBrush = Palette.Brush(Palette.ChartSurface);
    private static readonly Brush RowBrushA = Palette.Brush(Palette.RowA);
    private static readonly Brush RowBrushB = Palette.Brush(Palette.RowB);
    private static readonly Brush TextBrush = Palette.Brush(Palette.Text);
    private static readonly Brush MutedBrush = Palette.Brush(Palette.Muted);
    private static readonly Pen GridPen = Palette.Pen(Palette.Grid, 1);
    private static readonly Pen DividerPen = Palette.Pen(Palette.Grid, 1.5);
    private static readonly Pen CycleEdgePen = Palette.Pen(Palette.CycleEdge, 1.5);
    private static readonly Pen BarOutlinePen = Palette.Pen(Palette.BarOutline, 1);
    private static readonly Pen SelectionPen = Palette.Pen(Colors.White, 2.5);
    private static readonly Pen[] SelectionGlowPens = Palette.GlowPens(1);
    private static readonly Pen DropTargetPen = Palette.Pen(Color.FromRgb(0x3D, 0xDC, 0x84), 3);
    private static readonly Pen OverlapPen = Palette.Pen(Colors.Black, 2);
    private static readonly Pen LinkPen = Palette.Pen(Palette.Link, 1.3);
    private static readonly Pen SelectedLinkPen = Palette.Pen(Colors.White, 2.4);
    private static readonly Brush LinkBrush = Palette.Brush(Palette.Link);
    private static readonly Pen GhostPen = MakeGhostPen();

    /// <summary>Rounded ends on the bars, as in the previous editor.</summary>
    private const double BarCornerRadius = 3;
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly List<RowInfo> _rows = new();
    private readonly Dictionary<Guid, int> _rowOfOperation = new();
    private readonly List<(Rect Rect, Operation Operation)> _barHits = new();
    private readonly List<(Rect Rect, Operation Operation)> _resizeHits = new();
    private readonly List<(Point[] Points, OperationLink Link)> _linkHits = new();
    private readonly List<(Rect Rect, RowInfo Row)> _headerHits = new();
    private readonly ToolTip _tip = new()
    {
        Placement = PlacementMode.Relative,
        StaysOpen = true,
        IsHitTestVisible = false,
    };

    private double _zoom = 1;
    private double _scrollX;
    private double _scrollY;
    private double _pxPerUnit = 1;
    private bool _updatingScrollBars;

    private Operation? _selectedOperation;
    private OperationLink? _selectedLink;

    private Operation? _pressedOperation;
    private OperationLink? _pressedLink;
    private Point _pressPoint;
    private bool _dragging;
    private bool _resizing;
    private double _dragOriginalStart;
    private double _dragOriginalDuration;
    private Operation? _dropTarget;
    private List<Guid>? _chronologicalFreeze;

    private object? _hovered;
    private TextBox? _headerEditor;
    private RowInfo? _headerEditorRow;

    public ChartView()
    {
        InitializeComponent();
        Surface.Owner = this;
        _tip.PlacementTarget = Surface;
        Loaded += (_, _) => Surface.InvalidateVisual();
    }

    // ------------------------------------------------------------- public API

    public GanttDocument? Document { get; private set; }

    public ColorAllocator RegionColors { get; set; } = new();

    public ColorAllocator RobotColors { get; set; } = new();

    public ChartGroupMode GroupMode { get; private set; } = ChartGroupMode.Region;

    public ChartColorBy ColorMode { get; private set; } = ChartColorBy.Region;

    /// <summary>Regions to keep. Empty means no filtering.</summary>
    public HashSet<Guid> RegionFilter { get; } = new();

    public bool ShowOverlaps { get; private set; } = true;

    public ConflictResult Conflicts { get; private set; } = ConflictResult.Empty;

    /// <summary>Raised when the user changed the document through the chart.</summary>
    public event EventHandler? Edited;

    /// <summary>Raised when the conflict set was recomputed.</summary>
    public event EventHandler? ConflictsChanged;

    /// <summary>Region under the pointer, so the host can highlight its border on the image.</summary>
    public event EventHandler<Guid?>? HoveredRegionChanged;

    public void SetDocument(GanttDocument? document)
    {
        Document = document;
        _selectedOperation = null;
        _selectedLink = null;
        RegionFilter.Clear();
        _zoom = 1;
        _scrollX = 0;
        _scrollY = 0;
        Refresh();
    }

    public void SetGroupMode(ChartGroupMode mode)
    {
        GroupMode = mode;
        Refresh();
    }

    public void SetColorMode(ChartColorBy mode)
    {
        ColorMode = mode;
        Refresh();
    }

    public void SetShowOverlaps(bool show)
    {
        ShowOverlaps = show;
        Surface.InvalidateVisual();
    }

    /// <summary>Recomputes conflicts and redraws.</summary>
    public void Refresh()
    {
        RecomputeConflicts();
        Surface.InvalidateVisual();
    }

    public void RecomputeConflicts()
    {
        Conflicts = Document == null ? ConflictResult.Empty : ConflictDetector.Analyse(Document);
        ConflictsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Colour source actually in use: the grouping decides it unless in chronological mode.</summary>
    public ChartColorBy EffectiveColorBy => GroupMode switch
    {
        ChartGroupMode.Region => ChartColorBy.Region,
        ChartGroupMode.Robot => ChartColorBy.Robot,
        _ => ColorMode,
    };

    private void RaiseEdited()
    {
        RecomputeConflicts();
        Surface.InvalidateVisual();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ rows

    private enum RowKind { Robot, Region, Operation }

    private sealed class RowInfo
    {
        public string Title = string.Empty;
        public RowKind Kind;
        public string RobotName = string.Empty;
        public Guid RegionId;
        public Operation? Single;
        public List<Operation> Operations = new();
        public HashSet<Guid> Ghosted = new();
    }

    private void BuildRows()
    {
        _rows.Clear();
        _rowOfOperation.Clear();
        if (Document == null)
            return;

        var filtering = RegionFilter.Count > 0;
        var primary = new HashSet<Guid>();
        var ghosted = new HashSet<Guid>();

        if (filtering)
        {
            foreach (var op in Document.Operations)
                if (RegionFilter.Contains(op.RegionId))
                    primary.Add(op.Id);

            // Anything linked to a kept operation stays visible, but faded.
            foreach (var link in Document.Links)
            {
                if (primary.Contains(link.SourceId) && !primary.Contains(link.TargetId))
                    ghosted.Add(link.TargetId);
                if (primary.Contains(link.TargetId) && !primary.Contains(link.SourceId))
                    ghosted.Add(link.SourceId);
            }
        }

        bool Keep(Operation op) => !filtering || primary.Contains(op.Id) || ghosted.Contains(op.Id);

        switch (GroupMode)
        {
            case ChartGroupMode.Region:
                foreach (var robot in Document.Robots())
                {
                    var ops = Document.Operations
                        .Where(o => string.Equals(o.RobotName, robot, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (filtering && !ops.Any(o => primary.Contains(o.Id)))
                        continue;
                    AddRow(new RowInfo
                    {
                        Title = robot,
                        Kind = RowKind.Robot,
                        RobotName = robot,
                        Operations = ops.Where(Keep).ToList(),
                        Ghosted = ghosted,
                    });
                }
                break;

            case ChartGroupMode.Robot:
                foreach (var region in Document.UsedRegions())
                {
                    var ops = Document.Operations.Where(o => o.RegionId == region.Id).ToList();
                    if (filtering && !ops.Any(o => primary.Contains(o.Id)))
                        continue;
                    AddRow(new RowInfo
                    {
                        Title = region.Name,
                        Kind = RowKind.Region,
                        RegionId = region.Id,
                        Operations = ops.Where(Keep).ToList(),
                        Ghosted = ghosted,
                    });
                }
                break;

            default:
                foreach (var op in OrderChronologically())
                {
                    // One row per operation, so a linked operation keeps its row and just fades.
                    if (filtering && !primary.Contains(op.Id) && !ghosted.Contains(op.Id))
                        continue;
                    AddRow(new RowInfo
                    {
                        Title = op.Name,
                        Kind = RowKind.Operation,
                        Single = op,
                        Operations = new List<Operation> { op },
                        Ghosted = ghosted,
                    });
                }
                break;
        }

        void AddRow(RowInfo row)
        {
            var index = _rows.Count;
            _rows.Add(row);
            foreach (var op in row.Operations)
                _rowOfOperation[op.Id] = index;
        }
    }

    private IEnumerable<Operation> OrderChronologically()
    {
        if (Document == null)
            return Array.Empty<Operation>();

        // While dragging, keep the row order fixed so bars do not jump under the cursor.
        if (_chronologicalFreeze != null)
        {
            var order = _chronologicalFreeze;
            return Document.Operations
                .OrderBy(o => order.IndexOf(o.Id) is var i && i >= 0 ? i : int.MaxValue)
                .ToList();
        }

        return Document.Operations
            .OrderBy(o => o.Start)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private string ColorKeyOf(Operation op) => EffectiveColorBy == ChartColorBy.Robot
        ? op.RobotName
        : Document?.RegionOf(op)?.ColorKey ?? string.Empty;

    private ColorAllocator Allocator => EffectiveColorBy == ChartColorBy.Robot ? RobotColors : RegionColors;

    private string LabelOf(Operation op)
    {
        if (GroupMode != ChartGroupMode.Chronological)
            return op.Name;

        var other = EffectiveColorBy == ChartColorBy.Robot
            ? Document?.RegionOf(op)?.Name ?? "(region)"
            : op.RobotName;
        return $"{op.Name} - {other}";
    }

    // --------------------------------------------------------------- drawing

    internal void RenderChart(DrawingContext dc, Size size)
    {
        _barHits.Clear();
        _resizeHits.Clear();
        _linkHits.Clear();
        _headerHits.Clear();

        dc.DrawRectangle(SurfaceBrush, null, new Rect(size));

        if (Document == null || size.Width < 40 || size.Height < 40)
            return;

        BuildRows();

        var cycle = Math.Max(GanttDocument.MinCycleTime, Document.CycleTime);
        var viewportW = Math.Max(10, size.Width - HeaderWidth);
        var contentW = viewportW * _zoom;
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, contentW - viewportW));
        _pxPerUnit = contentW / cycle;

        var viewportH = Math.Max(10, size.Height - RulerHeight);
        var totalH = _rows.Count * RowHeight;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, totalH - viewportH));

        double X(double t) => HeaderWidth + t * _pxPerUnit - _scrollX;
        double RowTop(int index) => RulerHeight + index * RowHeight - _scrollY;

        var chartRect = new Rect(HeaderWidth, RulerHeight, size.Width - HeaderWidth, size.Height - RulerHeight);
        var step = TickStep();

        DrawRowBands(dc, size, RowTop);
        DrawRuler(dc, size, cycle, step, X);

        dc.PushClip(new RectangleGeometry(chartRect));

        for (var t = 0.0; t <= cycle + TimeMath.Epsilon; t += step)
        {
            var x = X(t);
            if (x >= HeaderWidth - 1 && x <= size.Width)
                dc.DrawLine(GridPen, new Point(x, RulerHeight), new Point(x, size.Height));
        }

        // The loop boundary, marked at both ends: everything past the right edge reappears on the left.
        foreach (var edge in new[] { X(0), X(cycle) })
            dc.DrawLine(CycleEdgePen, new Point(edge, RulerHeight), new Point(edge, size.Height));

        DrawGhost(dc, RowTop, X, cycle);

        for (var i = 0; i < _rows.Count; i++)
        {
            var top = RowTop(i);
            if (top > size.Height || top + RowHeight < RulerHeight)
                continue;
            foreach (var op in _rows[i].Operations)
                DrawBar(dc, op, top, _rows[i].Ghosted.Contains(op.Id) && RegionFilter.Count > 0, X, cycle);
        }

        DrawLinks(dc, RowTop, X, cycle);

        dc.Pop();

        DrawHeaders(dc, size, RowTop);
        UpdateScrollBars(contentW, viewportW, totalH, viewportH);

        if (_rows.Count == 0)
        {
            var hint = Text(Document.Operations.Count == 0
                ? "No operations yet - use \"Add operation\" to place the first one."
                : "Nothing matches the current region filter.", 13, MutedBrush, Face);
            dc.DrawText(hint, new Point(HeaderWidth + 20, RulerHeight + 20));
        }
    }

    private void DrawRowBands(DrawingContext dc, Size size, Func<int, double> rowTop)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var top = rowTop(i);
            if (top > size.Height || top + RowHeight < RulerHeight)
                continue;
            // Alternating bands run the full width, header column included.
            dc.DrawRectangle(RowBand(i), null, new Rect(0, top, size.Width, RowHeight));
        }
    }

    private static Brush RowBand(int index) => index % 2 == 0 ? RowBrushA : RowBrushB;

    private void DrawHeaders(DrawingContext dc, Size size, Func<int, double> rowTop)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, HeaderWidth, size.Height - RulerHeight)));
        dc.DrawRectangle(SurfaceBrush, null, new Rect(0, RulerHeight, HeaderWidth, size.Height - RulerHeight));

        for (var i = 0; i < _rows.Count; i++)
        {
            var top = rowTop(i);
            if (top > size.Height || top + RowHeight < RulerHeight)
                continue;

            var rect = new Rect(0, top, HeaderWidth, RowHeight);
            _headerHits.Add((rect, _rows[i]));
            dc.DrawRectangle(RowBand(i), null, rect);

            var row = _rows[i];
            // In "group by robot" the header is a region, so it carries the region's colour.
            if (row.Kind == RowKind.Region)
            {
                var region = Document?.FindRegion(row.RegionId);
                if (region != null)
                    dc.DrawRoundedRectangle(RegionColors.GetBrush(region.ColorKey), null,
                        new Rect(0, top + 5, 5, RowHeight - 10), 2, 2);
            }

            var text = Text(row.Title, 12.5, TextBrush, BoldFace);
            text.MaxTextWidth = HeaderWidth - 16;
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(text, new Point(10, top + (RowHeight - text.Height) / 2));
        }

        dc.Pop();
        dc.DrawLine(DividerPen, new Point(HeaderWidth, 0), new Point(HeaderWidth, size.Height));
    }

    private void DrawRuler(DrawingContext dc, Size size, double cycle, double step, Func<double, double> x)
    {
        dc.DrawRectangle(SurfaceBrush, null, new Rect(0, 0, size.Width, RulerHeight));
        dc.DrawLine(GridPen, new Point(0, RulerHeight), new Point(size.Width, RulerHeight));

        var caption = GroupMode switch
        {
            ChartGroupMode.Region => "Robot",
            ChartGroupMode.Robot => "Region",
            _ => "Operation",
        };
        dc.DrawText(Text(caption, 12, MutedBrush, BoldFace), new Point(10, 6));

        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, size.Width - HeaderWidth, RulerHeight)));
        for (var t = 0.0; t <= cycle + TimeMath.Epsilon; t += step)
        {
            var px = x(t);
            if (px < HeaderWidth - 40 || px > size.Width + 40)
                continue;
            dc.DrawLine(CycleEdgePen, new Point(px, RulerHeight - 6), new Point(px, RulerHeight));
            dc.DrawText(Text(TimeMath.Format(t), 11, MutedBrush, Face), new Point(px + 3, 4));
        }
        dc.Pop();
    }

    private void DrawGhost(DrawingContext dc, Func<int, double> rowTop, Func<double, double> x, double cycle)
    {
        if (!_dragging || _pressedOperation == null)
            return;
        if (!_rowOfOperation.TryGetValue(_pressedOperation.Id, out var rowIndex))
            return;

        var top = rowTop(rowIndex);
        foreach (var band in TimeMath.Bands(_dragOriginalStart, _dragOriginalDuration, cycle))
        {
            var rect = BandRect(band, top, x);
            if (rect.Width <= 0)
                continue;
            dc.DrawRoundedRectangle(null, GhostPen, rect, BarCornerRadius, BarCornerRadius);
        }
    }

    private static Rect BandRect((double From, double To) band, double top, Func<double, double> x)
    {
        var left = x(band.From);
        var right = x(band.To);
        return new Rect(left, top + BarInset, Math.Max(2, right - left), RowHeight - BarInset * 2);
    }

    private void DrawBar(DrawingContext dc, Operation op, double top, bool ghosted,
        Func<double, double> x, double cycle)
    {
        var key = ColorKeyOf(op);
        var fill = Allocator.GetBrush(key);
        var bands = TimeMath.Bands(op.Start, op.Duration, cycle);
        var selected = ReferenceEquals(op, _selectedOperation);
        var isDropTarget = ReferenceEquals(op, _dropTarget);

        if (ghosted)
            dc.PushOpacity(Palette.DimmedOpacity);

        Rect widest = Rect.Empty;
        Rect last = Rect.Empty;
        foreach (var band in bands)
        {
            var rect = BandRect(band, top, x);
            _barHits.Add((rect, op));
            last = rect;

            // A selected bar sits inside a white halo, standing in for the old drop shadow.
            if (selected)
                foreach (var glow in SelectionGlowPens)
                    dc.DrawRoundedRectangle(null, glow, rect, BarCornerRadius, BarCornerRadius);

            dc.DrawRoundedRectangle(fill, BarOutlinePen, rect, BarCornerRadius, BarCornerRadius);

            if (ShowOverlaps)
                DrawOverlaps(dc, op, band, rect, x);

            if (selected)
                dc.DrawRoundedRectangle(null, SelectionPen, rect, BarCornerRadius, BarCornerRadius);
            if (isDropTarget)
                dc.DrawRoundedRectangle(null, DropTargetPen, rect, BarCornerRadius, BarCornerRadius);

            if (widest == Rect.Empty || rect.Width > widest.Width)
                widest = rect;
        }

        if (widest != Rect.Empty && widest.Width > 34)
            DrawBarLabel(dc, op, widest);

        // Resize grip on the trailing edge. The last band always ends at the operation's end,
        // except for a bar at least a cycle long - there the right edge is the loop boundary, not
        // the end. Narrow bars are left alone so the grip never swallows the whole bar.
        if (last.Width >= MinResizeGripBar && op.Duration < cycle - TimeMath.Epsilon)
            _resizeHits.Add((new Rect(last.Right - ResizeGripWidth, last.Top,
                ResizeGripWidth, last.Height), op));

        if (ghosted)
            dc.Pop();
    }

    /// <summary>
    /// Loud white bands over the parts of a bar that clash with another bar on the same robot or
    /// region. A minimum width plus a heavy outline keeps very short overlaps visible.
    /// </summary>
    private void DrawOverlaps(DrawingContext dc, Operation op, (double From, double To) band, Rect barRect,
        Func<double, double> x)
    {
        if (!Conflicts.OverlapBands.TryGetValue(op.Id, out var overlaps))
            return;

        foreach (var overlap in overlaps)
        {
            var from = Math.Max(overlap.From, band.From);
            var to = Math.Min(overlap.To, band.To);
            if (to - from <= TimeMath.Epsilon)
                continue;

            var left = x(from);
            var width = Math.Max(MinOverlapWidth, x(to) - left);
            // Keep the marker inside the bar even after the minimum-width bump.
            if (left + width > barRect.Right)
                left = Math.Max(barRect.Left, barRect.Right - width);

            var rect = new Rect(left, barRect.Top + 1, width, Math.Max(1, barRect.Height - 2));
            dc.DrawRectangle(Brushes.White, OverlapPen, rect);
        }
    }

    /// <summary>
    /// Bar labels are black: every allocated fill is a bright hue, and the overlap markers are white,
    /// so one colour stays readable over both.
    /// </summary>
    private void DrawBarLabel(DrawingContext dc, Operation op, Rect rect)
    {
        var text = Text(LabelOf(op), 11.5, Brushes.Black, BoldFace);
        text.MaxTextWidth = Math.Max(8, rect.Width - 8);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;

        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(text, new Point(rect.Left + 5, rect.Top + (rect.Height - text.Height) / 2));
        dc.Pop();
    }

    private void DrawLinks(DrawingContext dc, Func<int, double> rowTop, Func<double, double> x, double cycle)
    {
        if (Document == null)
            return;

        foreach (var link in Document.Links)
        {
            var source = Document.FindOperation(link.SourceId);
            var target = Document.FindOperation(link.TargetId);
            if (source == null || target == null)
                continue;
            if (!_rowOfOperation.TryGetValue(source.Id, out var sourceRow) ||
                !_rowOfOperation.TryGetValue(target.Id, out var targetRow))
                continue;

            var sourceBands = TimeMath.Bands(source.Start, source.Duration, cycle);
            var targetBands = TimeMath.Bands(target.Start, target.Duration, cycle);
            if (sourceBands.Count == 0 || targetBands.Count == 0)
                continue;

            var from = new Point(x(sourceBands[^1].To), rowTop(sourceRow) + RowHeight / 2);
            var to = new Point(x(targetBands[0].From), rowTop(targetRow) + RowHeight / 2);

            var points = Route(from, to);
            _linkHits.Add((points, link));

            var selected = ReferenceEquals(link, _selectedLink);
            var pen = selected ? SelectedLinkPen : LinkPen;
            var head = selected ? Brushes.White : LinkBrush;

            // Fade the arrow to match when either end is only on screen because of the filter.
            var dimmed = RegionFilter.Count > 0 &&
                         (_rows[sourceRow].Ghosted.Contains(source.Id) || _rows[targetRow].Ghosted.Contains(target.Id));
            if (dimmed)
                dc.PushOpacity(Palette.DimmedOpacity);

            for (var i = 0; i < points.Length - 1; i++)
                dc.DrawLine(pen, points[i], points[i + 1]);

            DrawArrowHead(dc, to, head);

            if (dimmed)
                dc.Pop();
        }
    }

    /// <summary>Orthogonal route from a source end to a target start, always arriving from the left.</summary>
    private static Point[] Route(Point from, Point to)
    {
        if (to.X > from.X + LinkElbow * 2)
        {
            var mid = from.X + LinkElbow;
            return new[] { from, new Point(mid, from.Y), new Point(mid, to.Y), to };
        }

        // Target sits left of the source (usually because one of them wrapped): detour around.
        var midY = (from.Y + to.Y) / 2 + (Math.Abs(to.Y - from.Y) < 1 ? RowHeight / 2 - 2 : 0);
        return new[]
        {
            from,
            new Point(from.X + LinkElbow, from.Y),
            new Point(from.X + LinkElbow, midY),
            new Point(to.X - LinkElbow, midY),
            new Point(to.X - LinkElbow, to.Y),
            to,
        };
    }

    private static void DrawArrowHead(DrawingContext dc, Point tip, Brush brush)
    {
        const double length = 10;
        const double halfWidth = 3.5;

        var figure = new PathFigure
        {
            StartPoint = tip,
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new LineSegment(new Point(tip.X - length, tip.Y - halfWidth), true));
        figure.Segments.Add(new LineSegment(new Point(tip.X - length, tip.Y + halfWidth), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private double TickStep()
    {
        var raw = 70 / Math.Max(0.0001, _pxPerUnit);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1e-6, raw))));
        var normalised = raw / magnitude;
        var factor = normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10;
        return Math.Max(1e-6, factor * magnitude);
    }

    private void UpdateScrollBars(double contentW, double viewportW, double totalH, double viewportH)
    {
        if (_updatingScrollBars)
            return;

        _updatingScrollBars = true;
        try
        {
            var maxX = Math.Max(0, contentW - viewportW);
            HScroll.Maximum = maxX;
            HScroll.ViewportSize = viewportW;
            HScroll.LargeChange = viewportW * 0.9;
            HScroll.SmallChange = 20;
            HScroll.Value = Math.Clamp(_scrollX, 0, maxX);
            HScroll.Visibility = maxX > 0.5 ? Visibility.Visible : Visibility.Collapsed;

            var maxY = Math.Max(0, totalH - viewportH);
            VScroll.Maximum = maxY;
            VScroll.ViewportSize = viewportH;
            VScroll.LargeChange = viewportH * 0.9;
            VScroll.SmallChange = RowHeight;
            VScroll.Value = Math.Clamp(_scrollY, 0, maxY);
            VScroll.Visibility = maxY > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updatingScrollBars = false;
        }
    }

    private void HScroll_Scroll(object sender, ScrollEventArgs e)
    {
        if (_updatingScrollBars)
            return;
        _scrollX = HScroll.Value;
        Surface.InvalidateVisual();
    }

    private void VScroll_Scroll(object sender, ScrollEventArgs e)
    {
        if (_updatingScrollBars)
            return;
        _scrollY = VScroll.Value;
        Surface.InvalidateVisual();
    }

    // ----------------------------------------------------------------- input

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Document == null)
            return;

        var position = e.GetPosition(Surface);

        if (position.X < HeaderWidth)
        {
            _scrollY = Math.Max(0, _scrollY - Math.Sign(e.Delta) * RowHeight);
            Surface.InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Wheel over the chart changes the horizontal zoom, pinned to the time under the cursor.
        var cycle = Math.Max(GanttDocument.MinCycleTime, Document.CycleTime);
        var viewportW = Math.Max(10, Surface.ActualWidth - HeaderWidth);
        var oldPxPerUnit = viewportW * _zoom / cycle;
        var timeAtCursor = (position.X - HeaderWidth + _scrollX) / oldPxPerUnit;

        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1, 500);

        var newPxPerUnit = viewportW * _zoom / cycle;
        _scrollX = timeAtCursor * newPxPerUnit - (position.X - HeaderWidth);

        Surface.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // Clicks inside the inline row-header editor belong to the editor, not the chart.
        if (_headerEditor != null && e.OriginalSource is Visual source &&
            (ReferenceEquals(source, _headerEditor) || _headerEditor.IsAncestorOf(source)))
            return;

        CommitHeaderEditor();
        Focus();

        var position = e.GetPosition(Surface);
        if (position.X < 0 || position.Y < 0 ||
            position.X > Surface.ActualWidth || position.Y > Surface.ActualHeight)
            return;

        if (e.ClickCount == 2)
        {
            HandleDoubleClick(position);
            e.Handled = true;
            return;
        }

        _pressedOperation = HitBar(position);
        _resizing = _pressedOperation != null && OnResizeGrip(_pressedOperation, position);
        _pressedLink = _pressedOperation == null ? HitLink(position) : null;
        _pressPoint = position;

        _selectedOperation = _pressedOperation;
        _selectedLink = _pressedLink;

        if (_pressedOperation != null)
        {
            _dragOriginalStart = _pressedOperation.Start;
            _dragOriginalDuration = _pressedOperation.Duration;
            Surface.CaptureMouse();
        }

        HideTip();
        Surface.InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Document == null)
            return;

        var position = e.GetPosition(Surface);

        if (_pressedOperation != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && (position - _pressPoint).Length < DragSlop)
                return;

            if (!_dragging)
            {
                _dragging = true;
                _chronologicalFreeze = Document.Operations.Select(o => o.Id).ToList();
                HideTip();
            }

            var delta = (position.X - _pressPoint.X) / Math.Max(1e-6, _pxPerUnit);
            var snap = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;

            if (_resizing)
            {
                ResizeTo(_pressedOperation, _dragOriginalStart + _dragOriginalDuration + delta, snap);
                Surface.Cursor = Cursors.SizeWE;
            }
            else
            {
                var raw = Math.Max(0, _dragOriginalStart + delta);
                var start = Math.Round(SnapTime(_pressedOperation, raw, snap), 3);
                Document.MoveOperation(_pressedOperation, start);

                // Dropping onto a bar links the two. Resizing is not that gesture.
                _dropTarget = HitBar(position, exclude: _pressedOperation);
            }

            RecomputeConflicts();
            Surface.InvalidateVisual();
            return;
        }

        UpdateHover(position);
    }

    /// <summary>
    /// Drags an operation's trailing edge to <paramref name="rawEnd"/>. The start stays put, so any
    /// link feeding this bar keeps its length; links leaving it drag their targets along as usual.
    /// </summary>
    private void ResizeTo(Operation op, double rawEnd, bool snap)
    {
        if (Document == null)
            return;
        var end = Math.Round(SnapTime(op, rawEnd, snap), 3);
        Document.SetTiming(op, op.Start, Math.Max(0, end - op.Start));
    }

    /// <summary>
    /// Snaps whichever edge of a bar is being dragged - its start when moving, its end when
    /// resizing. Another bar's end wins when the drag lands within <see cref="BarEndSnapWindow"/>
    /// of it, so operations can be butted together exactly; otherwise the edge goes to the nearest
    /// whole unit. Shift turns snapping off for a free drag.
    /// </summary>
    private double SnapTime(Operation dragged, double raw, bool snap)
    {
        if (!snap || Document == null)
            return raw;

        var cycle = Document.CycleTime;
        var driven = Document.Descendants(dragged);
        var bestDistance = BarEndSnapWindow;
        double? best = null;

        foreach (var other in Document.Operations)
        {
            // Bars this one drags along move with it, so they are not something to snap to.
            if (other.Id == dragged.Id || driven.Contains(other.Id))
                continue;

            // A bar the filter has hidden is not on screen to aim at.
            if (RegionFilter.Count > 0 && !_rowOfOperation.ContainsKey(other.Id))
                continue;

            // On a looping axis the end the user is aiming at may be a whole cycle away from the
            // value stored on the operation, so take whichever repeat sits nearest the pointer.
            var candidate = other.End + Math.Round((raw - other.End) / cycle) * cycle;
            if (candidate < 0)
                continue;

            var distance = Math.Abs(candidate - raw);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        // Away from zero rather than the default to-even, so a bar dragged to x.5 always goes the
        // way the pointer is heading.
        return best ?? Math.Round(raw, MidpointRounding.AwayFromZero);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Surface.ReleaseMouseCapture();

        var dragged = _pressedOperation;
        var wasDragging = _dragging;
        var dropTarget = _dropTarget;

        _pressedOperation = null;
        _pressedLink = null;
        _dragging = false;
        _resizing = false;
        _dropTarget = null;
        _chronologicalFreeze = null;

        if (!wasDragging || dragged == null || Document == null)
        {
            Surface.InvalidateVisual();
            return;
        }

        if (dropTarget != null)
        {
            // Dropping onto a bar is a linking gesture, not a move: put the bar back first so the
            // link takes the separation the two operations already had.
            Document.MoveOperation(dragged, _dragOriginalStart);

            if (Document.CreateLink(dropTarget, dragged) == null)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "That link would feed an operation back into itself.",
                    "Cannot link", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        RaiseEdited();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Document == null || _headerEditor != null)
            return;

        if (e.Key == Key.Delete)
        {
            DeleteSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _selectedOperation = null;
            _selectedLink = null;
            Surface.InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HideTip();
        Surface.Cursor = null;
        if (_hovered != null)
        {
            _hovered = null;
            HoveredRegionChanged?.Invoke(this, null);
            Surface.InvalidateVisual();
        }
    }

    private void DeleteSelection()
    {
        if (Document == null)
            return;

        if (_selectedOperation != null)
        {
            Document.RemoveOperation(_selectedOperation);
            _selectedOperation = null;
            _selectedLink = null;
            RaiseEdited();
        }
        else if (_selectedLink != null)
        {
            Document.RemoveLink(_selectedLink);
            _selectedLink = null;
            RaiseEdited();
        }
    }

    private void HandleDoubleClick(Point position)
    {
        if (Document == null)
            return;

        var bar = HitBar(position);
        if (bar != null)
        {
            var dialog = new BarEditDialog(Document, bar) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true)
            {
                Document.EditTiming(bar, dialog.Start, dialog.Duration);
                RaiseEdited();
            }
            return;
        }

        var link = HitLink(position);
        if (link != null)
        {
            var dialog = new LinkEditDialog(Document, link) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true)
            {
                if (dialog.DeleteRequested)
                    Document.RemoveLink(link);
                else
                    Document.SetLinkLag(link, dialog.Lag);
                RaiseEdited();
            }
            return;
        }

        foreach (var (rect, row) in _headerHits)
        {
            if (rect.Contains(position))
            {
                ShowHeaderEditor(row, rect);
                return;
            }
        }
    }

    // ------------------------------------------------------------ hit testing

    private Operation? HitBar(Point position, Operation? exclude = null)
    {
        if (position.X < HeaderWidth || position.Y < RulerHeight)
            return null;

        for (var i = _barHits.Count - 1; i >= 0; i--)
        {
            var (rect, op) = _barHits[i];
            if (ReferenceEquals(op, exclude))
                continue;
            if (rect.Contains(position))
                return op;
        }
        return null;
    }

    /// <summary>
    /// True when the pointer is on <paramref name="op"/>'s trailing-edge grip. Asking about a
    /// specific bar rather than searching every grip keeps a bar underneath from stealing the
    /// gesture from the one actually on top.
    /// </summary>
    private bool OnResizeGrip(Operation op, Point position)
    {
        foreach (var (rect, candidate) in _resizeHits)
            if (ReferenceEquals(candidate, op) && rect.Contains(position))
                return true;
        return false;
    }

    private OperationLink? HitLink(Point position)
    {
        const double tolerance = 5;
        if (position.X < HeaderWidth || position.Y < RulerHeight)
            return null;

        for (var i = _linkHits.Count - 1; i >= 0; i--)
        {
            var (points, link) = _linkHits[i];
            for (var s = 0; s < points.Length - 1; s++)
                if (DistanceToSegment(position, points[s], points[s + 1]) <= tolerance)
                    return link;
        }
        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-9)
            return (p - a).Length;
        var t = Math.Clamp(((p - a) * ab) / lengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    // -------------------------------------------------------------- tooltips

    private void UpdateHover(Point position)
    {
        if (Document == null)
            return;

        var bar = HitBar(position);
        object? hit = bar;
        hit ??= HitLink(position);

        // Advertise the resize grip before the user commits to a drag.
        Surface.Cursor = bar != null && OnResizeGrip(bar, position) ? Cursors.SizeWE : null;

        if (!ReferenceEquals(hit, _hovered))
        {
            _hovered = hit;
            HoveredRegionChanged?.Invoke(this, bar?.RegionId);
            Surface.InvalidateVisual();
        }

        if (hit == null)
        {
            HideTip();
            return;
        }

        _tip.Content = hit switch
        {
            Operation op => BarTip(op),
            OperationLink link => LinkTip(link),
            _ => null,
        };
        _tip.HorizontalOffset = position.X + 18;
        _tip.VerticalOffset = position.Y + 22;
        _tip.IsOpen = true;
    }

    private string BarTip(Operation op)
    {
        var region = Document?.RegionOf(op)?.Name ?? "(none)";
        return $"{op.Name}\n" +
               $"Robot: {op.RobotName}\n" +
               $"Region: {region}\n" +
               $"Start {TimeMath.Format(op.Start)}   " +
               $"End {TimeMath.Format(op.End)}   " +
               $"Duration {TimeMath.Format(op.Duration)}";
    }

    private string LinkTip(OperationLink link)
    {
        var source = Document?.FindOperation(link.SourceId);
        var target = Document?.FindOperation(link.TargetId);
        return $"Link duration {TimeMath.Format(link.Lag)}\n" +
               $"From: {source?.Name ?? "?"}\n" +
               $"To: {target?.Name ?? "?"}";
    }

    private void HideTip()
    {
        if (_tip.IsOpen)
            _tip.IsOpen = false;
    }

    // -------------------------------------------------------- header editing

    private void ShowHeaderEditor(RowInfo row, Rect rect)
    {
        CommitHeaderEditor();

        _headerEditorRow = row;
        _headerEditor = new TextBox
        {
            Text = row.Title,
            Width = rect.Width - 8,
            Height = 24,
            Padding = new Thickness(2),
        };

        Canvas.SetLeft(_headerEditor, rect.Left + 4);
        Canvas.SetTop(_headerEditor, rect.Top + (rect.Height - 24) / 2);
        Overlay.Children.Add(_headerEditor);

        _headerEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitHeaderEditor();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelHeaderEditor();
                e.Handled = true;
            }
        };
        _headerEditor.LostFocus += (_, _) => CommitHeaderEditor();

        // The box has only just joined the tree; let layout settle before taking focus.
        var editor = _headerEditor;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            editor.Focus();
            editor.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitHeaderEditor()
    {
        var editor = _headerEditor;
        var row = _headerEditorRow;
        if (editor == null || row == null || Document == null)
            return;

        _headerEditor = null;
        _headerEditorRow = null;
        Overlay.Children.Remove(editor);

        var name = editor.Text.Trim();
        if (name.Length == 0 || name == row.Title)
        {
            Surface.InvalidateVisual();
            return;
        }

        switch (row.Kind)
        {
            case RowKind.Robot:
                Document.RenameRobot(row.RobotName, name);
                break;
            case RowKind.Region:
                var region = Document.FindRegion(row.RegionId);
                if (region != null)
                    region.Name = name;
                break;
            case RowKind.Operation:
                if (row.Single != null)
                    row.Single.Name = name;
                break;
        }

        RaiseEdited();
    }

    private void CancelHeaderEditor()
    {
        var editor = _headerEditor;
        _headerEditor = null;
        _headerEditorRow = null;
        if (editor != null)
            Overlay.Children.Remove(editor);
        Surface.InvalidateVisual();
    }

    // --------------------------------------------------------------- helpers

    private FormattedText Text(string value, double emSize, Brush brush, Typeface face) => new(
        value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, emSize, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Pen MakeGhostPen()
    {
        var pen = new Pen(Palette.Brush(Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF)), 1.5)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };
        pen.Freeze();
        return pen;
    }
}
