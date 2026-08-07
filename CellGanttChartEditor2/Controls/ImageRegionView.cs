using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Controls;

/// <summary>Outcome of a region pick: either an existing region, or the bounds of a freshly drawn box.</summary>
public sealed class RegionPickResult
{
    public ChartRegion? Existing { get; init; }
    public Rect NewBounds { get; init; }
    public bool IsNew => Existing == null;
}

/// <summary>
/// The reference image with the defined regions drawn over it as thick coloured borders.
/// Handles scroll-wheel zoom about the cursor, panning, and the click-or-draw region pick.
/// Region bounds are held in image pixel coordinates so swapping the image leaves them in place.
/// </summary>
public sealed class ImageRegionView : FrameworkElement
{
    private const double LabelFontSize = 11;
    private const double LabelPaddingX = 5;
    private const double LabelPaddingY = 1;
    private const double LabelBorderThickness = 2;
    private const double LabelCornerRadius = 3;

    /// <summary>Border weight at 1:1 zoom, and the extra it gains while highlighted.</summary>
    private const double BaseBorderThickness = 2;
    private const double HighlightExtraThickness = 3;
    private const double ClickSlop = 5;

    private static readonly Brush Background = Palette.Brush(Palette.ImageSurface);
    private static readonly Brush LabelPlateBrush = Palette.Brush(Palette.LabelPlate);
    private static readonly Brush BannerBrush = Palette.Brush(Color.FromArgb(0xEE, 0x1F, 0x6F, 0xEB));
    private static readonly Brush HintBrush = Palette.Brush(Palette.Muted);
    private static readonly Brush RubberBandFill = Palette.Brush(Color.FromArgb(48, 0xFF, 0xFF, 0xFF));
    private static readonly Pen RubberBandPen = MakeRubberBandPen();
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface BoldFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private BitmapSource? _image;
    private double _scale = 1;
    private Vector _offset;
    private bool _hasFitted;

    // Pick state
    private TaskCompletionSource<RegionPickResult?>? _pick;
    private bool _allowExisting;
    private bool _dragging;
    private Point _dragStart;
    private Point _dragCurrent;

    // Pan state
    private bool _panning;
    private Point _panStart;
    private Vector _panOrigin;

    private readonly List<(Rect Screen, ChartRegion Region)> _boxHits = new();
    private readonly List<(Rect Screen, ChartRegion Region)> _labelHits = new();
    private ChartRegion? _hover;

