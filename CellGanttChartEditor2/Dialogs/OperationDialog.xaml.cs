using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>
/// Collects everything needed to define an operation. When the user picked an existing region the
/// region fields are shown read-only; a freshly drawn box asks for a name and a category.
/// </summary>
public partial class OperationDialog : Window
{
    private readonly GanttDocument _document;
    private readonly ChartRegion? _existingRegion;

    public string RegionName { get; private set; } = string.Empty;
    public RegionCategory Category { get; private set; } = RegionCategory.Uncategorised;
    public string RobotName { get; private set; } = string.Empty;
    public string OperationName { get; private set; } = string.Empty;
    public double Start { get; private set; }
    public double Duration { get; private set; }

    public OperationDialog(GanttDocument document, ChartRegion? existingRegion)
    {
        InitializeComponent();

        _document = document;
        _existingRegion = existingRegion;

        CategoryBox.ItemsSource = RegionCategoryInfo.Options;
        RobotBox.ItemsSource = Presets.Combine(Presets.RobotNames, document.Robots());
        OperationBox.ItemsSource = Presets.Combine(
            Presets.OperationNames,
            document.Operations.Select(o => o.Name));

        if (existingRegion != null)
        {
            HeaderText.Text = $"New operation in the existing region \"{existingRegion.Name}\".";
            RegionNameBox.Text = existingRegion.Name;
            RegionNameBox.IsReadOnly = true;
            RegionNameBox.IsTabStop = false;
            CategoryBox.SelectedItem = RegionCategoryInfo.Options.First(o => o.Value == existingRegion.Category);
            CategoryBox.IsEnabled = false;
        }
        else
        {
            HeaderText.Text = "New operation in a newly drawn region.";
            CategoryBox.SelectedIndex = 0;
        }

        StartBox.Text = "0";
        DurationBox.Text = "5";

        Loaded += (_, _) =>
        {
            if (existingRegion != null)
                RobotBox.Focus();
            else
                RegionNameBox.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var regionName = RegionNameBox.Text.Trim();
        if (regionName.Length == 0)
        {
            Fail("Give the region a name.");
            return;
        }

        if (_existingRegion == null &&
            _document.Regions.Any(r => string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase)))
        {
            Fail($"A region called \"{regionName}\" already exists. Pick it on the image instead, or use another name.");
            return;
        }

        var robot = RobotBox.Text.Trim();
        if (robot.Length == 0)
        {
            Fail("Give the robot a name.");
            return;
        }

        var operation = OperationBox.Text.Trim();
        if (operation.Length == 0)
        {
            Fail("Give the operation a name.");
            return;
        }

        if (!TryParse(StartBox.Text, out var start))
        {
            Fail("The start time must be a number.");
            return;
        }

        if (!TryParse(DurationBox.Text, out var duration) || duration <= 0)
        {
            Fail("The duration must be a positive number.");
            return;
        }

        RegionName = regionName;
        Category = (CategoryBox.SelectedItem as CategoryOption)?.Value ?? RegionCategory.Uncategorised;
        RobotName = robot;
        OperationName = operation;
        Start = start;
        Duration = duration;

        DialogResult = true;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    internal static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
