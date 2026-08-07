using System.Windows;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>View and edit the length of a link, or delete it.</summary>
public partial class LinkEditDialog : Window
{
    public double Lag { get; private set; }

    public bool DeleteRequested { get; private set; }

    public LinkEditDialog(GanttDocument document, OperationLink link)
    {
        InitializeComponent();

        var source = document.FindOperation(link.SourceId);
        var target = document.FindOperation(link.TargetId);
        SummaryText.Text = $"From \"{source?.Name}\" ({source?.RobotName}) " +
                           $"to \"{target?.Name}\" ({target?.RobotName}).";
        LagBox.Text = TimeMath.Format(link.Lag);

        Loaded += (_, _) =>
        {
            LagBox.Focus();
            LagBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!OperationDialog.TryParse(LagBox.Text, out var lag))
        {
            ErrorText.Text = "The link length must be a number.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Lag = lag;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested = true;
        DialogResult = true;
    }
}