    private static Pen MakeRubberBandPen()
    {
        var pen = new Pen(Brushes.White, 2)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };
        pen.Freeze();
        return pen;
    }

    public ImageRegionView()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    /// <summary>Regions to draw. Set by the host whenever the document changes.</summary>
    public IReadOnlyList<ChartRegion> Regions { get; set; } = Array.Empty<ChartRegion>();

    /// <summary>Supplies the allocated colour a region's border is painted with.</summary>
    public Func<ChartRegion, Brush>? BrushProvider { get; set; }

    /// <summary>Region whose border is currently emphasised (driven by chart hover).</summary>
    public Guid? HighlightedRegionId { get; private set; }

    public bool IsPicking => _pick != null;

    public event EventHandler<ChartRegion>? RegionRenameRequested;

    public BitmapSource? Image
    {
        get => _image;
        private set
        {
            _image = value;
            InvalidateVisual();
        }
    }

    /// <summary>Loads image bytes. The view is only re-fitted the first time an image arrives.</summary>
    public void SetImage(byte[]? data)
    {
        if (data is not { Length: > 0 })
        {
            Image = null;
            _hasFitted = false;
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(data);
        bitmap.EndInit();
        bitmap.Freeze();

        var first = _image == null;
        Image = bitmap;
        if (first || !_hasFitted)
            FitToWindow();
    }

    public void SetHighlightedRegion(Guid? regionId)
    {
        if (HighlightedRegionId == regionId)
            return;
        HighlightedRegionId = regionId;
        InvalidateVisual();
    }

    public void FitToWindow()
    {
        if (_image == null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        _scale = Math.Min(ActualWidth / _image.PixelWidth, ActualHeight / _image.PixelHeight) * 0.97;
        if (_scale <= 0 || double.IsInfinity(_scale))
            _scale = 1;
        _offset = new Vector(
            (ActualWidth - _image.PixelWidth * _scale) / 2,
            (ActualHeight - _image.PixelHeight * _scale) / 2);
        _hasFitted = true;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ pick

    /// <summary>
    /// Asks the user to draw a box, or (when <paramref name="allowExisting"/>) click one of the
    /// regions already on the image. Resolves to null if the pick is cancelled.
    /// </summary>
    public Task<RegionPickResult?> PickRegionAsync(bool allowExisting)
    {
        CancelPick();
        _allowExisting = allowExisting;
        _pick = new TaskCompletionSource<RegionPickResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Cursor = Cursors.Cross;
        Focus();
        InvalidateVisual();
        return _pick.Task;
    }

    public void CancelPick() => CompletePick(null);

    private void CompletePick(RegionPickResult? result)
    {
        var pick = _pick;
        _pick = null;
        _dragging = false;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
        pick?.TrySetResult(result);
    }

    // -------------------------------------------------------------- geometry

    private Point ToScreen(Point image) => new(image.X * _scale + _offset.X, image.Y * _scale + _offset.Y);

    private Point ToImage(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    private Rect ToScreen(Rect image) => new(
        ToScreen(image.TopLeft),
        new Size(image.Width * _scale, image.Height * _scale));

    // --------------------------------------------------------------- drawing

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        dc.DrawRectangle(Background, null, new Rect(size));

        _boxHits.Clear();
        _labelHits.Clear();

        if (_image == null)
        {
            DrawCentredHint(dc, size, "No image loaded — use “Load image…” to pick one.");
            return;
        }

        dc.DrawImage(_image, new Rect(_offset.X, _offset.Y, _image.PixelWidth * _scale, _image.PixelHeight * _scale));

        foreach (var region in Regions)
            DrawRegion(dc, region);

        if (_dragging)
            dc.DrawRectangle(RubberBandFill, RubberBandPen, new Rect(_dragStart, _dragCurrent));

        if (IsPicking)
            DrawHint(dc, size, _allowExisting
                ? "Drag a box on the image to define a new region, or click an existing one. Esc to cancel."
                : "Drag a box on the image to redefine this region. Esc to cancel.");
    }

    /// <summary>
    /// A plain coloured box straddling the region bounds, titled with a dark plate outlined in the
    /// same colour. A highlighted region thickens and gains a white halo.
    /// The weight tracks the zoom, so a border keeps its size relative to the image.
    /// </summary>
    private void DrawRegion(DrawingContext dc, ChartRegion region)
    {
        var screen = ToScreen(region.Bounds);
        if (screen.Width <= 0 || screen.Height <= 0)
            return;

        _boxHits.Add((screen, region));

        var highlighted = HighlightedRegionId == region.Id || ReferenceEquals(_hover, region);
        var stroke = BrushProvider?.Invoke(region) ?? Brushes.Orange;

        var thickness = Math.Clamp(BaseBorderThickness * _scale, 2.5, 10);
        if (highlighted)
            thickness += Math.Clamp(HighlightExtraThickness, 2, 13);

        if (highlighted)
            foreach (var glow in Palette.GlowPens(thickness))
                dc.DrawRectangle(null, glow, screen);

        dc.DrawRectangle(null, new Pen(stroke, thickness), screen);

        DrawLabel(dc, region, screen, stroke, highlighted);
    }

    private void DrawLabel(DrawingContext dc, ChartRegion region, Rect screen, Brush stroke, bool highlighted)
    {
        var text = Text(region.Name, LabelFontSize, Brushes.White, highlighted ? BoldFace : Face);
        var w = text.Width + (LabelPaddingX + LabelBorderThickness) * 2;
        var h = text.Height + (LabelPaddingY + LabelBorderThickness) * 2;

        // Sits directly above the box, or tucks under the top edge when there is no room.
        var x = screen.Left;
        var y = Math.Max(0, screen.Top - h);

        var rect = new Rect(x, y, w, h);
        _labelHits.Add((rect, region));

        dc.DrawRoundedRectangle(LabelPlateBrush, new Pen(stroke, LabelBorderThickness), rect,
            LabelCornerRadius, LabelCornerRadius);
        dc.DrawText(text, new Point(x + LabelPaddingX + LabelBorderThickness,
            y + LabelPaddingY + LabelBorderThickness));
    }

    /// <summary>Banner across the top of the image while a region pick is running.</summary>
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

    private void DrawCentredHint(DrawingContext dc, Size size, string message)
    {
        var text = Text(message, 13, HintBrush, Face);
        dc.DrawText(text, new Point((size.Width - text.Width) / 2, (size.Height - text.Height) / 2));
    }

    private FormattedText Text(string value, double emSize, Brush brush, Typeface face) => new(
        value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, emSize, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ----------------------------------------------------------------- input

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_image == null)
            return;

        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var next = Math.Clamp(_scale * factor, 0.02, 60);
        factor = next / _scale;

        // Keep the image point under the cursor pinned.
        var pointer = e.GetPosition(this);
        _offset = (Vector)pointer - ((Vector)pointer - _offset) * factor;
        _scale = next;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        // Renaming is driven from the title drawn on the border.
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && !IsPicking &&
            TryStartRename(e.GetPosition(this)))
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && !IsPicking))
        {
            _panning = true;
            _panStart = e.GetPosition(this);
            _panOrigin = _offset;
            Cursor = Cursors.SizeAll;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && IsPicking)
        {
            _dragging = true;
            _dragStart = _dragCurrent = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (IsPicking)
        {
            CancelPick();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);

        if (_panning)
        {
            _offset = _panOrigin + (point - _panStart);
            InvalidateVisual();
            return;
        }

        if (_dragging)
        {
            _dragCurrent = point;
            InvalidateVisual();
            return;
        }

        if (IsPicking && _allowExisting)
        {
            var hit = HitRegion(point);
            if (!ReferenceEquals(hit, _hover))
            {
                _hover = hit;
                InvalidateVisual();
            }
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (_panning)
        {
            _panning = false;
            Cursor = IsPicking ? Cursors.Cross : Cursors.Arrow;
            ReleaseMouseCapture();
            return;
        }

        if (!_dragging || e.ChangedButton != MouseButton.Left)
            return;

        _dragging = false;
        ReleaseMouseCapture();

        var end = e.GetPosition(this);
        var moved = (end - _dragStart).Length;

        if (moved < ClickSlop)
        {
            // A click, not a box: adopt whichever region sits under the cursor.
            var hit = _allowExisting ? HitRegion(end) : null;
            if (hit != null)
                CompletePick(new RegionPickResult { Existing = hit });
            else
                InvalidateVisual();
            return;
        }

        var a = ToImage(_dragStart);
        var b = ToImage(end);
        var bounds = ClampToImage(new Rect(a, b));
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            InvalidateVisual();
            return;
        }

        CompletePick(new RegionPickResult { NewBounds = bounds });
    }

    private bool TryStartRename(Point point)
    {
        for (var i = _labelHits.Count - 1; i >= 0; i--)
        {
            if (!_labelHits[i].Screen.Contains(point))
                continue;
            RegionRenameRequested?.Invoke(this, _labelHits[i].Region);
            return true;
        }
        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsPicking)
        {
            CancelPick();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (!_hasFitted)
            FitToWindow();
    }

    private ChartRegion? HitRegion(Point point)
    {
        for (var i = _labelHits.Count - 1; i >= 0; i--)
            if (_labelHits[i].Screen.Contains(point))
                return _labelHits[i].Region;

        // Smallest box wins, so nested regions stay reachable.
        ChartRegion? best = null;
        var bestArea = double.MaxValue;
        foreach (var (screen, region) in _boxHits)
        {
            if (!screen.Contains(point))
                continue;
            var area = screen.Width * screen.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = region;
            }
        }
        return best;
    }

    private Rect ClampToImage(Rect rect)
    {
        if (_image == null)
            return rect;

        var x1 = Math.Clamp(rect.Left, 0, _image.PixelWidth);
        var y1 = Math.Clamp(rect.Top, 0, _image.PixelHeight);
        var x2 = Math.Clamp(rect.Right, 0, _image.PixelWidth);
        var y2 = Math.Clamp(rect.Bottom, 0, _image.PixelHeight);
        return new Rect(new Point(x1, y1), new Point(x2, y2));
    }
}
