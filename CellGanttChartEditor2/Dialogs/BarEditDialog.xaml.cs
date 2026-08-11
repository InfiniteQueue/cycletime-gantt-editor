using System.Windows;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>
/// Name and manual start / duration entry for a bar - what a double-click on it opens. The robot and
/// the region are shown for reference but cannot change; the items panel is where those move.
/// </summary>
public partial class BarEditDialog : Window
{
    public string OperationName { get; private set; } = string.Empty;
    public double Start { get; private set; }
    public double Duration { get; private set; }

    public BarEditDialog(GanttDocument document, Operation operation)
    {
        InitializeComponent();

        OperationBox.Text = operation.Name;
        RobotText.Text = operation.HasRobot ? operation.RobotName : "(none)";
        RegionText.Text = document.RegionOf(operation)?.Name ?? "(none)";
        StartBox.Text = TimeMath.Format(operation.Start);
        DurationBox.Text = TimeMath.Format(operation.Duration);

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

        OperationName = name;
        Start = start;
        Duration = duration;
        DialogResult = true;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
