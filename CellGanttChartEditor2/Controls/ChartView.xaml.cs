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
    private const double LinkElbow = 7;

    /// <summary>
    /// How far in from a bar's leading edge a link coming from another row lands, so the head sits
    /// just inside the corner rather than straddling it, and how far out from that edge the arrow
    /// turns to come at it. The turn is kept close to the target so the run across sits in the gap
    /// beside it rather than through whatever rows lie between.
    /// </summary>
    private const double LinkEdgeInset = 7;
    private const double LinkApproach = 8;

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
    private static readonly Brush BannerBrush = Palette.Brush(Palette.Banner);
    private static readonly Pen GridPen = Palette.Pen(Palette.Grid, 1);
    private static readonly Pen DividerPen = Palette.Pen(Palette.Grid, 1.5);
    private static readonly Pen CycleEdgePen = Palette.Pen(Palette.CycleEdge, 1.5);
    private static readonly Pen BarOutlinePen = Palette.Pen(Palette.BarOutline, 1);
    private static readonly Pen SelectionPen = Palette.Pen(Colors.White, 2.5);
    private static readonly Pen[] SelectionGlowPens = Palette.GlowPens(1);

    /// <summary>
    /// The same glow with its corners rounded off, for stroking round an arrowhead. A mitred join at
    /// a point that sharp throws a spike several times the length of the head.
    /// </summary>
    private static readonly Pen[] HeadGlowPens = SelectionGlowPens.Select(RoundJoined).ToArray();
    private static readonly Pen DropTargetPen = Palette.Pen(Color.FromRgb(0x3D, 0xDC, 0x84), 3);
    private static readonly Pen OverlapPen = Palette.Pen(Colors.Black, 2);
    private static readonly Pen LinkPen = Palette.Pen(Palette.Link, 1.3);
    private static readonly Pen SelectedLinkPen = Palette.Pen(Colors.White, 2.4);
    private static readonly Brush LinkBrush = Palette.Brush(Palette.Link);
    private static readonly Pen GhostPen = MakeGhostPen();
    private static readonly Brush GroupBandBrush = Palette.Brush(Palette.GroupBand);
    private static readonly Brush GroupBlockBrush = Palette.Brush(Palette.GroupBlock);
    /// <summary>Fill for a bar the current colour source has no key for - no region, or no robot.</summary>
    private static readonly Brush UnassignedBrush = Palette.Brush(Palette.GroupBlock);
    private static readonly Brush UnassignedTextBrush = ColorAllocator.TextOn(Palette.GroupBlock);
    private static readonly Pen GroupEdgePen = Palette.Pen(Palette.GroupEdge, 1);
    private static readonly Pen GroupBlockPen = Palette.Pen(Palette.BarOutline, 1);
    private static readonly Pen DropIndicatorPen = Palette.Pen(Palette.DropIndicator, 3);
    private static readonly Pen DropOntoPen = Palette.Pen(Palette.DropIndicator, 2.5);
    private static readonly Brush DraggedRowBrush = Palette.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    /// <summary>Rounded ends on the bars, as in the previous editor.</summary>
    private const double BarCornerRadius = 3;
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly List<RowInfo> _rows = new();
    private readonly List<Line> _lines = new();
    private readonly Dictionary<Guid, int> _rowOfOperation = new();
    private readonly List<(Rect Rect, Operation Operation)> _barHits = new();
    private readonly List<(Rect Rect, Operation Operation)> _resizeHits = new();
    private readonly List<(Point[] Points, Rect Head, OperationLink Link)> _linkHits = new();
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

    /// <summary>Middle-button pan, and where the pointer was when it was last acted on.</summary>
    private bool _panning;
    private Point _panPoint;

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

    // Bar pick: the chart standing in for a combo box while a dialog waits for the user to point at
    // an operation. Null when no pick is running.
    private TaskCompletionSource<Operation?>? _barPick;
    private string _barPickHint = string.Empty;
    private Operation? _barPickExclude;
    private bool _editingBar;

    private object? _hovered;
    private TextBox? _headerEditor;
    private Viewbox? _headerEditorBox;
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

    public ColorAllocator CategoryColors { get; set; } = new();

    public ChartGroupMode GroupMode { get; private set; } = ChartGroupMode.Region;

    public ChartColorBy ColorMode { get; private set; } = ChartColorBy.Region;

    /// <summary>Regions to keep, used while the colouring is by region. Empty means no filtering.</summary>
    public HashSet<Guid> RegionFilter { get; } = new();

    /// <summary>Robots to keep, used while the colouring is by robot. Empty means no filtering.</summary>
    public HashSet<string> RobotFilter { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Categories to keep, used while the colouring is by category.</summary>
    public HashSet<string> CategoryFilter { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The filter follows whatever the bars are coloured by, so the chips the user picks from are
    /// always the things the colours are telling them apart. The other set is left alone rather than
    /// consulted - switching the colour source clears both, so only one is ever non-empty.
    /// </summary>
    public bool Filtering => EffectiveColorBy switch
    {
        ChartColorBy.Category => CategoryFilter.Count > 0,
        ChartColorBy.Robot => RobotFilter.Count > 0,
        _ => RegionFilter.Count > 0,
    };

    private bool InFilter(Operation op) => EffectiveColorBy switch
    {
        ChartColorBy.Category => CategoryFilter.Contains(op.Category),
        ChartColorBy.Robot => op.HasRobot && RobotFilter.Contains(op.RobotName),
        _ => RegionFilter.Contains(op.RegionId),
    };

    /// <summary>True while the chart is waiting for the user to click an operation.</summary>
    public bool IsPickingBar => _barPick != null;

    /// <summary>
    /// Asks the user to click a bar, the chart's answer to picking something from a list. The one
    /// asking is named in the banner, and is itself excluded from what can be clicked. Resolves to
    /// null if the pick is cancelled with Esc or a click on empty space.
    /// </summary>
    public Task<Operation?> PickOperationAsync(Operation asking)
    {
        CancelPick();
        _barPickExclude = asking;
        _barPickHint = $"Click the operation \"{asking.Name}\" runs alongside. Esc to cancel.";
        _barPick = new TaskCompletionSource<Operation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Surface.Cursor = Cursors.Cross;
        Focus();
        Surface.InvalidateVisual();
        return _barPick.Task;
    }

    /// <summary>Cancels a bar pick, if one is running.</summary>
    public void CancelPick() => CompleteBarPick(null);

    private void CompleteBarPick(Operation? picked)
    {
        var pick = _barPick;
        _barPick = null;
        _barPickExclude = null;
        Surface.Cursor = null;
        Surface.InvalidateVisual();
        pick?.TrySetResult(picked);
    }

    /// <summary>Drops every filter, whichever kind is in use.</summary>
    public void ClearFilters()
    {
        RegionFilter.Clear();
        RobotFilter.Clear();
        CategoryFilter.Clear();
    }

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

    /// <summary>
    /// Drops the selection without touching the zoom or the scroll, for when the document changed
    /// underneath the chart and the selected bar may no longer be in it.
    /// </summary>
    public void ClearSelection()
    {
        _selectedOperation = null;
        _selectedLink = null;
        Surface.InvalidateVisual();
    }

    public void SetDocument(GanttDocument? document)
    {
        Document = document;
        _selectedOperation = null;
        _selectedLink = null;
        // A pick left running against the old document would never be answered, and whatever is
        // waiting on it would wait for good.
        CancelPick();
        if (_panning)
            EndPan();
        ClearFilters();
        _highlightedRegions.Clear();
        _zoom = 1;
        _scrollX = 0;
        _scrollY = 0;
        Refresh();
    }

    public void SetGroupMode(ChartGroupMode mode)
    {
        var was = EffectiveColorBy;
        GroupMode = mode;
        DropFilterIfColorSourceChanged(was);
        Refresh();
    }

    public void SetColorMode(ChartColorBy mode)
    {
        var was = EffectiveColorBy;
        ColorMode = mode;
        DropFilterIfColorSourceChanged(was);
        Refresh();
    }

    /// <summary>
    /// The chips are the things the bars are coloured by, so a change of colour source replaces the
    /// whole set of them. Anything the user had picked names something no longer being offered, so
    /// it goes rather than sitting invisibly on the chart.
    /// </summary>
    private void DropFilterIfColorSourceChanged(ChartColorBy was)
    {
        if (EffectiveColorBy != was)
            ClearFilters();
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

        var filtering = Filtering;
        var primary = new HashSet<Guid>();
        var ghosted = new HashSet<Guid>();

        if (filtering)
        {
            foreach (var op in Document.Operations)
                if (InFilter(op))
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

                // Work no robot carries out still has to appear somewhere in a view whose rows are
                // robots, so it gathers in a row of its own at the end.
                var unmanned = Document.Operations.Where(o => !o.HasRobot).ToList();
                if (unmanned.Count > 0 && (!filtering || unmanned.Any(o => primary.Contains(o.Id))))
                    AddRow(new RowInfo
                    {
                        Title = "(No robot)",
                        Kind = RowKind.Robot,
                        Key = GanttDocument.NoRobotKey,
                        Operations = unmanned.Where(Keep).ToList(),
                        Ghosted = ghosted,
                    });
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

    /// <summary>
    /// A bar's fill. Whichever way the chart is coloured, an operation may have nothing to allocate
    /// against - no region, or no robot - and then it takes the neutral fill rather than borrowing
    /// some other item's colour.
    /// </summary>
    private Brush FillOf(Operation op) => EffectiveColorBy switch
    {
        // Every operation has a category, so this branch never falls back to the neutral fill.
        ChartColorBy.Category => CategoryColors.GetBrush(op.CategoryColorKey),
        ChartColorBy.Robot => op.HasRobot ? RobotColors.GetBrush(op.RobotName) : UnassignedBrush,
        _ => Document?.RegionOf(op) is { } region
            ? RegionColors.GetBrush(region.ColorKey)
            : UnassignedBrush,
    };

    private string LabelOf(Operation op)
    {
        if (GroupMode != ChartGroupMode.Chronological)
            return op.Name;

        var other = EffectiveColorBy switch
        {
            ChartColorBy.Category => op.Category,
            ChartColorBy.Robot => Document?.RegionOf(op)?.Name ?? "(no region)",
            _ => op.HasRobot ? op.RobotName : "(no robot)",
        };
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
                    line.Row.Ghosted.Contains(op.Id) && Filtering, X, cycle);
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
                : "Nothing matches the current filter.", 13, MutedBrush, Face);
            dc.DrawText(hint, new Point(HeaderWidth + 20, RulerHeight + 20));
        }

        if (IsPickingBar)
            DrawHint(dc, size, _barPickHint);
    }

    /// <summary>
    /// Banner across the top while a pick is running. The same shape and colour the image view uses
    /// for the same thing, so the two reads as one gesture wherever the user is pointing.
    /// </summary>
    private void DrawHint(DrawingContext dc, Size size, string message)
    {
        var text = Text(message, 12, Brushes.White, Face);
        var w = text.Width + 24;
        var h = text.Height + 12;
        var x = Math.Max(0, (size.Width - w) / 2);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            const double r = 6;
            ctx.BeginFigure(new Point(x, 0), true, true);
            ctx.LineTo(new Point(x + w, 0), true, false);
            ctx.LineTo(new Point(x + w, h - r), true, false);
            ctx.ArcTo(new Point(x + w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(x + r, h), true, false);
            ctx.ArcTo(new Point(x, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();

        dc.DrawGeometry(BannerBrush, null, geometry);
        dc.DrawText(text, new Point(x + 12, 6));
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
        (double From, double To) widestBand = default;
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
            {
                widest = rect;
                widestBand = band;
            }
        }

        if (widest != Rect.Empty && widest.Width > 34)
            DrawBarLabel(dc, op, widest, widestBand, x);

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
        foreach (var rect in OverlapRects(op, band, barRect, x))
            dc.DrawRectangle(Brushes.White, OverlapPen, rect);
    }

    /// <summary>
    /// Where a bar is painted white by a clash. The label reads these as well as the drawing does,
    /// so the two cannot disagree about which part of a bar is white.
    /// </summary>
    private List<Rect> OverlapRects(Operation op, (double From, double To) band, Rect barRect,
        Func<double, double> x)
    {
        var rects = new List<Rect>();
        if (!Conflicts.OverlapBands.TryGetValue(op.Id, out var overlaps))
            return rects;

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

            rects.Add(new Rect(left, barRect.Top + 1, width, Math.Max(1, barRect.Height - 2)));
        }

        return rects;
    }

    /// <summary>
    /// A bar's label, in whichever of black and white reads on what is underneath it - the same
    /// choice the filter chips make about their own fill. A bar is not all one colour, though: a
    /// clash paints part of it white, and the label crosses that patch in black however dark the
    /// bar's own colour is. So it is drawn twice, each pass clipped to the parts it belongs on.
    /// </summary>
    private void DrawBarLabel(DrawingContext dc, Operation op, Rect rect,
        (double From, double To) band, Func<double, double> x)
    {
        void Draw(Brush brush, Geometry clip, bool checkered)
        {
            var text = Text(LabelOf(op), Scaled(11.5), brush, BoldFace);
            text.MaxTextWidth = Math.Max(8, rect.Width - 8);
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;

            var origin = new Point(rect.Left + 5, rect.Top + (rect.Height - text.Height) / 2);
            dc.PushClip(clip);
            TextBorder.Draw(dc, text, origin, brush, checkered);
            dc.Pop();
        }

        var checkered = CheckeredFill(op);
        var bar = new RectangleGeometry(rect);
        var marks = ShowOverlaps ? OverlapRects(op, band, rect, x) : new List<Rect>();
        if (marks.Count == 0)
        {
            Draw(TextOn(op), bar, checkered);
            return;
        }

        // Each marker lies inside the bar, so an even-odd group of the two leaves exactly the part
        // of the bar still showing its own colour.
        var unmarked = new GeometryGroup { FillRule = FillRule.EvenOdd };
        unmarked.Children.Add(bar);
        var marked = new GeometryGroup();
        foreach (var mark in marks)
        {
            unmarked.Children.Add(new RectangleGeometry(mark));
            marked.Children.Add(new RectangleGeometry(mark));
        }

        Draw(TextOn(op), unmarked, checkered);
        // The clash marker is flat white whatever the bar is, so that pass never wants a border.
        Draw(Brushes.Black, marked, false);
    }

    /// <summary>
    /// Whether the fill this bar is drawn in is a checkered pair rather than a flat colour, which is
    /// what decides whether its label is bordered. Reads the same source as <see cref="TextOn"/>.
    /// </summary>
    private bool CheckeredFill(Operation op) => EffectiveColorBy switch
    {
        ChartColorBy.Category => CategoryColors.IsCheckered(op.CategoryColorKey),
        ChartColorBy.Robot => op.HasRobot && RobotColors.IsCheckered(op.RobotName),
        _ => Document?.RegionOf(op) is { } region && RegionColors.IsCheckered(region.ColorKey),
    };

    /// <summary>Black or white for a bar's label, from the fill the bar is actually drawn in.</summary>
    private Brush TextOn(Operation op) => EffectiveColorBy switch
    {
        ChartColorBy.Category => CategoryColors.GetTextBrush(op.CategoryColorKey),
        ChartColorBy.Robot => op.HasRobot ? RobotColors.GetTextBrush(op.RobotName) : UnassignedTextBrush,
        _ => Document?.RegionOf(op) is { } region
            ? RegionColors.GetTextBrush(region.ColorKey)
            : UnassignedTextBrush,
    };

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

            Point to;
            Point[] points;
            if (sourceRow == targetRow)
            {
                // Along the row: in at the leading edge, as it has always been.
                to = new Point(x(targetBands[0].From), ScreenTop(targetLine) + targetLine.Height / 2);
                points = Route(from, to);
            }
            else
            {
                // Across rows: onto the edge the link is coming from, a little in from the corner,
                // so the head points at the row the work moves to rather than sideways at it.
                var rect = BandRect(targetBands[0], ScreenTop(targetLine), targetLine.Height, x);
                var descending = targetRow > sourceRow;
                to = new Point(rect.Left + Math.Min(LinkEdgeInset, rect.Width / 2),
                    descending ? rect.Top : rect.Bottom);
                points = RouteToEdge(from, to, descending);
            }

            var selected = ReferenceEquals(link, _selectedLink);
            var pen = selected ? SelectedLinkPen : LinkPen;
            var head = selected ? Brushes.White : LinkBrush;

            // Fade the arrow to match when either end is only on screen because of the filter.
            var dimmed = Filtering &&
                         ((sourceLine.Row?.Ghosted.Contains(source.Id) ?? false) ||
                          (targetLine.Row?.Ghosted.Contains(target.Id) ?? false));
            if (dimmed)
                dc.PushOpacity(Palette.DimmedOpacity);

            // A selected link sits in the same white halo a selected bar does. The head carries its
            // own, since a link short enough to be head and nothing else has no line to show one on.
            if (selected)
                foreach (var glow in SelectionGlowPens)
                    for (var i = 0; i < points.Length - 1; i++)
                        dc.DrawLine(glow, points[i], points[i + 1]);

            for (var i = 0; i < points.Length - 1; i++)
                dc.DrawLine(pen, points[i], points[i + 1]);

            // Registered from what was actually drawn, so the head a click has to land on is the
            // head the user is aiming at.
            _linkHits.Add((points, DrawArrowHead(dc, points[^1], points[^2], head, selected), link));

            if (dimmed)
                dc.Pop();
        }
    }

    /// <summary>
    /// Orthogonal route onto a bar's top or bottom edge, for a link between two rows: out of the
    /// source's end, down or up past the rows in between, across in the gap just outside the target,
    /// and onto it. The run across is held next to the target rather than halfway, where with rows
    /// far apart it would cut through the bars of a row in between.
    /// </summary>
    private static Point[] RouteToEdge(Point from, Point to, bool descending)
    {
        var stub = from.X + LinkElbow;
        var approach = descending ? to.Y - LinkApproach : to.Y + LinkApproach;
        return new[]
        {
            from,
            new Point(stub, from.Y),
            new Point(stub, approach),
            new Point(to.X, approach),
            to,
        };
    }

    /// <summary>Orthogonal route from a source end to a target start, always arriving from the left.</summary>
    private Point[] Route(Point from, Point to)
    {
        // Straight across, however small the gap. The knee is there to carry a link clear of the row
        // it leaves, which one that never leaves its row has no need of - so a pair sitting close
        // together reads as an arrowhead in the gap between them, or a stub of line behind it, and
        // not as a loop dropped below the row to make room for a knee nothing asked for.
        if (to.X >= from.X - 1 && Math.Abs(to.Y - from.Y) < 1)
            return new[] { from, to };

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

    /// <summary>
    /// The head at the end of a link, pointing the way its last segment travels: along the row for
    /// one arriving at a bar's leading edge, and down or up for one coming from another row.
    /// Returns the box it occupies, which is what a click on the head is tested against - a link
    /// short enough to be head and little else is otherwise almost impossible to hit.
    /// </summary>
    private static Rect DrawArrowHead(DrawingContext dc, Point tip, Point previous, Brush brush,
        bool glow)
    {
        const double length = 10;
        const double halfWidth = 3.5;

        var run = tip - previous;
        // A zero-length last segment has no direction to take; fall back to the old sideways head.
        var travel = run.Length < 1e-6 ? new Vector(1, 0) : run / run.Length;
        var across = new Vector(-travel.Y, travel.X) * halfWidth;
        var back = tip - travel * length;

        var figure = new PathFigure
        {
            StartPoint = tip,
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new LineSegment(back + across, true));
        figure.Segments.Add(new LineSegment(back - across, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        if (glow)
            foreach (var pen in HeadGlowPens)
                dc.DrawGeometry(null, pen, geometry);
        dc.DrawGeometry(brush, null, geometry);

        // The head only ever travels along an axis, so the box round its three corners is the head
        // itself rather than a loose approximation of it.
        var bounds = new Rect(back + across, back - across);
        bounds.Union(tip);
        return bounds;
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

        CommitHeaderEditor(); //Header editor display position will become invalid after any scroll or zoom, so commit and hide the editor

        var position = e.GetPosition(Surface);


        //Scoll normally when holding shift
        //if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        //{
        //    e.Handled = false;
        //    base.OnMouseWheel(e);
        //    return;
        //}

            // Ctrl+wheel stretches or squashes the rows, pinned to the row under the cursor so the
            // thing being looked at stays put. Offered over the headers too, since that is as much
            // "the chart" as the bars are.
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || position.X < HeaderWidth)
        {
            //Vertical Zoom
            ZoomRows(e.Delta > 0 ? 1.15 : 1 / 1.15, position.Y);
            e.Handled = true;
            return;
        }

        else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            //Horizontal Scroll
            _scrollX = Math.Max(0, _scrollX + Math.Sign(e.Delta) * ActualWidth * 0.05);
            Surface.InvalidateVisual();
            e.Handled = true;
            return;
        }

        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            //Vertical Scroll
            _scrollY = Math.Max(0, _scrollY - Math.Sign(e.Delta) * RowPitch);
            Surface.InvalidateVisual();
            e.Handled = true;
            return;
        }

        else if (position.X > HeaderWidth)
        {
            // Horizontal Zoom
            var cycle = Math.Max(GanttDocument.MinCycleTime, Document.CycleTime);
            var viewportW = Math.Max(10, Surface.ActualWidth - HeaderWidth);
            var oldPxPerUnit = viewportW * _zoom / cycle;
            var timeAtCursor = (position.X - HeaderWidth + _scrollX) / oldPxPerUnit;

            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1, 500);

            var newPxPerUnit = viewportW * _zoom / cycle;
            _scrollX = timeAtCursor * newPxPerUnit - (position.X - HeaderWidth);

            Surface.InvalidateVisual();
            e.Handled = true;
            return;
        }
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

    // -------------------------------------------------------------- panning

    /// <summary>
    /// Whether there is anywhere to pan to. The scroll bars were sized against the content on the
    /// last render, so they are the standing answer to how much room there is either way.
    /// </summary>
    private bool CanPan => HScroll.Maximum > 0.5 || VScroll.Maximum > 0.5;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.ChangedButton != MouseButton.Middle || Document == null || _panning || !CanPan)
            return;

        // The editor is placed against the scroll offset, so it cannot survive the view moving.
        CommitHeaderEditor();
        HideTip();

        _panning = true;
        _panPoint = e.GetPosition(Surface);
        Surface.Cursor = Cursors.ScrollAll;
        Surface.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.ChangedButton != MouseButton.Middle || !_panning)
            return;

        EndPan();
        e.Handled = true;
    }

    /// <summary>
    /// Alt+tab, a message box, anything that takes the mouse away mid-drag. Without this the view
    /// would still be panning when the pointer came back, with no button held.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_panning)
            EndPan();
    }

    private void EndPan()
    {
        _panning = false;
        Surface.ReleaseMouseCapture();

        // A pick is still running underneath if one was; its cursor is the one to go back to.
        Surface.Cursor = IsPickingBar ? Cursors.Cross : null;
    }

    /// <summary>
    /// Moves the view under the pointer so whatever was grabbed stays there: the content goes the
    /// way the mouse went, which is the offset going the other way.
    ///
    /// Each move is taken against the one before it rather than against where the drag started. The
    /// two are the same wherever there is room to scroll, and they differ only once an edge has been
    /// reached - where measuring from the start would bank all the travel spent pushing against it
    /// and leave the chart still while the pointer came back across that much again.
    /// </summary>
    private void PanTo(Point position)
    {
        _scrollX -= position.X - _panPoint.X;
        _scrollY -= position.Y - _panPoint.Y;
        _panPoint = position;

        // Both are clamped against the content on the way out, where the sizes are known.
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

        // A pick takes the whole surface: a click either answers it or cancels it, and nothing is
        // selected, dragged or opened on the way past.
        if (IsPickingBar)
        {
            var picked = HitBar(position, exclude: _barPickExclude);
            CompleteBarPick(picked);
            e.Handled = true;
            return;
        }

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

        // An arrowhead answers first, even over the bar it sits on; everything else about a link
        // only answers where no bar does.
        var headLink = HitLinkHead(position);
        _pressedOperation = headLink == null ? HitBar(position) : null;
        _resizing = _pressedOperation != null && OnResizeGrip(_pressedOperation, position);
        _pressedLink = headLink ?? (_pressedOperation == null ? HitLink(position) : null);
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

        // Ahead of everything else: while panning, the pointer is moving the view and is not over
        // anything in the sense the rest of this method means.
        if (_panning)
        {
            if (e.MiddleButton == MouseButtonState.Released)
                EndPan();
            else
                PanTo(position);
            return;
        }

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
            if (Filtering && !_rowOfOperation.ContainsKey(other.Id))
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
            // Esc calls off a pick before it drops the selection: while one is running it is the
            // only thing the key could sensibly mean.
            if (IsPickingBar)
            {
                CancelPick();
                e.Handled = true;
                return;
            }

            _selectedOperation = null;
            _selectedLink = null;
            Surface.InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        // Panning holds the mouse, so the pointer crossing the edge is part of the drag, not the end
        // of it - the cursor and the hover state both stay as they are until the button comes up.
        if (_panning)
            return;

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

    /// <summary>
    /// Runs the bar editor. It is a loop rather than a single ShowDialog because naming the work an
    /// operation runs alongside can be done by pointing at it on this very chart, which a modal
    /// dialog covers: the dialog closes, the pick runs here, and it opens again on the same draft.
    /// </summary>
    private async void EditBar(Operation bar)
    {
        // The flow lets go of the chart while a pick runs, so a second double-click has to be shut
        // out rather than opening a second dialog onto the same bar.
        if (_editingBar || Document == null)
            return;

        _editingBar = true;
        try
        {
            var draft = BarEditDraft.From(bar);
            BarEditDialog dialog;
            while (true)
            {
                dialog = new BarEditDialog(Document, bar, draft) { Owner = Window.GetWindow(this) };
                var accepted = dialog.ShowDialog() == true;
                draft = dialog.Draft;

                if (!accepted)
                    return;
                if (!dialog.PickRequested)
                    break;

                var picked = await PickOperationAsync(bar);
                if (picked != null && !draft.Simultaneous.Contains(picked.Id))
                    draft.Simultaneous.Add(picked.Id);
            }

            bar.Name = dialog.OperationName;
            bar.Notes = dialog.Notes;
            bar.Category = OperationCategories.Canonical(draft.Category, Document);
            Document.SetSimultaneous(bar, draft.Simultaneous);
            Document.EditTiming(bar, dialog.Start, dialog.Duration);
            RaiseEdited();
        }
        finally
        {
            _editingBar = false;
        }
    }

    private void HandleDoubleClick(Point position)
    {
        if (Document == null)
            return;

        // The same order the press takes, so what a double click opens is what a click would select.
        var link = HitLinkHead(position);
        if (link == null && HitBar(position) is { } bar)
        {
            EditBar(bar);
            return;
        }

        link ??= HitLink(position);
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

    /// <summary>
    /// A link whose arrowhead is under the pointer. This is asked before the bars are, because a
    /// link short enough to be head and little else has nothing else to offer a click, and the head
    /// is drawn over the bars rather than beside them. Nothing is added to the box for slack: the
    /// head is a few pixels across against a bar's full height, so what it takes from the bar is a
    /// sliver at one end and the bar is still there to be grabbed. The rest of a link takes no such
    /// precedence - a long line crossing a bar is not what the pointer is aiming at.
    /// </summary>
    private OperationLink? HitLinkHead(Point position)
    {
        if (position.X < HeaderWidth || position.Y < RulerHeight)
            return null;

        for (var i = _linkHits.Count - 1; i >= 0; i--)
            if (!_linkHits[i].Head.IsEmpty && _linkHits[i].Head.Contains(position))
                return _linkHits[i].Link;
        return null;
    }

    private OperationLink? HitLink(Point position)
    {
        const double tolerance = 5;
        if (position.X < HeaderWidth || position.Y < RulerHeight)
            return null;

        if (HitLinkHead(position) is { } head)
            return head;

        for (var i = _linkHits.Count - 1; i >= 0; i--)
        {
            var (points, _, link) = _linkHits[i];
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

        // Hovering answers in the order a click would, so the tooltip names the thing that would be
        // selected rather than the bar an arrowhead is lying on.
        var headLink = HitLinkHead(position);
        var bar = headLink == null ? HitBar(position) : null;
        var header = headLink == null && bar == null && position.X < HeaderWidth
            ? HitHeader(position)
            : null;
        object? hit = headLink;
        hit ??= bar;
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
               $"Robot: {(op.HasRobot ? op.RobotName : "(none)")}\n" +
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
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(2),
        };

        _headerEditorBox = new Viewbox() { Stretch = Stretch.Fill, Height = height, Child = _headerEditor, Width = rect.Width -8, HorizontalAlignment = HorizontalAlignment.Left, StretchDirection = StretchDirection.DownOnly };

        Canvas.SetLeft(_headerEditorBox, rect.Left + 4);
        Canvas.SetTop(_headerEditorBox, rect.Top + (rect.Height - height) / 2);
        _headerEditor.Width = _headerEditorBox.Width;
        Overlay.Children.Add(_headerEditorBox);

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
        var editorBox = _headerEditorBox;
        if (editor == null || line == null || Document == null || editorBox == null)
            return;

        _headerEditor = null;
        _headerEditorLine = null;
        _headerEditorBox = null;
        Overlay.Children.Remove(editorBox);

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
            // The catch-all rows stand for having no robot and no region; there is nothing there to
            // rename, and both leave the name empty, which the document refuses anyway.
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
        var editorBox = _headerEditorBox;
        _headerEditor = null;
        _headerEditorLine = null;
        _headerEditorBox = null;
        if (editor != null)
            Overlay.Children.Remove(_headerEditorBox);
        Surface.InvalidateVisual();
    }

    // --------------------------------------------------------------- helpers

    private FormattedText Text(string value, double emSize, Brush brush, Typeface face) => new(
        value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, emSize, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Pen RoundJoined(Pen pen)
    {
        var rounded = pen.Clone();
        rounded.LineJoin = PenLineJoin.Round;
        rounded.Freeze();
        return rounded;
    }

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
