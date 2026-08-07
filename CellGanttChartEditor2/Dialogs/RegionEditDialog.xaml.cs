using System.Windows;
using CellGanttChartEditor2.Models;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>
/// Rename a region, change its category, or ask to redraw its box on the image.
/// </summary>
public partial class RegionEditDialog : Window
{
    private readonly GanttDocument _document;
    private readonly ChartRegion _region;

    public string RegionName { get; private set; } = string.Empty;

    public RegionCategory Category { get; private set; }

    /// <summary>Set when the user asked to redefine the region's box.</summary>
    public bool RedrawRequested { get; private set; }

    public RegionEditDialog(GanttDocument document, ChartRegion region)
    {
        InitializeComponent();

        _document = document;
        _region = region;

        NameBox.Text = region.Name;
        CategoryBox.ItemsSource = RegionCategoryInfo.Options;
        CategoryBox.SelectedItem = RegionCategoryInfo.Options.First(o => o.Value == region.Category);

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Commit();

    private void Redraw_Click(object sender, RoutedEventArgs e)
    {
        RedrawRequested = true;
        Commit();
    }

    private void Commit()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Fail("Give the region a name.");
            return;
        }

        if (_document.Regions.Any(r => r.Id != _region.Id &&
                                       string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Fail($"Another region is already called \"{name}\".");
            return;
        }

        RegionName = name;
        Category = (CategoryBox.SelectedItem as CategoryOption)?.Value ?? RegionCategory.Uncategorised;
        DialogResult = true;
    }

    private void Fail(string message)
    {
        RedrawRequested = false;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
