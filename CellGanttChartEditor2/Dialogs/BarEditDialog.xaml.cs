using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>
/// Everything a bar carries that is not its robot or its region: the name, the timing, and the work
/// it is meant to run alongside. What a double-click on the bar opens. The robot and the region are
/// shown for reference but cannot change; the items panel is where those move.
/// </summary>
public partial class BarEditDialog : Window
{
    /// <summary>Stands for one entry in the operation list. Kept so the box can show a name.</summary>
    private sealed record OperationChoice(Operation Op, string Text)
    {
        public override string ToString() => Text;
    }

    private const string AddPrompt = "Add an operation...";

    private readonly GanttDocument _document;
    private readonly Operation _operation;
    private bool _building;

    public string OperationName { get; private set; } = string.Empty;
    public double Start { get; private set; }
    public double Duration { get; private set; }

    /// <summary>
    /// What the user has typed so far, kept outside the dialog because picking a bar on the chart
    /// means closing it and opening it again.
    /// </summary>
    public BarEditDraft Draft { get; }

    /// <summary>True when the dialog closed to let the user click an operation on the chart.</summary>
    public bool PickRequested { get; private set; }

    public string? Notes { get; private set; }

    public BarEditDialog(GanttDocument document, Operation operation, BarEditDraft draft)
    {
        InitializeComponent();

        _document = document;
        _operation = operation;
        Draft = draft;

        OperationBox.Text = draft.OperationName;
        RobotText.Text = operation.HasRobot ? operation.RobotName : "(none)";
        RegionText.Text = document.RegionOf(operation)?.Name ?? "(none)";
        StartBox.Text = draft.Start;
        DurationBox.Text = draft.Duration;
        NotesBox.Text = draft.Notes;
        CategoryBox.ItemsSource = OperationCategoryInfo.Options;
        CategoryBox.SelectedItem = OperationCategoryInfo.Options.First(o => o.Value == draft.Category);

        ShowSimultaneous();

        var incoming = document.IncomingLink(operation.Id);
        if (incoming != null)
        {
            var source = document.FindOperation(incoming.SourceId);
            DrivenText.Text = $"Driven by a link from \"{source?.Name}\". " +
                              "Changing the start time here changes the link length.";
            DrivenText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            StartBox.Focus();
            StartBox.SelectAll();
        };
    }

    // ------------------------------------------------------------ simultaneity

    /// <summary>
    /// Redraws the list of pairings and the box of what is left to add. Both come from the draft
    /// rather than the operation: nothing is written to the document until OK.
    /// </summary>
    private void ShowSimultaneous()
    {
        _building = true;
        try
        {
            SimultaneousList.Children.Clear();

            var chosen = Draft.Simultaneous
                .Select(_document.FindOperation)
                .Where(o => o != null)
                .Select(o => o!)
                .ToList();

            if (chosen.Count == 0)
                SimultaneousList.Children.Add(new TextBlock
                {
                    Text = "Nothing yet - this operation conflicts with everything it overlaps.",
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                });

            foreach (var op in chosen)
                SimultaneousList.Children.Add(Row(op));

            var candidates = _document.Operations
                .Where(o => o.Id != _operation.Id && !Draft.Simultaneous.Contains(o.Id))
                .Select(o => new OperationChoice(o, Describe(o)))
                .ToList();

            // The first entry is the prompt, so the box reads as an action and has somewhere to
            // return to once a choice has been taken.
            SimultaneousBox.ItemsSource = new List<object> { AddPrompt }.Concat(candidates).ToList();
            SimultaneousBox.SelectedIndex = 0;
            SimultaneousBox.IsEnabled = candidates.Count > 0;
        }
        finally
        {
            _building = false;
        }
    }

    private FrameworkElement Row(Operation op)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = Describe(op),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var remove = new Button { Content = "Remove", Padding = new Thickness(8, 1, 8, 1) };
        remove.Click += (_, _) =>
        {
            Draft.Simultaneous.Remove(op.Id);
            ShowSimultaneous();
        };
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        return row;
    }

    /// <summary>An operation as it reads in a list: its name, and the robot doing it if there is one.</summary>
    private static string Describe(Operation op) =>
        op.HasRobot ? $"{op.Name}  -  {op.RobotName}" : op.Name;

    private void Simultaneous_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_building || SimultaneousBox.SelectedItem is not OperationChoice choice)
            return;

        Draft.Simultaneous.Add(choice.Op.Id);
        ShowSimultaneous();
    }

    private void PickSimultaneous_Click(object sender, RoutedEventArgs e)
    {
        // The chart is behind this window and a modal dialog disables its owner, so the pick cannot
        // run while this is up. Everything typed goes to the draft and the chart reopens on it.
        Capture();
        PickRequested = true;
        DialogResult = true;
    }

    // -------------------------------------------------------------------- OK

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = OperationBox.Text.Trim();
        if (name.Length == 0)
        {
            Fail("Give the operation a name.");
            return;
        }

        if (!OperationDialog.TryParse(StartBox.Text, out var start))
        {
            Fail("The start time must be a number.");
            return;
        }

        if (!OperationDialog.TryParse(DurationBox.Text, out var duration) || duration <= 0)
        {
            Fail("The duration must be a positive number.");
            return;
        }

        Capture();
        OperationName = name;
        Start = start;
        Duration = duration;
        Notes = NotesBox.Text.Trim();
        DialogResult = true;
    }

    /// <summary>Copies the fields into the draft, so a trip out to the chart loses nothing.</summary>
    private void Capture()
    {
        Draft.OperationName = OperationBox.Text;
        Draft.Start = StartBox.Text;
        Draft.Duration = DurationBox.Text;
        Draft.Notes = NotesBox.Text;
        Draft.Category = (CategoryBox.SelectedItem as OperationCategoryOption)?.Value
                         ?? OperationCategory.Uncategorised;
    }

    private void NotesBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var selectionStart = NotesBox.SelectionStart;
            NotesBox.Text = NotesBox.Text.Insert(selectionStart, "\n");
            NotesBox.SelectionStart = selectionStart + 1;
            e.Handled = true;
        }
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}

/// <summary>
/// A bar's half-finished edits. The dialog closes to let the user click an operation on the chart
/// and opens again on the same draft, so the numeric fields are held as text - exactly as
/// <see cref="OperationDraft"/> does for the add-operation dialog, and for the same reason.
/// </summary>
public sealed class BarEditDraft
{
    public string OperationName { get; set; } = string.Empty;
    public string Start { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public List<Guid> Simultaneous { get; } = new();

    public string? Notes { get; set; } = string.Empty;

    public OperationCategory Category { get; set; } = OperationCategory.Uncategorised;

    public static BarEditDraft From(Operation op)
    {
        var draft = new BarEditDraft
        {
            OperationName = op.Name,
            Start = TimeMath.Format(op.Start),
            Duration = TimeMath.Format(op.Duration),
            Notes = op.Notes,
            Category = op.Category,
        };
        draft.Simultaneous.AddRange(op.SimultaneousWith);
        return draft;
    }
}
