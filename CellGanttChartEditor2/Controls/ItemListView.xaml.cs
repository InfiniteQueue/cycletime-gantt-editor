using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Controls;

/// <summary>
/// The side panel's contents: every region and robot the document currently holds, each expandable
/// into its own property editor. Cards are built by hand rather than bound, in keeping with the rest
/// of the app, and rebuilt whenever the document changes - so which cards were open is remembered by
/// key rather than by control.
/// </summary>
public partial class ItemListView : UserControl
{
    private static readonly Thickness FieldMargin = new(0, 3, 0, 3);

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

    /// <summary>The user asked to delete an item. The host confirms; this panel does not.</summary>
    public event EventHandler<ChartRegion>? DeleteRegionRequested;

    public event EventHandler<string>? DeleteRobotRequested;

    /// <summary>Opens an item's editor, so a newly added one lands ready to be named.</summary>
    public void ExpandRegion(ChartRegion region) => _expanded.Add(RegionKey(region));

    public void ExpandRobot(string name) => _expanded.Add(RobotKey(name));

    private static string RegionKey(ChartRegion region) => "region:" + region.Id.ToString("N");

    private static string RobotKey(string name) => "robot:" + name;

    /// <summary>Rebuilds every card from the document. Cheap enough to do on any change.</summary>
    public void Rebuild()
    {
        _building = true;
        try
        {
            ItemHost.Children.Clear();

            var regions = Document?.Regions ?? new List<ChartRegion>();
            var robots = Document?.Robots() ?? new List<string>();

            SummaryText.Text = $"{regions.Count} regions, {robots.Count} robots";

            AddHeading("Regions", "Add region", () => AddRegionRequested?.Invoke(this, EventArgs.Empty));
            if (regions.Count == 0)
                AddEmptyNote("No regions yet.");
            foreach (var region in regions)
                ItemHost.Children.Add(RegionCard(region));

            AddHeading("Robots", "Add robot", () => AddRobotRequested?.Invoke(this, EventArgs.Empty));
            if (robots.Count == 0)
                AddEmptyNote("No robots yet.");
            foreach (var robot in robots)
                ItemHost.Children.Add(RobotCard(robot));
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
    private void AddHeading(string text, string addLabel, Action add)
    {
        var row = new Grid { Margin = new Thickness(2, 4, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Brush(Palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
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

        ItemHost.Children.Add(row);
    }

    private void AddEmptyNote(string text) => ItemHost.Children.Add(new TextBlock
    {
        Text = text,
        Foreground = Palette.Brush(Palette.Muted),
        Margin = new Thickness(2, 0, 0, 8),
    });

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
    private static void Commit(TextBox box, Action apply)
    {
        box.LostFocus += (_, _) => apply();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            apply();
            e.Handled = true;
        };
    }

    private void RaiseEdited() => Edited?.Invoke(this, EventArgs.Empty);
}
