using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CellGanttChartEditor2.Controls;
using CellGanttChartEditor2.Dialogs;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;
using Microsoft.Win32;

namespace CellGanttChartEditor2;

public partial class MainWindow : Window
{
    private const string ImageFilter =
        "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*";

    private static readonly Brush ConflictOkBrush = Palette.Brush(Color.FromRgb(0x7F, 0xD9, 0x8C));
    private static readonly Brush ConflictBadBrush = Palette.Brush(Color.FromRgb(0xFF, 0x8A, 0x8A));

    private readonly ColorAllocator _regionColors = new();
    private readonly ColorAllocator _robotColors = new();

    private GanttDocument _document = new();
    private string? _path;
    private bool _suppressChanges;

    public MainWindow()
    {
        InitializeComponent();

        Chart.RegionColors = _regionColors;
        Chart.RobotColors = _robotColors;
        Chart.Edited += (_, _) => RefreshAll();
        Chart.ConflictsChanged += (_, _) => UpdateConflictCount();
        Chart.HoveredRegionChanged += (_, regionIds) => ImageView.SetHighlightedRegions(regionIds);

        // Region borders take the finer checker tile so the pattern still reads inside a stroke.
        ImageView.BrushProvider = region => _regionColors.GetStrokeBrush(region.ColorKey);
        ImageView.RegionRenameRequested += ImageView_RegionRenameRequested;

        // A crosshair only takes the robot's colour while the chart is keyed on robots; otherwise
        // the colour would be one the chart is not currently showing, so it stays neutral.
        ImageView.RobotBrushProvider = robot => Chart.EffectiveColorBy == ChartColorBy.Robot
            ? _robotColors.GetStrokeBrush(robot.Name)
            : null;
        ImageView.RobotHovered += ImageView_RobotHovered;

        Items.RegionColors = _regionColors;
        Items.RobotColors = _robotColors;
        Items.Edited += (_, _) => RefreshAll();
        Items.RedrawRegionRequested += Items_RedrawRegionRequested;
        Items.PlaceRobotRequested += Items_PlaceRobotRequested;
        Items.AddRegionRequested += Items_AddRegionRequested;
        Items.AddRobotRequested += Items_AddRobotRequested;
        Items.DeleteRegionRequested += Items_DeleteRegionRequested;
        Items.DeleteRobotRequested += Items_DeleteRobotRequested;

        GroupByBox.ItemsSource = ViewOptions.GroupModes;
        GroupByBox.SelectedIndex = 0;
        ColorByBox.ItemsSource = ViewOptions.ColorModes;
        ColorByBox.SelectedIndex = 0;

        PreviewKeyDown += MainWindow_PreviewKeyDown;

        LoadDocument(new GanttDocument(), null);
    }

    // ------------------------------------------------------------ document

    private void LoadDocument(GanttDocument document, string? path)
    {
        _document = document;
        _path = path;

        _suppressChanges = true;
        CycleTimeBox.Text = TimeMath.Format(document.CycleTime);
        _suppressChanges = false;

        Chart.SetDocument(document);
        ImageView.SetImage(document.ImageData);
        RefreshAll();
    }

    /// <summary>Re-syncs colours, the image overlay, the filter chips and the chart.</summary>
    private void RefreshAll()
    {
        _regionColors.Sync(_document.Regions.Select(r => r.ColorKey));
        _robotColors.Sync(_document.Robots());

        // A region only goes when it is deleted outright, which takes its operations with it.
        var live = _document.Regions.Select(r => r.Id).ToHashSet();
        Chart.RegionFilter.RemoveWhere(id => !live.Contains(id));

        ImageView.Regions = _document.Regions;
        ImageView.RobotMarkers = _document.PlacedRobots();
        ImageView.InvalidateVisual();

        Items.Document = _document;
        Items.Rebuild();

        RebuildFilterChips();
        Chart.Refresh();

        ImageButton.Content = _document.ImageData == null ? "Load image..." : "Change image...";
        Title = _path == null
            ? "Cell Gantt Chart Editor"
            : $"Cell Gantt Chart Editor - {Path.GetFileName(_path)}";

        StatusText.Text =
            $"{_document.Operations.Count} operations   |   " +
            $"{_document.Robots().Count} robots   |   " +
            $"{_document.Regions.Count} regions   |   " +
            $"{_document.Links.Count} links";
    }

    private void UpdateConflictCount()
    {
        var count = Chart.Conflicts.Count;
        ConflictText.Text = $"Conflicts: {count}";
        ConflictText.Foreground = count == 0 ? ConflictOkBrush : ConflictBadBrush;
    }

