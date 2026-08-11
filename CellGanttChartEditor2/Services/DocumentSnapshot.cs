using System.Windows;
using CellGanttChartEditor2.Models;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// A copy of everything about a document that an edit can change - cheap enough to take after every
/// one. Undo is built on these rather than on a list of commands: every path that edits the document
/// comes back through the host's single refresh, so comparing what is there now against what was
/// there before catches an edit wherever it came from - a bar dragged on the chart, a field in the
/// items panel, a dialog - without each of them having to remember to announce itself.
///
/// Model objects are held by reference with their fields copied beside them, so restoring puts the
/// very same <see cref="Operation"/> back rather than a lookalike, and anything still pointing at it
/// stays valid.
///
/// Row layouts are deliberately left out. Dragging a row or collapsing a group arranges the view
/// rather than changing the chart, and neither belongs on the undo stack.
/// </summary>
public sealed class DocumentSnapshot
{
    private sealed record OperationState(Operation Op, string Name, string Robot, Guid Region,
        double Start, double Duration);

    private sealed record LinkState(OperationLink Link, Guid Source, Guid Target, double Lag);

    private sealed record RegionState(ChartRegion Region, string Name, Rect Bounds, RegionCategory Category);

    private sealed record RobotState(RobotInfo Robot, string Name, Point? Location);

    private readonly List<OperationState> _operations = new();
    private readonly List<LinkState> _links = new();
    private readonly List<RegionState> _regions = new();
    private readonly List<RobotState> _robots = new();
    private double _cycleTime;

    public static DocumentSnapshot Capture(GanttDocument document)
    {
        var snapshot = new DocumentSnapshot { _cycleTime = document.CycleTime };

        foreach (var op in document.Operations)
            snapshot._operations.Add(new OperationState(op, op.Name, op.RobotName, op.RegionId,
                op.Start, op.Duration));

        foreach (var link in document.Links)
            snapshot._links.Add(new LinkState(link, link.SourceId, link.TargetId, link.Lag));

        foreach (var region in document.Regions)
            snapshot._regions.Add(new RegionState(region, region.Name, region.Bounds, region.Category));

        foreach (var robot in document.RobotDetails)
            snapshot._robots.Add(new RobotState(robot, robot.Name, robot.Location));

        return snapshot;
    }

    /// <summary>
    /// Puts the document back the way it was. The lists are rebuilt in place rather than swapped,
    /// because the document hands them out and holds them for the life of the chart.
    /// </summary>
    public void Restore(GanttDocument document)
    {
        document.CycleTime = _cycleTime;

        document.Operations.Clear();
        foreach (var state in _operations)
        {
            state.Op.Name = state.Name;
            state.Op.RobotName = state.Robot;
            state.Op.RegionId = state.Region;
            state.Op.Start = state.Start;
            state.Op.Duration = state.Duration;
            document.Operations.Add(state.Op);
        }

        document.Links.Clear();
        foreach (var state in _links)
        {
            state.Link.SourceId = state.Source;
            state.Link.TargetId = state.Target;
            state.Link.Lag = state.Lag;
            document.Links.Add(state.Link);
        }

        document.Regions.Clear();
        foreach (var state in _regions)
        {
            state.Region.Name = state.Name;
            state.Region.Bounds = state.Bounds;
            state.Region.Category = state.Category;
            document.Regions.Add(state.Region);
        }

        document.RobotDetails.Clear();
        foreach (var state in _robots)
        {
            state.Robot.Name = state.Name;
            state.Robot.Location = state.Location;
            document.RobotDetails.Add(state.Robot);
        }
    }

    /// <summary>
    /// True when nothing an undo would put back has changed. This is what keeps a refresh that
    /// changed nothing - saving, loading an image, opening the panel - off the undo stack.
    /// </summary>
    public bool Matches(DocumentSnapshot other) =>
        Math.Abs(_cycleTime - other._cycleTime) < TimeMath.Epsilon &&
        _operations.SequenceEqual(other._operations) &&
        _links.SequenceEqual(other._links) &&
        _regions.SequenceEqual(other._regions) &&
        _robots.SequenceEqual(other._robots);

    /// <summary>
    /// Names the change from <paramref name="before"/> to <paramref name="after"/>, for telling the
    /// user what an undo just took back. First difference found wins - it is a status line, not a
    /// full account.
    /// </summary>
    public static string Describe(DocumentSnapshot before, DocumentSnapshot after)
    {
        var gone = before._operations.Where(b => Find(after, b.Op) == null).ToList();
        if (gone.Count > 0)
            return gone.Count == 1
                ? $"deleting \"{gone[0].Name}\""
                : $"deleting \"{gone[0].Name}\" and {gone.Count - 1} more";

        var added = after._operations.Where(a => Find(before, a.Op) == null).ToList();
        if (added.Count > 0)
            return $"adding \"{added[0].Name}\"";

        foreach (var was in before._operations)
        {
            var now = Find(after, was.Op)!;
            if (Math.Abs(now.Start - was.Start) > TimeMath.Epsilon ||
                Math.Abs(now.Duration - was.Duration) > TimeMath.Epsilon)
                return $"the timing of \"{was.Name}\"";
            if (now.Name != was.Name)
                return $"renaming \"{was.Name}\"";
            if (now.Robot != was.Robot || now.Region != was.Region)
                return $"the change to \"{was.Name}\"";
        }

        if (before._links.Count != after._links.Count)
            return "the change to the links";
        if (before._regions.Count != after._regions.Count)
            return "the change to the regions";
        if (before._robots.Count != after._robots.Count)
            return "the change to the robots";

        return "the last change";
    }

    private static OperationState? Find(DocumentSnapshot snapshot, Operation op) =>
        snapshot._operations.FirstOrDefault(s => ReferenceEquals(s.Op, op));
}
