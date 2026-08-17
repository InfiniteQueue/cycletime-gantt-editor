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

    /// <summary>How far back ctrl+z goes. Snapshots are small; this is about the user's memory.</summary>
    private const int UndoDepth = 100;

    private readonly ColorAllocator _regionColors = new();
    private readonly ColorAllocator _robotColors = new();
    private readonly ColorAllocator _categoryColors = new();

    /// <summary>
    /// Past states, oldest first, and states undone out of, the most recently undone last.
    /// Alongside them, <see cref="_baseline"/> is the document as it stood at the end of the last
    /// refresh - the thing every new state is compared against.
    /// </summary>
    private readonly List<DocumentSnapshot> _undo = new();
    private readonly List<DocumentSnapshot> _redo = new();
    private DocumentSnapshot? _baseline;

    private GanttDocument _document = new();
    private string? _path;
    private bool _suppressChanges;
    private bool _addingOperation;
    private bool _pickingSimultaneous;

    public MainWindow()
    {
        InitializeComponent();

        Chart.RegionColors = _regionColors;
        Chart.RobotColors = _robotColors;
        Chart.CategoryColors = _categoryColors;
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
        Items.AddOperationRequested += (_, _) => AddOperation();
        Items.DeleteRegionRequested += Items_DeleteRegionRequested;
        Items.DeleteRobotRequested += Items_DeleteRobotRequested;
        Items.DeleteOperationRequested += Items_DeleteOperationRequested;
        Items.PickSimultaneousRequested += Items_PickSimultaneousRequested;

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

        // A new document is not something the old one's history can be applied to.
        _undo.Clear();
        _redo.Clear();
        _baseline = null;

        ShowCycleTime();

        Chart.SetDocument(document);
        ImageView.SetImage(document.ImageData);
        RefreshAll();
    }

    /// <summary>Re-syncs colours, the image overlay, the filter chips and the chart.</summary>
    private void RefreshAll()
    {
        _regionColors.Sync(_document.Regions.Select(r => r.ColorKey));
        _robotColors.Sync(_document.Robots());

        // The presets first, so the ones the editor ships with keep their colours from document to
        // document; anything typed into this one follows behind them.
        _categoryColors.Sync(OperationCategories.Suggestions(_document).Select(OperationCategories.ColorKey));

        // A region only goes when it is deleted outright, which takes its operations with it. A
        // robot can also lose its last operation and stop being offered, so both filters are pruned
        // to what there are still chips for.
        var live = _document.Regions.Select(r => r.Id).ToHashSet();
        Chart.RegionFilter.RemoveWhere(id => !live.Contains(id));

        var robots = new HashSet<string>(_document.Robots(), StringComparer.OrdinalIgnoreCase);
        Chart.RobotFilter.RemoveWhere(name => !robots.Contains(name));

        // A category stops being offered once nothing is in it, so it cannot stay in the filter.
        var categories = UsedCategories().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Chart.CategoryFilter.RemoveWhere(category => !categories.Contains(category));

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

        RecordChange();
    }

    private void ShowCycleTime()
    {
        _suppressChanges = true;
        CycleTimeBox.Text = TimeMath.Format(_document.CycleTime);
        _suppressChanges = false;
    }

    // ----------------------------------------------------------------- undo

    /// <summary>
    /// Takes the document's picture at the end of every refresh and, when it differs from the last
    /// one, files that last one away as somewhere ctrl+z can go back to. Every edit in the app ends
    /// in a refresh, so this is the one place undo has to be wired into - and a refresh that changed
    /// nothing (a save, a new image, the panel opening) leaves the stack alone.
    /// </summary>
    private void RecordChange()
    {
        var now = DocumentSnapshot.Capture(_document);

        if (_baseline == null || now.Matches(_baseline))
        {
            _baseline = now;
            return;
        }

        _undo.Add(_baseline);
        if (_undo.Count > UndoDepth)
            _undo.RemoveAt(0);
        _baseline = now;

        // A fresh change is a new branch: what was undone out of is no longer ahead of the user.
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            StatusText.Text = "Nothing to undo.";
            return;
        }

        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        var current = _baseline!;
        _redo.Add(current);
        StatusText.Text = $"Undone: {DocumentSnapshot.Describe(previous, current)}.";
        GoTo(previous);
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            StatusText.Text = "Nothing to redo.";
            return;
        }

        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        var current = _baseline!;
        _undo.Add(current);
        StatusText.Text = $"Redone: {DocumentSnapshot.Describe(current, next)}.";
        GoTo(next);
    }

    /// <summary>
    /// Puts the document into a state off one of the two stacks. The baseline is moved first, so the
    /// refresh does not file the step away as a change of its own and send ctrl+z round in circles.
    /// </summary>
    private void GoTo(DocumentSnapshot snapshot)
    {
        var message = StatusText.Text;

        snapshot.Restore(_document);
        _baseline = snapshot;

        // The selected bar may be one the step has just taken out of the document.
        Chart.ClearSelection();
        ShowCycleTime();
        RefreshAll();

        // The refresh writes the running totals over it, so the message goes back afterwards.
        StatusText.Text = message;
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
        // Through the full refresh, so the change is one ctrl+z can take back like any other.
        RefreshAll();
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

        // Grouping decides the colour source, which the crosshairs and the filter chips follow.
        RebuildFilterChips();
        ImageView.InvalidateVisual();
    }

    private void SortByTime_Click(object sender, RoutedEventArgs e) => Chart.SortChronologically();

    private void ColorBy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Chart == null || ColorByBox.SelectedItem is not NamedOption<ChartColorBy> option)
            return;

        Chart.SetColorMode(option.Value);

        // The crosshairs and the chips follow the chart's colour source, so both go with it.
        RebuildFilterChips();
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
        if (!RequireImage("a region's area is a box drawn on it", "Region area"))
            return;

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

    /// <summary>
    /// Adds a region with no area. Giving it one is a separate step, with the crosshair in its
    /// editor - a region is a name and a category first, and a box on the image only if it needs one.
    /// </summary>
    private void Items_AddRegionRequested(object? sender, EventArgs e)
    {
        var region = _document.AddRegion(
            _document.UnusedName("Region", _document.Regions.Select(r => r.Name)),
            new Rect(), RegionCategory.Uncategorised);

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

    /// <summary>
    /// Deleting an operation takes its links with it, which the user cannot see from the panel - so
    /// they are counted into the question rather than sprung afterwards.
    /// </summary>
    private void Items_DeleteOperationRequested(object? sender, Operation op)
    {
        var links = _document.LinksInvolving(op.Id).Count();
        var message = links == 0
            ? $"Delete the operation \"{op.Name}\"?"
            : $"Delete the operation \"{op.Name}\", and the {links} link{(links == 1 ? "" : "s")} attached to it?";

        if (MessageBox.Show(this, message, "Delete", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _document.RemoveOperation(op);
        RefreshAll();
    }

    /// <summary>
    /// Names the work an operation runs alongside by pointing at it. The panel is not a dialog, so
    /// nothing has to close: the chart takes the next click and the pairing is made when it comes.
    /// </summary>
    private async void Items_PickSimultaneousRequested(object? sender, Operation op)
    {
        if (_pickingSimultaneous)
            return;

        _pickingSimultaneous = true;
        try
        {
            var picked = await Chart.PickOperationAsync(op);
            if (picked == null)
                return;

            _document.SetSimultaneous(op, picked, true);
            Items.ExpandOperation(op);
            RefreshAll();
        }
        finally
        {
            _pickingSimultaneous = false;
        }
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

    /// <summary>
    /// The chips are whatever the bars are being coloured by, so that picking one always means
    /// "keep the things wearing this colour". Which of the three it is comes from the chart, since
    /// grouping decides the colour source unless the view is chronological.
    /// </summary>
    private void RebuildFilterChips()
    {
        _suppressChanges = true;
        FilterPanel.Children.Clear();

        switch (Chart.EffectiveColorBy)
        {
            case ChartColorBy.Robot:
                FilterLabel.Text = "Filter by robot:";
                foreach (var robot in _document.Robots())
                    AddChip(robot, _robotColors, robot, robot, Chart.RobotFilter.Contains(robot));

                if (_document.Robots().Count == 0)
                    AddEmptyNote("No robots defined yet.");
                break;

            case ChartColorBy.Category:
                // Only the categories in use are offered. Every operation has one, so unlike the
                // other two there is never a chipless remainder to account for.
                FilterLabel.Text = "Filter by category:";
                foreach (var category in UsedCategories())
                    AddChip(category, _categoryColors, OperationCategories.ColorKey(category),
                        category, Chart.CategoryFilter.Contains(category));

                if (_document.Operations.Count == 0)
                    AddEmptyNote("No operations defined yet.");
                break;

            default:
                FilterLabel.Text = "Filter by region:";
                foreach (var region in _document.Regions)
                    AddChip(region.Name, _regionColors, region.ColorKey, region.Id,
                        Chart.RegionFilter.Contains(region.Id),
                        $"{region.Name}  -  {RegionCategoryInfo.Display(region.Category)}");

                if (_document.Regions.Count == 0)
                    AddEmptyNote("No regions defined yet.");
                break;
        }

        _suppressChanges = false;

        void AddChip(string text, ColorAllocator colors, string colorKey, object tag, bool on,
            string? tip = null)
        {
            var chip = new ToggleButton
            {
                Content = new BorderedText
                {
                    Text = text,
                    Foreground = colors.GetTextBrush(colorKey),
                    Checkered = colors.IsCheckered(colorKey),
                },
                Style = (Style)FindResource("FilterChip"),
                Background = colors.GetBrush(colorKey),
                Tag = tag,
                IsChecked = on,
                ToolTip = tip ?? text,
            };
            chip.Checked += FilterChip_Changed;
            chip.Unchecked += FilterChip_Changed;
            FilterPanel.Children.Add(chip);
        }

        void AddEmptyNote(string text) => FilterPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Palette.Brush(Palette.Muted),
        });
    }

    private void FilterChip_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChanges || sender is not ToggleButton chip)
            return;

        var on = chip.IsChecked == true;

        // Which set a chip belongs to is decided by what the chart is coloured by, not by the type
        // of its tag: a robot's name and an operation category are both strings, so the tag alone
        // can no longer tell them apart.
        switch (Chart.EffectiveColorBy)
        {
            case ChartColorBy.Robot when chip.Tag is string robot:
                Toggle(Chart.RobotFilter, robot);
                break;
            case ChartColorBy.Category when chip.Tag is string category:
                Toggle(Chart.CategoryFilter, category);
                break;
            case ChartColorBy.Region when chip.Tag is Guid regionId:
                Toggle(Chart.RegionFilter, regionId);
                break;
            default: return;
        }

        void Toggle<T>(HashSet<T> filter, T value)
        {
            if (on)
                filter.Add(value);
            else
                filter.Remove(value);
        }

        Chart.Refresh();
    }

    /// <summary>
    /// Categories at least one operation is in, presets first and then whatever the document named
    /// for itself - the order the dropdowns offer them in, so the chips read the same way.
    /// </summary>
    private List<string> UsedCategories()
    {
        var used = _document.Operations
            .Select(o => o.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return OperationCategories.Suggestions(_document).Where(used.Contains).ToList();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        Chart.ClearFilters();
        RebuildFilterChips();
        Chart.Refresh();
    }

    // ------------------------------------------------------ add / edit flow

    /// <summary>
    /// Runs the add-operation dialog. Picking something on the image needs the dialog out of the
    /// way, so the dialog closes, the pick runs, and it reopens on the same draft - which is why
    /// this is a loop rather than a single ShowDialog.
    /// </summary>
    private void AddOperation_Click(object sender, RoutedEventArgs e) => AddOperation();

    private async void AddOperation()
    {
        // Two ways in now - the toolbar and the items panel - and the flow lets go of the UI while a
        // pick runs, so re-entry has to be shut out here rather than by disabling one button.
        if (_addingOperation)
            return;

        _addingOperation = true;
        AddOperationButton.IsEnabled = false;
        try
        {
            var draft = new OperationDraft();
            while (true)
            {
                var dialog = new OperationDialog(_document, draft) { Owner = this };
                var accepted = dialog.ShowDialog() == true;
                draft = dialog.Draft;

                if (!accepted)
                    return;

                if (dialog.Request == OperationPickRequest.None)
                    break;

                await RunPick(dialog.Request, draft);
            }

            CommitOperation(draft);
            RefreshAll();
        }
        finally
        {
            _addingOperation = false;
            AddOperationButton.IsEnabled = true;
        }
    }

    /// <summary>Fetches whatever the dialog asked for from the image, leaving the draft otherwise alone.</summary>
    private async Task RunPick(OperationPickRequest request, OperationDraft draft)
    {
        if (!RequireImage("there is nothing to pick from until one is loaded", "Pick on the image"))
            return;

        if (request == OperationPickRequest.RobotLocation)
        {
            var point = await ImageView.PickPointAsync($"\"{draft.RobotName}\"");
            if (point != null)
                draft.RobotLocation = point;
            return;
        }

        var pick = await ImageView.PickRegionAsync(allowExisting: true);
        if (pick == null)
            return;

        if (pick.Existing is { } existing)
        {
            draft.NewRegion = false;
            draft.RegionId = existing.Id;
            return;
        }

        // A box drawn on the image defines a new region; it is only created if the dialog is accepted.
        draft.NewRegion = true;
        draft.RegionId = null;
        draft.NewRegionBounds = pick.NewBounds;
        if (draft.NewRegionName.Length == 0)
            draft.NewRegionName = _document.UnusedName("Region", _document.Regions.Select(r => r.Name));
    }

    private void CommitOperation(OperationDraft draft)
    {
        var regionId = Guid.Empty;
        if (draft.NewRegion)
            regionId = _document
                .AddRegion(draft.NewRegionName, draft.NewRegionBounds, draft.Category).Id;
        else if (draft.RegionId is { } chosen)
            regionId = chosen;

        OperationDialog.TryParse(draft.Start, out var start);
        OperationDialog.TryParse(draft.Duration, out var duration);

        _document.AddOperation(new Operation
        {
            Name = draft.OperationName,
            RobotName = draft.RobotName,
            RegionId = regionId,
            Start = start,
            Duration = duration,
            Notes = draft.Notes,
            Category = OperationCategories.Canonical(draft.OperationCategory, _document),
        });

        // No name, no robot: there is nothing to register a place against.
        if (draft.RobotLocation is { } at && draft.RobotName.Length > 0)
            _document.RobotDetail(draft.RobotName).Location = at;
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
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Key is not (Key.Z or Key.Y))
            return;

        // A text box has an undo of its own. Someone part way through typing means that one.
        if (Keyboard.FocusedElement is TextBoxBase)
            return;

        // Ctrl+Y and ctrl+shift+Z both redo; between them they cover what anyone is likely to try.
        if (e.Key == Key.Y || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            Redo();
        else
            Undo();

        e.Handled = true;
    }
}