    // -------------------------------------------------------------- toolbar

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_document.Operations.Count > 0 &&
            MessageBox.Show(this, "Discard the current chart?", "New chart",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        LoadDocument(new GanttDocument(), null);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = DocumentFile.Filter, DefaultExt = DocumentFile.Extension };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            LoadDocument(DocumentFile.Load(dialog.FileName), dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open that chart.\n\n{ex.Message}", "Open",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null)
            SaveAs_Click(sender, e);
        else
            SaveTo(_path);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = DocumentFile.Filter,
            DefaultExt = DocumentFile.Extension,
            FileName = _path == null ? "chart" + DocumentFile.Extension : Path.GetFileName(_path),
        };
        if (dialog.ShowDialog(this) == true)
            SaveTo(dialog.FileName);
    }

    private void SaveTo(string path)
    {
        try
        {
            DocumentFile.Save(_document, path);
            _path = path;
            RefreshAll();
            StatusText.Text = $"Saved to {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the chart.\n\n{ex.Message}", "Save",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = ImageFilter };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            // Regions keep their image-pixel bounds, so they stay put when the image is swapped.
            _document.ImageData = File.ReadAllBytes(dialog.FileName);
            _document.ImageFileName = Path.GetFileName(dialog.FileName);
            ImageView.SetImage(_document.ImageData);
            RefreshAll();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load that image.\n\n{ex.Message}", "Load image",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FitImage_Click(object sender, RoutedEventArgs e) => ImageView.FitToWindow();

    private void OverlapToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (Chart != null)
            Chart.SetShowOverlaps(OverlapToggle.IsChecked == true);
    }

    private void CycleTime_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        ApplyCycleTime();
        e.Handled = true;
    }

    private void CycleTime_LostFocus(object sender, RoutedEventArgs e) => ApplyCycleTime();

    private void ApplyCycleTime()
    {
        if (_suppressChanges)
            return;

        if (!OperationDialog.TryParse(CycleTimeBox.Text, out var value) || value <= 0)
        {
            CycleTimeBox.Text = TimeMath.Format(_document.CycleTime);
            return;
        }

        // Start times are absolute and deliberately survive this: only the drawing wraps.
        _document.CycleTime = value;
        CycleTimeBox.Text = TimeMath.Format(_document.CycleTime);
        Chart.Refresh();
    }

    private void Conflicts_Click(object sender, RoutedEventArgs e)
    {
        if (Chart.Conflicts.Count == 0)
        {
            MessageBox.Show(this, "No operations overlap.", "Conflicts",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new ConflictsWindow(_document, Chart.Conflicts) { Owner = this }.ShowDialog();
    }

    // ----------------------------------------------------------- view modes

    private void GroupBy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Chart == null || GroupByBox.SelectedItem is not NamedOption<ChartGroupMode> option)
            return;

        Chart.SetGroupMode(option.Value);

        // The colour source is only a free choice when every operation has its own row, and sorting
        // by time only means anything when a row is one operation.
        var chronological = option.Value == ChartGroupMode.Chronological;
        ColorByLabel.Visibility = chronological ? Visibility.Visible : Visibility.Collapsed;
        ColorByBox.Visibility = chronological ? Visibility.Visible : Visibility.Collapsed;
        SortByTimeButton.Visibility = chronological ? Visibility.Visible : Visibility.Collapsed;

        // Grouping decides the colour source, which the crosshairs follow.
        ImageView.InvalidateVisual();
    }

    private void SortByTime_Click(object sender, RoutedEventArgs e) => Chart.SortChronologically();

    private void ColorBy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Chart == null || ColorByBox.SelectedItem is not NamedOption<ChartColorBy> option)
            return;

        Chart.SetColorMode(option.Value);
        // The crosshairs follow the chart's colour source, so they have to be redrawn with it.
        ImageView.InvalidateVisual();
    }

    private void ItemsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ItemsPanel == null)
            return;

        ItemsPanel.Visibility = ItemsToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------------------------------------------------------- items panel

    private async void Items_RedrawRegionRequested(object? sender, ChartRegion region)
    {
        var pick = await ImageView.PickRegionAsync(allowExisting: false);
        if (pick is not { IsNew: true })
            return;

        region.Bounds = pick.NewBounds;
        RefreshAll();
    }

    private async void Items_PlaceRobotRequested(object? sender, string robot)
    {
        if (!RequireImage("a robot is placed against a point on it", "Place robot"))
            return;

        var point = await ImageView.PickPointAsync($"\"{robot}\"");
        if (point == null)
            return;

        // Placing a robot is also what registers it, so the detail record is made here rather than
        // when the panel merely lists it.
        _document.RobotDetail(robot).Location = point;
        RefreshAll();
    }

    private async void Items_AddRegionRequested(object? sender, EventArgs e)
    {
        if (!RequireImage("a region is a box drawn on it", "Add region"))
            return;

        var pick = await ImageView.PickRegionAsync(allowExisting: false);
        if (pick is not { IsNew: true })
            return;

        var region = _document.AddRegion(
            _document.UnusedName("Region", _document.Regions.Select(r => r.Name)),
            pick.NewBounds, RegionCategory.Uncategorised);

        // Open its editor, so the placeholder name is sitting there ready to be replaced.
        Items.ExpandRegion(region);
        RefreshAll();
    }

    private void Items_AddRobotRequested(object? sender, EventArgs e)
    {
        var name = _document.UnusedName("Robot", _document.Robots());
        _document.RobotDetail(name);
        Items.ExpandRobot(name);
        RefreshAll();
    }

    private void Items_DeleteRegionRequested(object? sender, ChartRegion region)
    {
        var used = _document.Operations.Count(o => o.RegionId == region.Id);
        if (!ConfirmDelete($"region \"{region.Name}\"", used, "defined against it"))
            return;

        _document.RemoveRegion(region);
        RefreshAll();
    }

    private void Items_DeleteRobotRequested(object? sender, string robot)
    {
        var used = _document.Operations.Count(o =>
            string.Equals(o.RobotName, robot, StringComparison.OrdinalIgnoreCase));
        if (!ConfirmDelete($"robot \"{robot}\"", used, "it carries out"))
            return;

        _document.RemoveRobot(robot);
        RefreshAll();
    }

    /// <summary>Deleting takes operations with it, so the count is spelled out before it happens.</summary>
    private bool ConfirmDelete(string what, int operations, string relation)
    {
        var message = operations == 0
            ? $"Delete the {what}?"
            : $"Delete the {what}, and the {operations} operation{(operations == 1 ? "" : "s")} {relation}?";

        return MessageBox.Show(this, message, "Delete", MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private bool RequireImage(string because, string title)
    {
        if (_document.ImageData != null)
            return true;

        MessageBox.Show(this, $"Load the reference image first - {because}.", title,
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    /// <summary>
    /// Hovering a robot's crosshair lights up every region that robot works in - the bars in the
    /// chart, and the same regions' borders on the image the pointer is already over.
    /// </summary>
    private void ImageView_RobotHovered(object? sender, RobotInfo? robot)
    {
        var regions = robot == null
            ? Array.Empty<Guid>()
            : _document.RegionsOfRobot(robot.Name).ToArray();

        Chart.SetHighlightedRegions(regions);
        ImageView.SetHighlightedRegions(regions);
    }

    // --------------------------------------------------------------- filter

    private void RebuildFilterChips()
    {
        _suppressChanges = true;
        FilterPanel.Children.Clear();

        foreach (var region in _document.Regions)
        {
            var chip = new ToggleButton
            {
                Content = region.Name,
                Style = (Style)FindResource("FilterChip"),
                Background = _regionColors.GetBrush(region.ColorKey),
                Foreground = _regionColors.GetTextBrush(region.ColorKey),
                Tag = region.Id,
                IsChecked = Chart.RegionFilter.Contains(region.Id),
                ToolTip = $"{region.Name}  -  {RegionCategoryInfo.Display(region.Category)}",
            };
            chip.Checked += FilterChip_Changed;
            chip.Unchecked += FilterChip_Changed;
            FilterPanel.Children.Add(chip);
        }

        if (_document.Regions.Count == 0)
        {
            FilterPanel.Children.Add(new TextBlock
            {
                Text = "No regions defined yet.",
                Foreground = Palette.Brush(Palette.Muted),
            });
        }

        _suppressChanges = false;
    }

    private void FilterChip_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChanges || sender is not ToggleButton chip || chip.Tag is not Guid regionId)
            return;

        if (chip.IsChecked == true)
            Chart.RegionFilter.Add(regionId);
        else
            Chart.RegionFilter.Remove(regionId);

        Chart.Refresh();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        Chart.RegionFilter.Clear();
        RebuildFilterChips();
        Chart.Refresh();
    }

    // ------------------------------------------------------ add / edit flow

    private async void AddOperation_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage("operations are defined against a region of it", "Add operation"))
            return;

        AddOperationButton.IsEnabled = false;
        try
        {
            var pick = await ImageView.PickRegionAsync(allowExisting: true);
            if (pick == null)
                return;

            var existing = pick.Existing;
            var dialog = new OperationDialog(_document, existing) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            var region = existing ?? _document.AddRegion(dialog.RegionName, pick.NewBounds, dialog.Category);

            _document.AddOperation(new Operation
            {
                Name = dialog.OperationName,
                RobotName = dialog.RobotName,
                RegionId = region.Id,
                Start = dialog.Start,
                Duration = dialog.Duration,
            });

            RefreshAll();
        }
        finally
        {
            AddOperationButton.IsEnabled = true;
        }
    }

    private async void ImageView_RegionRenameRequested(object? sender, ChartRegion region)
    {
        var dialog = new RegionEditDialog(_document, region) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        region.Name = dialog.RegionName;
        region.Category = dialog.Category;
        RefreshAll();

        if (!dialog.RedrawRequested)
            return;

        var pick = await ImageView.PickRegionAsync(allowExisting: false);
        if (pick is { IsNew: true })
        {
            region.Bounds = pick.NewBounds;
            RefreshAll();
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ImageView.IsPickingAnything)
        {
            ImageView.CancelPick();
            e.Handled = true;
        }
    }
}
