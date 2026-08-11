using System.Diagnostics;
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

    /// <summary>Narrowest strip of chart worth drawing beside the row headers.</summary>
    private const double MinChartWidth = 40;

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

    /// <summary>
    /// A group's own line: a thin strip when expanded, and when collapsed a band a little shorter
    /// than a row, since it stands in for the rows it hides.
    /// </summary>
    private const double GroupStripHeight = 20;
    private const double CollapsedGroupHeight = 30;
    private const double GroupIndent = 12;
    private const double ChevronBox = 16;

    /// <summary>Fraction of a row's height at each end that means "drop between" rather than "group".</summary>
    private const double ReorderEdgeFraction = 0.3;

    /// <summary>
    /// How far ctrl+wheel may stretch or squash the rows. The floor keeps a row tall enough to still
    /// carry a readable label and a grabbable bar; the ceiling is about as far as anyone needs to go
    /// to pick apart a busy row.
    /// </summary>
    private const double MinRowZoom = 0.5;
    private const double MaxRowZoom = 2.5;

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
    private static readonly Brush GroupBandBrush = Palette.Brush(Palette.GroupBand);
    private static readonly Brush GroupBlockBrush = Palette.Brush(Palette.GroupBlock);
    private static readonly Brush NoRegionBrush = Palette.Brush(Palette.GroupBlock);
    private static readonly Pen GroupEdgePen = Palette.Pen(Palette.GroupEdge, 1);
    private static readonly Pen GroupBlockPen = Palette.Pen(Palette.BarOutline, 1);
    private static readonly Pen DropIndicatorPen = Palette.Pen(Palette.DropIndicator, 3);
    private static readonly Pen DropOntoPen = Palette.Pen(Palette.DropIndicator, 2.5);
    private static readonly Brush DraggedRowBrush =
        Palette.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    /// <summary>Rounded ends on the bars, as in the previous editor.</summary>
    private const double BarCornerRadius = 3;
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly List<RowInfo> _rows = new();
    private readonly List<Line> _lines = new();
    private readonly Dictionary<Guid, int> _rowOfOperation = new();
    private readonly List<(Rect Rect, Operation Operation)> _barHits = new();
    private readonly List<(Rect Rect, Operation Operation)> _resizeHits = new();
    private readonly List<(Point[] Points, OperationLink Link)> _linkHits = new();
    private readonly List<(Rect Rect, Line Line)> _headerHits = new();
    private readonly List<(Rect Rect, RowGroup Group)> _chevronHits = new();
    private readonly HashSet<Guid> _highlightedRegions = new();
    private readonly ToolTip _tip = new()
    {
        Placement = PlacementMode.Relative,
        StaysOpen = true,
        IsHitTestVisible = false,
    };

    private double _zoom = 1;

    /// <summary>Vertical zoom, from ctrl+wheel. Everything measured down the page scales by this.</summary>
    private double _rowZoom = 1;
    private double _scrollX;
    private double _scrollY;
    private double _pxPerUnit = 1;
    private bool _updatingScrollBars;

    private double RowPitch => RowHeight * _rowZoom;
    private double BarMargin => BarInset * _rowZoom;

    /// <summary>Text sized with the rows, floored so a squashed chart is still readable.</summary>
    private double Scaled(double fontSize) => Math.Clamp(fontSize * _rowZoom, 8.5, fontSize * 1.5);

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

    private object? _hovered;
    private TextBox? _headerEditor;
    private Line? _headerEditorLine;

    // Row drag: which header was grabbed, and where releasing would put it.
    private RowRef? _pressedHeader;
    private bool _draggingRow;
    private RowRef? _rowDropOnto;
    private int _rowDropBefore = -1;

    /// <summary>
    /// Names a header in a way that survives the line list being rebuilt, which happens on every
    /// render and so happens repeatedly mid-drag: a row by its key, a group by its id. Holding the
    /// <see cref="Line"/> itself would leave the drag pointing at an object no longer on screen.
    /// </summary>
    private readonly record struct RowRef(string? Key, Guid? GroupId);

    private static RowRef RefOf(Line line) =>
        line.IsGroup ? new RowRef(null, line.Group!.Id) : new RowRef(line.Row!.Key, null);

    private bool Matches(Line line, RowRef reference) => reference.GroupId.HasValue
        ? line.Group?.Id == reference.GroupId
        : line.Row != null && string.Equals(line.Row.Key, reference.Key, StringComparison.OrdinalIgnoreCase);

    private Line? Resolve(RowRef reference) => _lines.FirstOrDefault(l => Matches(l, reference));

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

    /// <summary>
    /// Regions under the pointer, so the host can highlight their borders on the image. A bar names
    /// one; a row header names every region that row's operations are involved with.
    /// </summary>
    public event EventHandler<IReadOnlyCollection<Guid>>? HoveredRegionChanged;

    /// <summary>
    /// Regions to emphasise, driven from outside the chart - hovering a robot's crosshair on the
    /// image names every region that robot works in. Bars in them gain the halo a selected bar has,
    /// without the solid outline, so the two stay tellable apart.
    /// </summary>
    public void SetHighlightedRegions(IReadOnlyCollection<Guid>? regionIds)
    {
        var next = regionIds ?? Array.Empty<Guid>();
        if (_highlightedRegions.Count == next.Count && next.All(_highlightedRegions.Contains))
            return;

        _highlightedRegions.Clear();
        foreach (var id in next)
            _highlightedRegions.Add(id);
        Surface.InvalidateVisual();
    }

    public void SetDocument(GanttDocument? document)
    {
        Document = document;
        _selectedOperation = null;
        _selectedLink = null;
        RegionFilter.Clear();
        _highlightedRegions.Clear();
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

        /// <summary>What the layout orders and groups this row by. See <see cref="RowLayout"/>.</summary>
        public string Key = string.Empty;
    }

    /// <summary>
    /// One horizontal band of the chart. Rows no longer all share a height - a group contributes a
    /// line of its own - so each line carries its own top and height rather than being placed by
    /// multiplying an index.
    /// </summary>
    private sealed class Line
    {
        public double Top;
        public double Height;

        /// <summary>Set on an ordinary row line.</summary>
        public RowInfo? Row;

        /// <summary>Set on a group's own line, collapsed or not.</summary>
        public RowGroup? Group;

        /// <summary>Everything inside a collapsed group, for the occupancy blocks.</summary>
        public List<Operation> Contents = new();

        /// <summary>True for a row drawn inside an expanded group, which sits indented.</summary>
        public bool Nested;

        public bool IsGroup => Group != null;
        public Rect HeaderRect(double top) => new(0, top, HeaderWidth, Height);
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
                        Key = robot,
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
                        Key = GanttDocument.KeyOf(region.Id),
                        Operations = ops.Where(Keep).ToList(),
                        Ghosted = ghosted,
                    });
                }

                // Operations belonging to no region still have to appear somewhere in a view whose
                // rows are regions, so they gather in a row of their own at the end.
                var loose = Document.Operations.Where(o => !o.HasRegion).ToList();
                if (loose.Count > 0 && (!filtering || loose.Any(o => primary.Contains(o.Id))))
                    AddRow(new RowInfo
                    {
                        Title = "(No region)",
                        Kind = RowKind.Region,
                        Key = GanttDocument.NoRegionKey,
                        Operations = loose.Where(Keep).ToList(),
                        Ghosted = ghosted,
                    });
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
                        Key = GanttDocument.KeyOf(op.Id),
                        Operations = new List<Operation> { op },
                        Ghosted = ghosted,
                    });
                }
                break;
        }

        LayOutLines();

        void AddRow(RowInfo row) => _rows.Add(row);
    }

    /// <summary>
    /// Turns the natural row list into the lines actually drawn: applies the manual order, pulls
    /// each group's members together behind a line of their own, and stacks everything up with
    /// explicit tops. <see cref="_rowOfOperation"/> is rebuilt here, pointing an operation inside a
    /// collapsed group at the group's line so links still have somewhere to land.
    /// </summary>
    private void LayOutLines()
    {
        _lines.Clear();
        _rowOfOperation.Clear();
        if (Document == null)
            return;

        var layout = Document.LayoutFor(GroupMode);

        var ordered = _rows
            .Select((row, natural) => (row, natural))
            .OrderBy(x => layout.IndexOf(x.row.Key))
            .ThenBy(x => x.natural)
            .Select(x => x.row)
            .ToList();

        // Tops are in content space, from zero. The ruler offset and scroll are applied at draw
        // time, so laying out does not have to wait for the scroll to be clamped against a height
        // that this very pass is what produces.
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var y = 0.0;

        foreach (var row in ordered)
        {
            if (!emitted.Add(row.Key))
                continue;

            var group = layout.GroupOf(row.Key);
            if (group == null)
            {
                Add(new Line { Row = row, Height = RowPitch });
                continue;
            }

            // Members may have drifted apart in the order; the group gathers them back together.
            var members = ordered.Where(r => ContainsKey(group, r.Key)).ToList();
            foreach (var member in members)
                emitted.Add(member.Key);

            var groupLine = new Line
            {
                Group = group,
                Height = (group.Collapsed ? CollapsedGroupHeight : GroupStripHeight) * _rowZoom,
                Contents = members.SelectMany(m => m.Operations).ToList(),
            };
            Add(groupLine);

            if (group.Collapsed)
            {
                var index = _lines.Count - 1;
                foreach (var op in groupLine.Contents)
                    _rowOfOperation[op.Id] = index;
                continue;
            }

            foreach (var member in members)
                Add(new Line { Row = member, Height = RowPitch, Nested = true });
        }

        void Add(Line line)
        {
            line.Top = y;
            y += line.Height;
            var index = _lines.Count;
            _lines.Add(line);
            if (line.Row != null)
                foreach (var op in line.Row.Operations)
                    _rowOfOperation[op.Id] = index;
        }
    }

    private static bool ContainsKey(RowGroup group, string key) =>
        group.Members.Contains(key, StringComparer.OrdinalIgnoreCase);

    private double ContentHeight => _lines.Count == 0 ? 0 : _lines[^1].Top + _lines[^1].Height;

    /// <summary>Screen-space top of a line, once the ruler and the scroll are taken into account.</summary>
    private double ScreenTop(Line line) => RulerHeight + line.Top - _scrollY;

    /// <summary>
    /// The order the "Sort by time" button produces, and the order rows fall into before anything
    /// has been dragged. It is not applied as bars move: rows only re-sort when asked to, so a bar
    /// dragged along the axis does not pull its row out from under the pointer.
    /// </summary>
    private IEnumerable<Operation> OrderChronologically()
    {
        if (Document == null)
            return Array.Empty<Operation>();

        return Document.Operations
            .OrderBy(o => o.Start)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Re-sorts the chronological view's rows by start time, writing the result into the manual
    /// order the view otherwise keeps. Rows inside a group still gather together where the first of
    /// them lands, so sorting cannot tear a group apart.
    /// </summary>
    public void SortChronologically()
    {
        if (Document == null || GroupMode != ChartGroupMode.Chronological)
            return;

        Document.LayoutFor(GroupMode)
            .SetOrder(OrderChronologically().Select(o => GanttDocument.KeyOf(o.Id)));
        RaiseEdited();
    }

    private string ColorKeyOf(Operation op) => EffectiveColorBy == ChartColorBy.Robot
        ? op.RobotName
        : Document?.RegionOf(op)?.ColorKey ?? string.Empty;

    private ColorAllocator Allocator => EffectiveColorBy == ChartColorBy.Robot ? RobotColors : RegionColors;

    /// <summary>
    /// A bar's fill. Colouring by region leaves an operation that has no region without a key to
    /// allocate against, so it takes the neutral fill rather than borrowing some region's colour.
    /// </summary>
    private Brush FillOf(Operation op) =>
        EffectiveColorBy == ChartColorBy.Region && !op.HasRegion
            ? NoRegionBrush
            : Allocator.GetBrush(ColorKeyOf(op));

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
        _chevronHits.Clear();

        dc.DrawRectangle(SurfaceBrush, null, new Rect(size));

        // Everything below assumes there is room for the header column plus a usable strip of
        // chart beside it. Without that check the clip and ruler rects get a negative width, which
        // throws out of OnRender and takes the app down with it.
        if (Document == null || size.Width < HeaderWidth + MinChartWidth || size.Height < 40)
        {
            HScroll.Visibility = Visibility.Collapsed;
            VScroll.Visibility = Visibility.Collapsed;
            return;
        }

        BuildRows();

        var cycle = Math.Max(GanttDocument.MinCycleTime, Document.CycleTime);
        var viewportW = Math.Max(10, size.Width - HeaderWidth);
        var contentW = viewportW * _zoom;
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, contentW - viewportW));
        _pxPerUnit = contentW / cycle;

        var viewportH = Math.Max(10, size.Height - RulerHeight);
        var totalH = ContentHeight;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, totalH - viewportH));

        double X(double t) => HeaderWidth + t * _pxPerUnit - _scrollX;

        var chartRect = new Rect(HeaderWidth, RulerHeight, size.Width - HeaderWidth, size.Height - RulerHeight);
        var step = TickStep();

        DrawRowBands(dc, size);
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

        DrawGhost(dc, X, cycle);

        foreach (var line in _lines)
        {
            var top = ScreenTop(line);
            if (top > size.Height || top + line.Height < RulerHeight)
                continue;

            if (line.IsGroup)
            {
                if (line.Group!.Collapsed)
                    DrawGroupOccupancy(dc, line, top, X, cycle);
                continue;
            }

            foreach (var op in line.Row!.Operations)
                DrawBar(dc, op, top, line.Height,
                    line.Row.Ghosted.Contains(op.Id) && RegionFilter.Count > 0, X, cycle);
        }

        DrawLinks(dc, X, cycle);

        dc.Pop();

        DrawHeaders(dc, size);
        // Over everything, including the header column the row is being dragged in.
        DrawRowDropIndicator(dc, size);
        UpdateScrollBars(contentW, viewportW, totalH, viewportH);

        if (_lines.Count == 0)
        {
            var hint = Text(Document.Operations.Count == 0
                ? "No operations yet - use \"Add operation\" to place the first one."
                : "Nothing matches the current region filter.", 13, MutedBrush, Face);
            dc.DrawText(hint, new Point(HeaderWidth + 20, RulerHeight + 20));
        }
    }

    private void DrawRowBands(DrawingContext dc, Size size)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var top = ScreenTop(line);
            if (top > size.Height || top + line.Height < RulerHeight)
                continue;

            // Alternating bands run the full width, header column included. A group's own line
            // takes a flat colour instead, so the block it heads reads as one thing.
            var brush = line.IsGroup ? GroupBandBrush : RowBand(i);
            dc.DrawRectangle(brush, null, new Rect(0, top, size.Width, line.Height));

            if (line.IsGroup)
                dc.DrawLine(GroupEdgePen, new Point(0, top + 0.5), new Point(size.Width, top + 0.5));

            if (IsBeingDragged(line))
                dc.DrawRectangle(DraggedRowBrush, null, new Rect(0, top, size.Width, line.Height));
        }
    }

    private static Brush RowBand(int index) => index % 2 == 0 ? RowBrushA : RowBrushB;

    /// <summary>True for the line the user has picked up, and for the members it takes with it.</summary>
    private bool IsBeingDragged(Line line)
    {
        if (!_draggingRow || _pressedHeader is not { } dragged)
            return false;
        if (Matches(line, dragged))
            return true;

        // A group travels with its rows.
        if (dragged.GroupId is not { } id || line.Row == null)
            return false;
        var group = Document?.LayoutFor(GroupMode).FindGroup(id);
        return group != null && ContainsKey(group, line.Row.Key);
    }

    /// <summary>
    /// While a row is in flight: a bright rule where it would land, or an outline round the row it
    /// would join into a group.
    /// </summary>
    private void DrawRowDropIndicator(DrawingContext dc, Size size)
    {
        if (!_draggingRow)
            return;

        if (_rowDropOnto is { } onto && Resolve(onto) is { } target)
        {
            var top = ScreenTop(target);
            dc.DrawRoundedRectangle(null, DropOntoPen,
                new Rect(1.5, top + 1.5, size.Width - 3, target.Height - 3), 3, 3);
            return;
        }

        if (_rowDropBefore < 0)
            return;

        var y = _rowDropBefore < _lines.Count
            ? ScreenTop(_lines[_rowDropBefore])
            : ScreenTop(_lines[^1]) + _lines[^1].Height;
        dc.DrawLine(DropIndicatorPen, new Point(0, y), new Point(size.Width, y));
    }

    /// <summary>
    /// A collapsed group stands in for every row it hides, so it blocks in the union of their
    /// occupied times. The fill is deliberately neutral: the block covers several regions or robots
    /// at once and any one of their colours would claim it for that one.
    /// </summary>
    private void DrawGroupOccupancy(DrawingContext dc, Line line, double top, Func<double, double> x,
        double cycle)
    {
        var bands = TimeMath.Merge(line.Contents
            .SelectMany(op => TimeMath.Bands(op.Start, op.Duration, cycle)));

        var inset = Math.Min(BarMargin, line.Height / 3);
        var height = line.Height - inset * 2 + 2;
        foreach (var band in bands)
        {
            var left = x(band.From);
            var width = Math.Max(2, x(band.To) - left);
            dc.DrawRoundedRectangle(GroupBlockBrush, GroupBlockPen,
                new Rect(left, top + inset - 1, width, Math.Max(2, height)),
                BarCornerRadius, BarCornerRadius);
        }
    }

    /// <summary>
    /// A group's header: a chevron that toggles it, then the name. The name is what a double-click
    /// renames, the same gesture that renames an ordinary row.
    /// </summary>
    private void DrawGroupHeader(DrawingContext dc, Line line, Rect rect)
    {
        var group = line.Group!;
        dc.DrawRectangle(GroupBandBrush, null, rect);
        if (IsBeingDragged(line))
            dc.DrawRectangle(DraggedRowBrush, null, rect);

        // The chevron cannot outgrow the strip it sits in, which is what limits it when the rows
        // are zoomed right down.
        var box = Math.Min(ChevronBox, rect.Height - 2);
        var chevron = new Rect(4, rect.Top + (rect.Height - box) / 2, box, box);
        _chevronHits.Add((chevron, group));
        DrawChevron(dc, chevron, group.Collapsed);

        var count = group.Members.Count;
        var text = Text($"{group.Name}  ({count})", Scaled(11.5), TextBrush, BoldFace);
        text.MaxTextWidth = Math.Max(20, HeaderWidth - chevron.Right - 10);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(text, new Point(chevron.Right + 4, rect.Top + (rect.Height - text.Height) / 2));
    }

    /// <summary>Points right when the group is collapsed, down when it is open.</summary>
    private static void DrawChevron(DrawingContext dc, Rect box, bool collapsed)
    {
        var cx = box.Left + box.Width / 2;
        var cy = box.Top + box.Height / 2;
        var r = box.Width * 0.225;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (collapsed)
            {
                ctx.BeginFigure(new Point(cx - r * 0.7, cy - r), true, true);
                ctx.LineTo(new Point(cx + r * 0.8, cy), true, false);
                ctx.LineTo(new Point(cx - r * 0.7, cy + r), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(cx - r, cy - r * 0.7), true, true);
                ctx.LineTo(new Point(cx + r, cy - r * 0.7), true, false);
                ctx.LineTo(new Point(cx, cy + r * 0.8), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(TextBrush, null, geometry);
    }

    private void DrawHeaders(DrawingContext dc, Size size)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, HeaderWidth, size.Height - RulerHeight)));
        dc.DrawRectangle(SurfaceBrush, null, new Rect(0, RulerHeight, HeaderWidth, size.Height - RulerHeight));

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var top = ScreenTop(line);
            if (top > size.Height || top + line.Height < RulerHeight)
                continue;

            var rect = new Rect(0, top, HeaderWidth, line.Height);
            _headerHits.Add((rect, line));

            if (line.IsGroup)
            {
                DrawGroupHeader(dc, line, rect);
                continue;
            }

            dc.DrawRectangle(RowBand(i), null, rect);
            if (IsBeingDragged(line))
                dc.DrawRectangle(DraggedRowBrush, null, rect);

            var row = line.Row!;
            var indent = line.Nested ? GroupIndent : 0;

            // In "group by robot" the header is a region, so it carries the region's colour.
            if (row.Kind == RowKind.Region)
            {
                var region = Document?.FindRegion(row.RegionId);
                if (region != null)
                    dc.DrawRoundedRectangle(RegionColors.GetBrush(region.ColorKey), null,
                        new Rect(indent, top + 5, 5, line.Height - 10), 2, 2);
            }

            // A gutter down the left marks how far the group above it reaches.
            if (line.Nested)
                dc.DrawRectangle(GroupBandBrush, null, new Rect(0, top, GroupIndent - 4, line.Height));

            var text = Text(row.Title, Scaled(12.5), TextBrush, BoldFace);
            text.MaxTextWidth = Math.Max(20, HeaderWidth - 16 - indent);
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(text, new Point(10 + indent, top + (line.Height - text.Height) / 2));
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

    private void DrawGhost(DrawingContext dc, Func<double, double> x, double cycle)
    {
        if (!_dragging || _pressedOperation == null)
            return;
        if (!_rowOfOperation.TryGetValue(_pressedOperation.Id, out var lineIndex))
            return;

        var line = _lines[lineIndex];
        var top = ScreenTop(line);
        foreach (var band in TimeMath.Bands(_dragOriginalStart, _dragOriginalDuration, cycle))
        {
            var rect = BandRect(band, top, line.Height, x);
            if (rect.Width <= 0)
                continue;
            dc.DrawRoundedRectangle(null, GhostPen, rect, BarCornerRadius, BarCornerRadius);
        }
    }

    /// <summary>
    /// A bar's rectangle inside its line. The height comes from the line rather than the constant,
    /// so a bar tracks the vertical zoom - and stays inside a group's own shorter line.
    /// </summary>
    private Rect BandRect((double From, double To) band, double top, double lineHeight,
        Func<double, double> x)
    {
        var left = x(band.From);
        var right = x(band.To);
        var inset = Math.Min(BarMargin, lineHeight / 3);
        return new Rect(left, top + inset, Math.Max(2, right - left), Math.Max(2, lineHeight - inset * 2));
    }

    private void DrawBar(DrawingContext dc, Operation op, double top, double lineHeight, bool ghosted,
        Func<double, double> x, double cycle)
    {
        var fill = FillOf(op);
        var bands = TimeMath.Bands(op.Start, op.Duration, cycle);
        var selected = ReferenceEquals(op, _selectedOperation);
        var isDropTarget = ReferenceEquals(op, _dropTarget);
        var highlighted = _highlightedRegions.Contains(op.RegionId);

        if (ghosted)
            dc.PushOpacity(Palette.DimmedOpacity);

        Rect widest = Rect.Empty;
        Rect last = Rect.Empty;
        foreach (var band in bands)
        {
            var rect = BandRect(band, top, lineHeight, x);
            _barHits.Add((rect, op));
            last = rect;

            // A selected bar sits inside a white halo, standing in for the old drop shadow. A bar
            // highlighted from outside takes the same halo without the outline that follows it.
            if (selected || highlighted)
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
        var text = Text(LabelOf(op), Scaled(11.5), Brushes.Black, BoldFace);
        text.MaxTextWidth = Math.Max(8, rect.Width - 8);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;

        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawText(text, new Point(rect.Left + 5, rect.Top + (rect.Height - text.Height) / 2));
        dc.Pop();
    }

    private void DrawLinks(DrawingContext dc, Func<double, double> x, double cycle)
    {
        if (Document == null)
            return;

        foreach (var link in Document.Links)
        {
            var source = Document.FindOperation(link.SourceId);
            var target = Document.FindOperation(link.TargetId);
            if (source == null || target == null)
                continue;
            // An operation inside a collapsed group maps to the group's line, so the arrow lands on
            // the block standing in for it rather than vanishing.
            if (!_rowOfOperation.TryGetValue(source.Id, out var sourceRow) ||
                !_rowOfOperation.TryGetValue(target.Id, out var targetRow))
                continue;

            var sourceBands = TimeMath.Bands(source.Start, source.Duration, cycle);
            var targetBands = TimeMath.Bands(target.Start, target.Duration, cycle);
            if (sourceBands.Count == 0 || targetBands.Count == 0)
                continue;

            var sourceLine = _lines[sourceRow];
            var targetLine = _lines[targetRow];
            var from = new Point(x(sourceBands[^1].To), ScreenTop(sourceLine) + sourceLine.Height / 2);
            var to = new Point(x(targetBands[0].From), ScreenTop(targetLine) + targetLine.Height / 2);

            var points = Route(from, to);
            _linkHits.Add((points, link));

            var selected = ReferenceEquals(link, _selectedLink);
            var pen = selected ? SelectedLinkPen : LinkPen;
            var head = selected ? Brushes.White : LinkBrush;

            // Fade the arrow to match when either end is only on screen because of the filter.
            var dimmed = RegionFilter.Count > 0 &&
                         ((sourceLine.Row?.Ghosted.Contains(source.Id) ?? false) ||
                          (targetLine.Row?.Ghosted.Contains(target.Id) ?? false));
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
    private Point[] Route(Point from, Point to)
    {
        if (to.X > from.X + LinkElbow * 2)
        {
            var mid = from.X + LinkElbow;
            return new[] { from, new Point(mid, from.Y), new Point(mid, to.Y), to };
        }

        // Target sits left of the source (usually because one of them wrapped): detour around.
        var midY = (from.Y + to.Y) / 2 + (Math.Abs(to.Y - from.Y) < 1 ? RowPitch / 2 - 2 : 0);
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
            VScroll.SmallChange = RowPitch;
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

        // Ctrl+wheel stretches or squashes the rows, pinned to the row under the cursor so the
        // thing being looked at stays put. Offered over the headers too, since that is as much
        // "the chart" as the bars are.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ZoomRows(e.Delta > 0 ? 1.15 : 1 / 1.15, position.Y);
            e.Handled = true;
            return;
        }

        if (position.X < HeaderWidth)
        {
            _scrollY = Math.Max(0, _scrollY - Math.Sign(e.Delta) * RowPitch);
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

    /// <summary>
    /// One step of vertical zoom, keeping whatever sits at <paramref name="pointerY"/> under the
    /// pointer. Scrolling by the same factor as the rows grow is what pins it: content space
    /// stretches about its own origin, so the offset has to stretch with it.
    /// </summary>
    private void ZoomRows(double factor, double pointerY)
    {
        var previous = _rowZoom;
        _rowZoom = Math.Clamp(_rowZoom * factor, MinRowZoom, MaxRowZoom);
        if (Math.Abs(_rowZoom - previous) < 1e-9)
            return;

        var above = pointerY - RulerHeight;
        _scrollY = Math.Max(0, (_scrollY + above) * (_rowZoom / previous) - above);
        Surface.InvalidateVisual();
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

        // The chevron is a button, not the start of a drag.
        if (ToggleChevronAt(position))
        {
            e.Handled = true;
            return;
        }

        var header = position.X < HeaderWidth ? HitHeader(position) : null;
        _pressedHeader = header == null ? null : RefOf(header);
        if (_pressedHeader != null)
        {
            _pressPoint = position;
            Surface.CaptureMouse();
            HideTip();
            Surface.InvalidateVisual();
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

        if (_pressedHeader != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_draggingRow && (position - _pressPoint).Length < DragSlop)
                return;

            _draggingRow = true;
            HideTip();
            UpdateRowDropTarget(position);
            Surface.InvalidateVisual();
            return;
        }

        if (_pressedOperation != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && (position - _pressPoint).Length < DragSlop)
                return;

            if (!_dragging)
            {
                _dragging = true;
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

    // ------------------------------------------------------------- row drags

    /// <summary>
    /// Works out what releasing here would mean: near a row's middle, joining it into a group;
    /// near either end, dropping between rows.
    /// </summary>
    private void UpdateRowDropTarget(Point position)
    {
        _rowDropOnto = null;
        _rowDropBefore = -1;

        var target = HitHeader(position);

        if (target == null)
        {
            // Past the end of the list: drop at the bottom.
            if (_lines.Count > 0 && position.Y > ScreenTop(_lines[^1]) + _lines[^1].Height)
                _rowDropBefore = _lines.Count;
            Surface.InvalidateVisual();
            return;
        }

        if (IsBeingDragged(target))
            return;

        var top = ScreenTop(target);
        var edge = target.Height * ReorderEdgeFraction;
        var index = _lines.IndexOf(target);

        if (position.Y < top + edge)
            _rowDropBefore = index;
        else if (position.Y > top + target.Height - edge)
            _rowDropBefore = index + 1;
        else
            _rowDropOnto = RefOf(target);
    }

    /// <summary>Applies a released row drag: either a reorder, or a grouping.</summary>
    private void DropRow(RowRef source, RowRef? ontoRef, int before)
    {
        if (Document == null)
        {
            Surface.InvalidateVisual();
            return;
        }

        var layout = Document.LayoutFor(GroupMode);

        if (ontoRef is { } reference && Resolve(reference) is { } onto)
        {
            GroupRows(layout, source, onto);
            RaiseEdited();
            return;
        }

        if (before < 0)
        {
            Surface.InvalidateVisual();
            return;
        }

        ReorderRow(layout, source, before);
        RaiseEdited();
    }

    /// <summary>
    /// Puts the dragged row and the row it landed on into one group - joining the target's group
    /// when it already has one, so dropping onto a group adds to it rather than nesting.
    /// </summary>
    private void GroupRows(RowLayout layout, RowRef source, Line onto)
    {
        var moving = KeysOf(source);
        if (moving.Count == 0)
            return;

        var target = onto.Group ?? (onto.Row == null ? null : layout.GroupOf(onto.Row.Key));
        if (target == null)
        {
            target = new RowGroup { Name = SuggestGroupName(onto, Resolve(source)) };
            layout.Groups.Add(target);
            layout.AddToGroup(target, onto.Row!.Key);
        }

        foreach (var key in moving)
        {
            if (ContainsKey(target, key))
                continue;
            layout.AddToGroup(target, key);
        }

        // Members sit together on screen, so put them together in the order too - after the last
        // one already there, so a row added to a group joins the end of it.
        var order = DisplayedKeys();
        order.RemoveAll(k => moving.Contains(k, StringComparer.OrdinalIgnoreCase));
        var anchor = order.FindLastIndex(k => ContainsKey(target, k));
        order.InsertRange(anchor < 0 ? order.Count : anchor + 1, moving);
        layout.SetOrder(order);
    }

    private void ReorderRow(RowLayout layout, RowRef source, int before)
    {
        var moving = KeysOf(source);
        if (moving.Count == 0)
            return;

        // The insertion point is a line index; turn it into a position among row keys.
        var order = DisplayedKeys();
        var anchorKey = before < _lines.Count ? FirstKeyFrom(before) : null;

        foreach (var key in moving)
            layout.LeaveGroup(key);

        order.RemoveAll(k => moving.Contains(k, StringComparer.OrdinalIgnoreCase));
        var at = anchorKey == null
            ? order.Count
            : order.FindIndex(k => string.Equals(k, anchorKey, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            at = order.Count;

        order.InsertRange(at, moving);
        layout.SetOrder(order);
    }

    /// <summary>Row keys a header carries: one for a row, all its members for a group.</summary>
    private List<string> KeysOf(RowRef reference)
    {
        if (reference.GroupId is not { } id)
            return reference.Key == null ? new List<string>() : new List<string> { reference.Key };

        var group = Document?.LayoutFor(GroupMode).FindGroup(id);
        return group == null
            ? new List<string>()
            : DisplayedKeys().Where(k => ContainsKey(group, k)).ToList();
    }

    private List<string> DisplayedKeys() =>
        _lines.Where(l => l.Row != null).Select(l => l.Row!.Key).ToList();

    /// <summary>First row key at or after a line index, which is where an insertion lands.</summary>
    private string? FirstKeyFrom(int lineIndex)
    {
        for (var i = lineIndex; i < _lines.Count; i++)
            if (_lines[i].Row != null)
                return _lines[i].Row!.Key;
        return null;
    }

    private static string SuggestGroupName(Line a, Line? b)
    {
        var first = a.Row?.Title ?? a.Group?.Name;
        var second = b?.Row?.Title ?? b?.Group?.Name;
        return string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)
            ? "Group"
            : $"{first} + {second}";
    }

    /// <summary>Flips a group open or shut if the click landed on its chevron.</summary>
    private bool ToggleChevronAt(Point position)
    {
        foreach (var (rect, group) in _chevronHits)
        {
            if (!rect.Contains(position))
                continue;
            group.Collapsed = !group.Collapsed;
            RaiseEdited();
            return true;
        }
        return false;
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

        if (_pressedHeader is { } source)
        {
            var wasRowDrag = _draggingRow;
            var onto = _rowDropOnto;
            var before = _rowDropBefore;

            _pressedHeader = null;
            _draggingRow = false;
            _rowDropOnto = null;
            _rowDropBefore = -1;

            if (wasRowDrag)
                DropRow(source, onto, before);
            else
                Surface.InvalidateVisual();
            return;
        }

        var dragged = _pressedOperation;
        var wasDragging = _dragging;
        var dropTarget = _dropTarget;

        _pressedOperation = null;
        _pressedLink = null;
        _dragging = false;
        _resizing = false;
        _dropTarget = null;

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
            HoveredRegionChanged?.Invoke(this, Array.Empty<Guid>());
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

        // The chevron already toggled on the first of the two clicks; do not also rename.
        if (_chevronHits.Any(c => c.Rect.Contains(position)))
            return;

        foreach (var (rect, line) in _headerHits)
        {
            if (rect.Contains(position))
            {
                ShowHeaderEditor(line, rect);
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

    private Line? HitHeader(Point position)
    {
        foreach (var (rect, line) in _headerHits)
            if (rect.Contains(position))
                return line;
        return null;
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
        var header = bar == null && position.X < HeaderWidth ? HitHeader(position) : null;
        object? hit = bar;
        hit ??= header;
        hit ??= HitLink(position);

        // Advertise the resize grip before the user commits to a drag.
        Surface.Cursor = bar != null && OnResizeGrip(bar, position) ? Cursors.SizeWE
            //: header != null ? Cursors.SizeAll
            : null;

        if (!ReferenceEquals(hit, _hovered))
        {
            _hovered = hit;
            HoveredRegionChanged?.Invoke(this, RegionsOf(hit));
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
            Line line => HeaderTip(line),
            _ => null,
        };
        _tip.HorizontalOffset = position.X + 18;
        _tip.VerticalOffset = position.Y + 22;
        _tip.IsOpen = true;
    }

    /// <summary>
    /// Every region the hovered thing touches: one for a bar, all of a row's for a row header, and
    /// everything inside a group for a group header. The host lights these up on the image.
    /// </summary>
    private IReadOnlyCollection<Guid> RegionsOf(object? hit) => hit switch
    {
        // An operation with no region names none, rather than naming the empty id.
        Operation op => op.HasRegion ? new[] { op.RegionId } : Array.Empty<Guid>(),
        Line line => OperationsOf(line).Where(o => o.HasRegion).Select(o => o.RegionId).Distinct().ToArray(),
        _ => Array.Empty<Guid>(),
    };

    private IEnumerable<Operation> OperationsOf(Line line) =>
        line.Row?.Operations ?? (IEnumerable<Operation>)line.Contents;

    private string HeaderTip(Line line)
    {
        if (line.IsGroup)
        {
            var names = line.Group!.Members.Count;
            return $"{line.Group.Name}\n" +
                   $"{names} rows, {line.Contents.Count} operations\n" +
                   (line.Group.Collapsed ? "Click the arrow to expand" : "Click the arrow to collapse");
        }

        var ops = line.Row!.Operations;
        var regions = ops.Select(o => Document?.RegionOf(o)?.Name ?? "(none)").Distinct().ToList();
        return $"{line.Row.Title}\n" +
               $"{ops.Count} operations\n" +
               $"Regions: {string.Join(", ", regions)}";
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

    private void ShowHeaderEditor(Line line, Rect rect)
    {
        CommitHeaderEditor();

        // The box has to fit the line it sits on, which the vertical zoom can make short.
        var height = Math.Min(24, Math.Max(14, rect.Height - 2));

        _headerEditorLine = line;
        _headerEditor = new TextBox
        {
            Text = line.Group?.Name ?? line.Row?.Title ?? string.Empty,
            Width = rect.Width - 8,
            Height = height,
            Padding = new Thickness(2),
        };

        Canvas.SetLeft(_headerEditor, rect.Left + 4);
        Canvas.SetTop(_headerEditor, rect.Top + (rect.Height - height) / 2);
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
        var line = _headerEditorLine;
        if (editor == null || line == null || Document == null)
            return;

        _headerEditor = null;
        _headerEditorLine = null;
        Overlay.Children.Remove(editor);

        var name = editor.Text.Trim();
        if (name.Length == 0)
        {
            Surface.InvalidateVisual();
            return;
        }

        if (line.Group != null)
        {
            if (name != line.Group.Name)
            {
                line.Group.Name = name;
                RaiseEdited();
            }
            else
            {
                Surface.InvalidateVisual();
            }
            return;
        }

        var row = line.Row!;
        if (name == row.Title)
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
        _headerEditorLine = null;
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
