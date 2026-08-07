using System.Windows;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>
/// Manual start / duration entry for a bar. The region is shown for reference but cannot change.
/// </summary>
public partial class BarEditDialog : Window
{
    public double Start { get; private set; }
    public double Duration { get; private set; }

    public BarEditDialog(GanttDocument document, Operation operation)
    {
        InitializeComponent();

        OperationText.Text = operation.Name;
        RobotText.Text = operation.RobotName;
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
