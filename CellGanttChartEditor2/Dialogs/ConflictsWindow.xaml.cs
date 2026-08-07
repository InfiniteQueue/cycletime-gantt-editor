using System.Windows;
using CellGanttChartEditor2.Models;
using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>Lists every overlapping pair behind the conflict count in the status bar.</summary>
public partial class ConflictsWindow : Window
{
    public ConflictsWindow(GanttDocument document, ConflictResult conflicts)
    {
        InitializeComponent();

        SummaryText.Text = conflicts.Count == 1
            ? "1 overlapping pair"
            : $"{conflicts.Count} overlapping pairs";

        ConflictList.ItemsSource = conflicts.Conflicts
            .OrderByDescending(c => c.Overlap)
            .Select(c => new Row
            {
                Kind = c.KindText,
                A = $"{c.A.Name} ({c.A.RobotName})",
                B = $"{c.B.Name} ({c.B.RobotName})",
                Shared = Shared(document, c),
                Overlap = TimeMath.Format(c.Overlap),
            })
            .ToList();
    }

    private static string Shared(GanttDocument document, Conflict conflict)
    {
        var parts = new List<string>();
        if (conflict.Kind.HasFlag(ConflictKind.Robot))
            parts.Add(conflict.A.RobotName);
        if (conflict.Kind.HasFlag(ConflictKind.Region))
            parts.Add(document.RegionOf(conflict.A)?.Name ?? "(region)");
        return string.Join(", ", parts);
    }

    private sealed class Row
    {
        public string Kind { get; init; } = string.Empty;
        public string A { get; init; } = string.Empty;
        public string B { get; init; } = string.Empty;
        public string Shared { get; init; } = string.Empty;
        public string Overlap { get; init; } = string.Empty;
    }
}
