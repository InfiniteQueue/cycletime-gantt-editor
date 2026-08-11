using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CellGanttChartEditor2.Models;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>
/// Collects everything needed to define an operation. The name and the timing are required; the
/// robot and the region are not, and a region chosen here may be an existing one, a box drawn on the
/// image, or a brand new region with no area at all.
///
/// Picking anything on the image closes this dialog - it has to get out of the way of the image -
/// and the host reopens it on the same <see cref="OperationDraft"/> with the answer filled in.
/// </summary>
public partial class OperationDialog : Window
{
    /// <summary>Stands for one entry in the region list.</summary>
    private sealed record RegionChoice(Guid? Id, bool IsNew, string Text)
    {
        public override string ToString() => Text;
    }

    private const string NoRegionText = "(No region)";
    private const string NewRegionText = "New region...";

    private readonly GanttDocument _document;
    private bool _loading = true;

    /// <summary>The draft as it stands, written back whether the dialog is accepted or not.</summary>
    public OperationDraft Draft { get; private set; }

    /// <summary>What the host should go and pick before reopening this dialog.</summary>
    public OperationPickRequest Request { get; private set; } = OperationPickRequest.None;

    public OperationDialog(GanttDocument document, OperationDraft draft)
    {
        InitializeComponent();

        _document = document;
        Draft = draft;

        HeaderText.Text = "New operation. Only the name and the timing are required - leave the " +
                          "robot or the region blank for work that has neither.";

        CategoryBox.ItemsSource = RegionCategoryInfo.Options;
        RobotBox.ItemsSource = document.Robots();
        OperationBox.ItemsSource = document.Operations.Select(o => o.Name).Distinct().ToList();

        RegionBox.ItemsSource = Choices();
        RegionBox.SelectedItem = CurrentChoice();
        CategoryBox.SelectedItem = RegionCategoryInfo.Options.First(o => o.Value == draft.Category);

        RobotBox.Text = draft.RobotName;
        OperationBox.Text = draft.OperationName;
        StartBox.Text = draft.Start;
        DurationBox.Text = draft.Duration;
        RegionNameBox.Text = draft.NewRegionName;

        _loading = false;
        SyncRegionFields();

        Loaded += (_, _) => RobotBox.Focus();
    }

    private List<RegionChoice> Choices()
    {
        var choices = new List<RegionChoice> { new(null, false, NoRegionText) };
        choices.AddRange(_document.Regions.Select(r => new RegionChoice(r.Id, false, r.Name)));
        choices.Add(new RegionChoice(null, true, NewRegionText));
        return choices;
    }

    private RegionChoice CurrentChoice()
    {
        var choices = (List<RegionChoice>)RegionBox.ItemsSource;
        if (Draft.NewRegion)
            return choices[^1];
        return choices.FirstOrDefault(c => c.Id == Draft.RegionId && Draft.RegionId != null) ?? choices[0];
    }

    /// <summary>Name and category are only asked for when the region is one being defined here.</summary>
    private void SyncRegionFields()
    {
        if (_loading)
            return;

        var creating = (RegionBox.SelectedItem as RegionChoice)?.IsNew == true;

        RegionNameLabel.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        RegionNameBox.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        CategoryLabel.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        CategoryBox.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;

        RegionAreaText.Text = creating
            ? Draft.NewRegionBounds.Width > 0 && Draft.NewRegionBounds.Height > 0
                ? $"Area drawn on the image: {Draft.NewRegionBounds.Width:0} by {Draft.NewRegionBounds.Height:0} pixels."
                : "No area on the image. Use the crosshair to draw one, or leave it - a region does not need one."
            : string.Empty;

        RobotPlaceText.Visibility = Draft.RobotLocation == null ? Visibility.Collapsed : Visibility.Visible;
        if (Draft.RobotLocation is { } at)
            RobotPlaceText.Text = $"Will be placed on the image at {at.X:0}, {at.Y:0}.";
    }

    private void Region_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RegionBox.SelectedItem is not RegionChoice choice)
            return;

        Draft.NewRegion = choice.IsNew;
        Draft.RegionId = choice.Id;
        if (choice.IsNew && RegionNameBox.Text.Trim().Length == 0)
            RegionNameBox.Text = _document.UnusedName("Region", _document.Regions.Select(r => r.Name));

        SyncRegionFields();
    }

    // ------------------------------------------------------------------ picks

    private void PickRegion_Click(object sender, RoutedEventArgs e) => AskFor(OperationPickRequest.Region);

    private void PickRobot_Click(object sender, RoutedEventArgs e)
    {
        if (RobotBox.Text.Trim().Length == 0)
        {
            Fail("Name the robot before placing it on the image.");
            return;
        }

        AskFor(OperationPickRequest.RobotLocation);
    }

    /// <summary>
    /// Closes with the draft intact so the host can run a pick. Nothing is validated on the way out:
    /// the user is part way through, and half-filled fields must survive the trip.
    /// </summary>
    private void AskFor(OperationPickRequest request)
    {
        Capture();
        Request = request;
        DialogResult = true;
    }

    /// <summary>Copies the fields into the draft, valid or not.</summary>
    private void Capture()
    {
        var choice = RegionBox.SelectedItem as RegionChoice;
        Draft.NewRegion = choice?.IsNew == true;
        Draft.RegionId = choice?.Id;
        Draft.NewRegionName = RegionNameBox.Text.Trim();
        Draft.Category = (CategoryBox.SelectedItem as CategoryOption)?.Value ?? RegionCategory.Uncategorised;
        Draft.RobotName = RobotBox.Text.Trim();
        Draft.OperationName = OperationBox.Text.Trim();
        Draft.Start = StartBox.Text;
        Draft.Duration = DurationBox.Text;
    }

    // ------------------------------------------------------------- acceptance

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Capture();

        if (Draft.OperationName.Length == 0)
        {
            Fail("Give the operation a name.");
            return;
        }

        if (Draft.NewRegion)
        {
            if (Draft.NewRegionName.Length == 0)
            {
                Fail("Give the new region a name, or choose \"(No region)\".");
                return;
            }

            if (_document.Regions.Any(r =>
                    string.Equals(r.Name, Draft.NewRegionName, StringComparison.OrdinalIgnoreCase)))
            {
                Fail($"A region called \"{Draft.NewRegionName}\" already exists. Choose it from the list, or use another name.");
                return;
            }
        }

        if (!TryParse(Draft.Start, out _))
        {
            Fail("The start time must be a number.");
            return;
        }

        if (!TryParse(Draft.Duration, out var duration) || duration <= 0)
        {
            Fail("The duration must be a positive number.");
            return;
        }

        Request = OperationPickRequest.None;
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
