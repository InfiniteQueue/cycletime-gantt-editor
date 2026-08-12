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
/// The side panel's contents: every region, robot and operation the document currently holds, each
/// expandable into its own property editor. Cards are built by hand rather than bound, in keeping
/// with the rest of the app, and rebuilt whenever the document changes - so which cards were open is
/// remembered by key rather than by control.
/// </summary>
public partial class ItemListView : UserControl
{
    private static readonly Thickness FieldMargin = new(0, 3, 0, 3);

    /// <summary>Stands for one entry in an operation's region list. Null is "(No region)".</summary>
    private sealed record RegionChoice(Guid? Id, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Stands for one operation in a list of them.</summary>
    private sealed record OperationChoice(Operation Op)
    {
        public override string ToString() => Op.Name;
    }

    private const string NoRegionText = "(No region)";
    private const string NoRobotText = "(No robot)";
    private const string AddSimultaneousText = "Add an operation...";

    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set while cards are being created, so the controls' own events are not mistaken for edits.</summary>
    private bool _building;

    public ItemListView()
    {
        InitializeComponent();
    }

    public GanttDocument? Document { get; set; }

    public ColorAllocator RegionColors { get; set; } = new();

    public ColorAllocator RobotColors { get; set; } = new();

    /// <summary>Raised when something in here changed the document.</summary>
    public event EventHandler? Edited;

    /// <summary>The user asked to draw this region's box on the image again.</summary>
    public event EventHandler<ChartRegion>? RedrawRegionRequested;

    /// <summary>The user asked to place this robot, by name, on the image.</summary>
    public event EventHandler<string>? PlaceRobotRequested;

    /// <summary>The user asked for a new region or robot. Both need the host: one picks a box on the
    /// image, and both want their new card opened afterwards.</summary>
    public event EventHandler? AddRegionRequested;

    public event EventHandler? AddRobotRequested;

    /// <summary>The user asked for a new operation, which is the host's add-operation flow.</summary>
    public event EventHandler? AddOperationRequested;

    /// <summary>The user asked to delete an item. The host confirms; this panel does not.</summary>
    public event EventHandler<ChartRegion>? DeleteRegionRequested;

    public event EventHandler<string>? DeleteRobotRequested;

    public event EventHandler<Operation>? DeleteOperationRequested;

    /// <summary>
    /// The user asked to name the work this operation runs alongside by clicking a bar. The chart is
    /// the host's, so it runs the pick; this panel only asks.
    /// </summary>
    public event EventHandler<Operation>? PickSimultaneousRequested;

    /// <summary>Opens an item's editor, so a newly added one lands ready to be named.</summary>
    public void ExpandRegion(ChartRegion region) => _expanded.Add(RegionKey(region));

    public void ExpandRobot(string name) => _expanded.Add(RobotKey(name));

    public void ExpandOperation(Operation op) => _expanded.Add(OperationKey(op));

    private static string RegionKey(ChartRegion region) => "region:" + region.Id.ToString("N");

    private static string RobotKey(string name) => "robot:" + name;

    private static string OperationKey(Operation op) => "operation:" + op.Id.ToString("N");

    /// <summary>Rebuilds every card from the document. Cheap enough to do on any change.</summary>
    public void Rebuild()
    {
        _building = true;
        try
        {
            ItemHost.Children.Clear();

            var regions = Document?.Regions ?? new List<ChartRegion>();
            var robots = Document?.Robots() ?? new List<string>();
            var operations = Document?.Operations ?? new List<Operation>();

            SummaryText.Text = $"{regions.Count} regions, {robots.Count} robots, " +
                               $"{operations.Count} operations";

            var regionsContent = new StackPanel();
            if (regions.Count == 0)
                regionsContent.Children.Add(NewEmptyNote("No regions yet."));
            foreach (var region in regions)
                regionsContent.Children.Add(RegionCard(region));

            AddCategory("Regions", "Add region", () => AddRegionRequested?.Invoke(this, EventArgs.Empty), regionsContent);

            var robotsContent = new StackPanel();  
            if (robots.Count == 0)
                robotsContent.Children.Add(NewEmptyNote("No robots yet."));
            foreach (var robot in robots)
                robotsContent.Children.Add(RobotCard(robot));
            AddCategory("Robots", "Add robot", () => AddRobotRequested?.Invoke(this, EventArgs.Empty), robotsContent);

            var operationsContent = new StackPanel();
            if (operations.Count == 0)
                operationsContent.Children.Add(NewEmptyNote("No operations yet."));
            foreach (var op in operations)
                operationsContent.Children.Add(OperationCard(op));
            AddCategory("Operations", "Add operation", () => AddOperationRequested?.Invoke(this, EventArgs.Empty), operationsContent);
        }
        finally
        {
            _building = false;
        }
    }

    // ----------------------------------------------------------------- cards

    private FrameworkElement RegionCard(ChartRegion region)
    {
        var key = RegionKey(region);
        var used = Document?.Operations.Count(o => o.RegionId == region.Id) ?? 0;

        var details = new StackPanel();

        var name = Field(details, "Name", new TextBox { Text = region.Name });
        Commit(name, () =>
        {
            var value = name.Text.Trim();
            if (value.Length == 0 || value == region.Name)
                return;
            region.Name = value;
            RaiseEdited();
        });

        var category = new ComboBox
        {
            ItemsSource = RegionCategoryInfo.Options,
            SelectedItem = RegionCategoryInfo.Options.FirstOrDefault(o => o.Value == region.Category),
        };
        Field(details, "Category", category);
        category.SelectionChanged += (_, _) =>
        {
            if (_building || category.SelectedItem is not CategoryOption option ||
                option.Value == region.Category)
                return;
            region.Category = option.Value;
            RaiseEdited();
        };

        // The area is a property like any other, with the crosshair beside it for going and getting
        // one - the same button that means "point at something on the image" everywhere else.
        details.Children.Add(WithPick("Area on the image",
            region.HasArea
                ? $"{region.Bounds.Width:0} by {region.Bounds.Height:0} pixels at {region.Bounds.X:0}, {region.Bounds.Y:0}"
                : "None. This region is not drawn on the image.",
            "Drag a box on the image for this region.",
            () => RedrawRegionRequested?.Invoke(this, region)));

        details.Children.Add(Actions(Delete(() => DeleteRegionRequested?.Invoke(this, region))));

        return Card(key, RegionColors.GetBrush(region.ColorKey), region.Name,
            used == 1 ? "1 operation" : $"{used} operations", details);
    }

    /// <summary>
    /// A robot is named, not owned: the card works from the name, and only reaches for the detail
    /// record when there is one. Listing a robot must not create one, or every robot in the document
    /// would be registered simply by opening this panel.
    /// </summary>
    private FrameworkElement RobotCard(string robot)
    {
        var key = RobotKey(robot);
        var placed = Document?.FindRobot(robot)?.Location;
        var used = Document?.Operations.Count(o =>
            string.Equals(o.RobotName, robot, StringComparison.OrdinalIgnoreCase)) ?? 0;

        var details = new StackPanel();

        var name = Field(details, "Name", new TextBox { Text = robot });
        Commit(name, () =>
        {
            var value = name.Text.Trim();
            if (value.Length == 0 || value == robot || Document == null)
                return;
            // The name is the robot's identity - every operation naming it moves with it.
            var wasOpen = _expanded.Remove(key);
            Document.RenameRobot(robot, value);
            if (wasOpen)
                _expanded.Add(RobotKey(value));
            RaiseEdited();
        });

        details.Children.Add(WithPick("Place on the image",
            placed is { } at ? $"At {at.X:0}, {at.Y:0}" : "Not placed.",
            "Click the spot on the image where this robot sits.",
            () => PlaceRobotRequested?.Invoke(this, robot)));

        var actions = new List<Button>();
        if (placed != null)
        {
            var clear = new Button { Content = "Clear place" };
            clear.Click += (_, _) =>
            {
                var info = Document?.FindRobot(robot);
                if (info == null)
                    return;
                info.Location = null;
                RaiseEdited();
            };
            actions.Add(clear);
        }

        actions.Add(Delete(() => DeleteRobotRequested?.Invoke(this, robot)));
        details.Children.Add(Actions(actions.ToArray()));

        return Card(key, RobotColors.GetBrush(robot), robot,
            used == 1 ? "1 operation" : $"{used} operations", details);
    }

    /// <summary>
    /// An operation's card. Everything about it can be changed from here, including which robot and
    /// which region it belongs to - and either of those may be nothing at all, so both lists carry an
    /// entry for that.
    /// </summary>
    private FrameworkElement OperationCard(Operation op)
    {
        var key = OperationKey(op);
        var region = Document?.RegionOf(op);

        var details = new StackPanel();

        var name = Field(details, "Name", new TextBox { Text = op.Name });
        Commit(name, () =>
        {
            var value = name.Text.Trim();
            if (value.Length == 0 || value == op.Name)
                return;
            op.Name = value;
            RaiseEdited();
        });

        // Editable, so an operation can be given a robot the document has never heard of - the same
        // way the add-operation dialog does it. A robot is named, not chosen from a fixed list.
        var robot = Field(details, "Robot", new ComboBox
        {
            IsEditable = true,
            ItemsSource = new[] { NoRobotText }.Concat(Document?.Robots() ?? new List<string>()).ToList(),
            Text = op.HasRobot ? op.RobotName : NoRobotText,
        });
        Commit(robot, () =>
        {
            // Clearing the box says the same thing as picking the entry at the top of the list.
            var value = robot.Text.Trim();
            if (value == NoRobotText)
                value = string.Empty;
            if (string.Equals(value, op.RobotName, StringComparison.Ordinal))
                return;
            op.RobotName = value;
            RaiseEdited();
        });

        var regionBox = Field(details, "Region", new ComboBox { ItemsSource = RegionChoices() });
        regionBox.SelectedItem = ((List<RegionChoice>)regionBox.ItemsSource)
            .FirstOrDefault(c => c.Id == (op.HasRegion ? op.RegionId : (Guid?)null));
        regionBox.SelectionChanged += (_, _) =>
        {
            if (_building || regionBox.SelectedItem is not RegionChoice choice)
                return;
            var chosen = choice.Id ?? Guid.Empty;
            if (chosen == op.RegionId)
                return;
            op.RegionId = chosen;
            RaiseEdited();
        };

        var start = Field(details, "Start time", new TextBox { Text = TimeMath.Format(op.Start) });
        var duration = Field(details, "Duration", new TextBox { Text = TimeMath.Format(op.Duration) });

        // Timing goes through the document so links behave exactly as they do when a bar is dragged.
        void ApplyTiming()
        {
            if (Document == null)
                return;

            if (!OperationDialog.TryParse(start.Text, out var startValue) ||
                !OperationDialog.TryParse(duration.Text, out var durationValue) || durationValue <= 0)
            {
                start.Text = TimeMath.Format(op.Start);
                duration.Text = TimeMath.Format(op.Duration);
                return;
            }

            if (Math.Abs(startValue - op.Start) < TimeMath.Epsilon &&
                Math.Abs(durationValue - op.Duration) < TimeMath.Epsilon)
                return;

            Document.EditTiming(op, startValue, durationValue);
            RaiseEdited();
        }

        Commit(start, ApplyTiming);
        Commit(duration, ApplyTiming);

        var notes = Field(details, "Notes", new TextBox
        {
            Text = op.Notes,
            AcceptsReturn = true,
            Height = 60,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Commit(notes, () =>
        {
            var value = notes.Text.Trim();
            if (value == op.Notes)
                return;
            op.Notes = value;
            RaiseEdited();
        });

        AddSimultaneous(details, op);

        details.Children.Add(Actions(Delete(() => DeleteOperationRequested?.Invoke(this, op))));

        var swatch = op.HasRegion && region != null ? RegionColors.GetBrush(region.ColorKey)
            : op.HasRobot ? RobotColors.GetBrush(op.RobotName)
            : Palette.Brush(Palette.GroupBlock);

        var subtitle = $"{(op.HasRobot ? op.RobotName : NoRobotText)} - {region?.Name ?? NoRegionText}, " +
                       $"{TimeMath.Format(op.Start)} for {TimeMath.Format(op.Duration)}";

        return Card(key, swatch, op.Name, subtitle, details);
    }

    /// <summary>
    /// The work an operation is declared to run alongside: what is on the list, a box of what could
    /// join it, and the button that goes and points at a bar instead. Edits land straight away, the
    /// way every other field on a card does - there is no OK here to wait for.
    /// </summary>
    private void AddSimultaneous(Panel details, Operation op)
    {
        if (Document == null)
            return;

        details.Children.Add(new TextBlock
        {
            Text = "Simultaneous with",
            FontSize = 11,
            Foreground = Palette.Brush(Palette.Muted),
            Margin = new Thickness(0, 8, 0, 0),
        });

        var together = Document.SimultaneousWith(op);
        if (together.Count == 0)
            details.Children.Add(NewEmptyNote("Nothing - this operation clashes with whatever it overlaps."));

        foreach (var other in together)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = other.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var remove = new Button { Content = "Remove", Padding = new Thickness(8, 1, 8, 1) };
            var target = other;
            remove.Click += (_, _) =>
            {
                Document.SetSimultaneous(op, target, false);
                RaiseEdited();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            details.Children.Add(row);
        }

        var choices = Document.SimultaneousCandidates(op);

        var picker = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The prompt sits at the top of the list so the box reads as an action and has somewhere to
        // return to; picking anything below it is the edit.
        var box = new ComboBox
        {
            ItemsSource = new List<object> { AddSimultaneousText }
                .Concat(choices.Select(o => new OperationChoice(o))).ToList(),
            SelectedIndex = 0,
            IsEnabled = choices.Count > 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.SelectionChanged += (_, _) =>
        {
            if (_building || box.SelectedItem is not OperationChoice choice)
                return;
            Document.SetSimultaneous(op, choice.Op, true);
            RaiseEdited();
        };
        picker.Children.Add(box);

        var pick = new Button
        {
            Content = "Pick on chart",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Click the operation on the chart.",
        };
        pick.Click += (_, _) => PickSimultaneousRequested?.Invoke(this, op);
        Grid.SetColumn(pick, 1);
        picker.Children.Add(pick);

        details.Children.Add(picker);
    }

    private List<RegionChoice> RegionChoices()
    {
        var choices = new List<RegionChoice> { new(null, NoRegionText) };
        if (Document != null)
            choices.AddRange(Document.Regions.Select(r => new RegionChoice(r.Id, r.Name)));
        return choices;
    }

    /// <summary>The row of buttons at the foot of an editor. Delete is pushed to the far end.</summary>
    private static FrameworkElement Actions(params Button[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons.Take(buttons.Length - 1))
        {
            button.Margin = new Thickness(0, 0, 6, 0);
            left.Children.Add(button);
        }
        row.Children.Add(left);

        var last = buttons[^1];
        Grid.SetColumn(last, 1);
        row.Children.Add(last);
        return row;
    }

    private Button Delete(Action confirm)
    {
        var button = new Button
        {
            Content = "Delete",
            Foreground = (Brush)FindResource("DangerBrush"),
            ToolTip = "Delete this item, and anything defined against it.",
        };
        button.Click += (_, _) => confirm();
        return button;
    }

    /// <summary>
    /// One item: a header that is always on show, and a detail panel below it that the Edit button
    /// opens. The header carries the item's allocated colour, so the panel reads the same way the
    /// chart and the image do.
    /// </summary>
    private FrameworkElement Card(string key, Brush swatch, string title, string subtitle,
        FrameworkElement details)
    {
        var header = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chip = new Border
        {
            Background = swatch,
            Width = 6,
            Height = 20,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(chip);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = Palette.Brush(Palette.Muted),
        });
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        var toggle = new ToggleButton
        {
            Content = "Edit",
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = _expanded.Contains(key),
        };
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);

        var body = new Border
        {
            BorderBrush = (Brush)FindResource("DividerBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8),
            Child = details,
            Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
        };

        void Sync()
        {
            var open = toggle.IsChecked == true;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open)
                _expanded.Add(key);
            else
                _expanded.Remove(key);
        }

        toggle.Checked += (_, _) => Sync();
        toggle.Unchecked += (_, _) => Sync();

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);

        return new Border
        {
            Background = (Brush)FindResource("ChartBrush"),
            BorderBrush = (Brush)FindResource("ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 0, 6),
            Child = stack,
        };
    }

    // --------------------------------------------------------------- helpers

    /// <summary>A section title with the button that adds another one of whatever it lists.</summary>
    private void AddCategory(string text, string addLabel, Action add, Panel contentStack)
    {
        var row = new Grid { Margin = new Thickness(2, 4, 0, 5) };
        row.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = $"{text.ToUpperInvariant()}  - {contentStack.Children.Count} items",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Brush(Palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });

        var button = new Button
        {
            Content = addLabel,
            FontSize = 11,
            Padding = new Thickness(7, 1, 7, 1),
        };
        button.Click += (_, _) => add();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        row.Cursor = Cursors.Hand;

        row.MouseDown += (s, e) => contentStack.Visibility = contentStack.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ItemHost.Children.Add(row);
        ItemHost.Children.Add(contentStack);
    }

    private TextBlock NewEmptyNote(string text) => new TextBlock
    {
        Text = text,
        Foreground = Palette.Brush(Palette.Muted),
        Margin = new Thickness(2, 0, 0, 8),
    };

    /// <summary>
    /// A read-only property whose value comes from the image, with the crosshair button that goes
    /// and gets it.
    /// </summary>
    private FrameworkElement WithPick(string label, string value, string toolTip, Action pick)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Palette.Brush(Palette.Muted),
        });
        text.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);

        var button = new Button
        {
            Style = (Style)FindResource("PickButton"),
            ToolTip = toolTip,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => pick();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);

        return row;
    }

    /// <summary>Adds a labelled row to a detail panel and hands the control back.</summary>
    private static T Field<T>(Panel host, string label, T control) where T : FrameworkElement
    {
        host.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Palette.Brush(Palette.Muted),
            Margin = new Thickness(0, 2, 0, 0),
        });
        control.Margin = FieldMargin;
        host.Children.Add(control);
        return control;
    }

    /// <summary>Applies a text edit on Enter or when the box loses focus, the way the toolbar does.</summary>
    private static void Commit(TextBox box, Action apply) => Commit((Control)box, apply);

    /// <summary>
    /// The same contract for an editable combo box, selection included: choosing from the list only
    /// fills the text in, so the change lands when the box is left, exactly as a typed one does.
    /// </summary>
    private static void Commit(ComboBox box, Action apply) => Commit((Control)box, apply);

    private static void Commit(Control control, Action apply)
    {
        control.LostFocus += (_, _) => apply();
        control.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            apply();
            e.Handled = true;
        };
    }

    private void RaiseEdited() => Edited?.Invoke(this, EventArgs.Empty);
}
