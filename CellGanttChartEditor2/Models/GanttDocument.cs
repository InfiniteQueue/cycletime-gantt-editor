using System.Windows;

namespace CellGanttChartEditor2.Models;

/// <summary>
/// The whole chart: reference image, regions, operations and links.
/// Robots are implicit - they exist for as long as an operation names them.
/// </summary>
public sealed class GanttDocument
{
    public const double MinCycleTime = 0.001;

    private double _cycleTime = 60;

    public List<ChartRegion> Regions { get; } = new();

    public List<Operation> Operations { get; } = new();

    public List<OperationLink> Links { get; } = new();

    /// <summary>
    /// Row arrangement, one per grouping mode. What a row *is* changes with the mode - robots when
    /// grouping by region, regions when grouping by robot, single operations when chronological -
    /// so an order dragged out in one mode means nothing in another and each keeps its own.
    /// </summary>
    public RowLayout RobotRows { get; init; } = new();

    public RowLayout RegionRows { get; init; } = new();

    public RowLayout OperationRows { get; init; } = new();

    /// <summary>
    /// Detail for robots that have any - a place on the image, or simply having been added by hand
    /// before there was any work for them. An entry here is also what keeps a robot alive with no
    /// operations left: one that only ever existed because an operation named it goes when that
    /// operation does, while one the user set up stays until it is deleted.
    /// </summary>
    public List<RobotInfo> RobotDetails { get; } = new();

    public RowLayout LayoutFor(ChartGroupMode mode) => mode switch
    {
        ChartGroupMode.Region => RobotRows,
        ChartGroupMode.Robot => RegionRows,
        _ => OperationRows,
    };

    public static string KeyOf(Guid id) => id.ToString("N");

    /// <summary>Raw bytes of the embedded reference image (whatever format the user picked).</summary>
    public byte[]? ImageData { get; set; }

    public string? ImageFileName { get; set; }

    /// <summary>Width of the chart in time units. Never rewrites operation start times.</summary>
    public double CycleTime
    {
        get => _cycleTime;
        set => _cycleTime = Math.Max(MinCycleTime, value);
    }

    // ---------------------------------------------------------------- lookup

    public ChartRegion? FindRegion(Guid id) => Regions.FirstOrDefault(r => r.Id == id);

    public Operation? FindOperation(Guid id) => Operations.FirstOrDefault(o => o.Id == id);

    public OperationLink? FindLink(Guid id) => Links.FirstOrDefault(l => l.Id == id);

    public ChartRegion? RegionOf(Operation op) => FindRegion(op.RegionId);

    /// <summary>
    /// Every robot the document knows: those an operation names, in first-use order, followed by any
    /// registered without work yet - one added through the items panel and not used since.
    /// </summary>
    public List<string> Robots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var op in Operations)
            if (seen.Add(op.RobotName))
                list.Add(op.RobotName);
        foreach (var robot in RobotDetails)
            if (seen.Add(robot.Name))
                list.Add(robot.Name);
        return list;
    }

    public RobotInfo? FindRobot(string name) =>
        RobotDetails.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The detail record for a robot, created on first use so callers can just edit it.</summary>
    public RobotInfo RobotDetail(string name)
    {
        var found = FindRobot(name);
        if (found != null)
            return found;

        var info = new RobotInfo { Name = name };
        RobotDetails.Add(info);
        return info;
    }

    /// <summary>Robots placed on the image, in first-use order. What the crosshairs are drawn from.</summary>
    public List<RobotInfo> PlacedRobots() => Robots()
        .Select(FindRobot)
        .Where(r => r is { Location: not null })
        .Select(r => r!)
        .ToList();

    /// <summary>Regions a robot's operations are involved with, in definition order.</summary>
    public List<Guid> RegionsOfRobot(string name)
    {
        var ids = Operations
            .Where(o => string.Equals(o.RobotName, name, StringComparison.OrdinalIgnoreCase))
            .Select(o => o.RegionId)
            .ToHashSet();
        return Regions.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToList();
    }

    /// <summary>Regions that still carry at least one operation, in definition order.</summary>
    public List<ChartRegion> UsedRegions()
    {
        var used = Operations.Select(o => o.RegionId).ToHashSet();
        return Regions.Where(r => used.Contains(r.Id)).ToList();
    }

    public IEnumerable<OperationLink> LinksInvolving(Guid operationId) =>
        Links.Where(l => l.SourceId == operationId || l.TargetId == operationId);

    public OperationLink? IncomingLink(Guid operationId) =>
        Links.FirstOrDefault(l => l.TargetId == operationId);

    /// <summary>
    /// Everything that links leaving <paramref name="op"/> drag along, transitively. These follow
    /// the operation as it moves, so they are not fixed points to measure against.
    /// </summary>
    public HashSet<Guid> Descendants(Operation op)
    {
        var found = new HashSet<Guid>();
        var pending = new Queue<Guid>();
        pending.Enqueue(op.Id);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var link in Links.Where(l => l.SourceId == current))
                if (found.Add(link.TargetId))
                    pending.Enqueue(link.TargetId);
        }

        return found;
    }

    // ---------------------------------------------------------------- edits

    public ChartRegion AddRegion(string name, Rect bounds, RegionCategory category)
    {
        var region = new ChartRegion { Name = name, Bounds = bounds, Category = category };
        Regions.Add(region);
        return region;
    }

    public void AddOperation(Operation op)
    {
        Operations.Add(op);
        Propagate(op);
    }

    public void RemoveOperation(Operation op)
    {
        Operations.Remove(op);
        Links.RemoveAll(l => l.SourceId == op.Id || l.TargetId == op.Id);
        PruneOrphans();
    }

    public void RemoveLink(OperationLink link) => Links.Remove(link);

    /// <summary>
    /// Deletes a region and everything defined against it. Regions and robots are the user's to add
    /// and remove, so nothing here happens on its own - an emptied region stays until it is deleted.
    /// </summary>
    public void RemoveRegion(ChartRegion region)
    {
        foreach (var op in Operations.Where(o => o.RegionId == region.Id).ToList())
        {
            Operations.Remove(op);
            Links.RemoveAll(l => l.SourceId == op.Id || l.TargetId == op.Id);
        }

        Regions.Remove(region);
        PruneOrphans();
    }

    /// <summary>Deletes a robot, and with it every operation it was carrying out.</summary>
    public void RemoveRobot(string name)
    {
        foreach (var op in Operations
                     .Where(o => string.Equals(o.RobotName, name, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            Operations.Remove(op);
            Links.RemoveAll(l => l.SourceId == op.Id || l.TargetId == op.Id);
        }

        RobotDetails.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        PruneOrphans();
    }

    /// <summary>A name no region or robot is using yet, as "{prefix} {n}".</summary>
    public string UnusedName(string prefix, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
        {
            var name = $"{prefix} {i}";
            if (!used.Contains(name))
                return name;
        }
    }

    /// <summary>Tidies the row layouts. Regions and robots themselves are never dropped here.</summary>
    public void PruneOrphans() => PruneLayouts();

    /// <summary>Clears row order and group entries that no longer name a live row.</summary>
    public void PruneLayouts()
    {
        RobotRows.Prune(Robots());
        RegionRows.Prune(Regions.Select(r => KeyOf(r.Id)).ToList());
        OperationRows.Prune(Operations.Select(o => KeyOf(o.Id)).ToList());
    }

    /// <summary>
    /// Applies a new start/duration and pushes the change down the link chain.
    /// </summary>
    public void SetTiming(Operation op, double start, double duration)
    {
        op.Start = start;
        op.Duration = Math.Max(0, duration);
        Propagate(op);
    }

    public void SetStart(Operation op, double start) => SetTiming(op, start, op.Duration);

    /// <summary>
    /// Moves an operation the way the user does: any link feeding this bar stretches to suit,
    /// leaving the bar it came from untouched, while links leaving this bar drag their targets along.
    /// </summary>
    public void MoveOperation(Operation op, double start)
    {
        op.Start = start;

        var incoming = IncomingLink(op.Id);
        if (incoming != null)
        {
            var source = FindOperation(incoming.SourceId);
            if (source != null)
                incoming.Lag = op.Start - source.End;
        }

        Propagate(op);
    }

    /// <summary>Manual start/duration edit, with the same link behaviour as dragging.</summary>
    public void EditTiming(Operation op, double start, double duration)
    {
        op.Duration = Math.Max(0, duration);
        MoveOperation(op, start);
    }

    /// <summary>Re-applies every link constraint reachable from <paramref name="op"/>.</summary>
    public void Propagate(Operation op)
    {
        PropagateCore(op, new HashSet<Guid>());
    }

    /// <summary>Re-applies every link constraint in the document, roots first.</summary>
    public void PropagateAll()
    {
        var targets = Links.Select(l => l.TargetId).ToHashSet();
        foreach (var root in Operations.Where(o => !targets.Contains(o.Id)).ToList())
            PropagateCore(root, new HashSet<Guid>());
    }

    private void PropagateCore(Operation op, HashSet<Guid> visited)
    {
        if (!visited.Add(op.Id))
            return;

        foreach (var link in Links.Where(l => l.SourceId == op.Id).ToList())
        {
            var target = FindOperation(link.TargetId);
            if (target == null)
                continue;
            target.Start = op.End + link.Lag;
            PropagateCore(target, visited);
        }
    }

    /// <summary>
    /// Creates a link from <paramref name="source"/> onto <paramref name="target"/>, taking the
    /// current separation as the link length. Returns null if the link would create a cycle.
    /// A bar can only be driven by one link, so any existing incoming link is replaced.
    /// </summary>
    public OperationLink? CreateLink(Operation source, Operation target)
    {
        if (source.Id == target.Id || WouldCycle(source, target))
            return null;

        Links.RemoveAll(l => l.TargetId == target.Id);

        var link = new OperationLink
        {
            SourceId = source.Id,
            TargetId = target.Id,
            Lag = target.Start - source.End,
        };
        Links.Add(link);
        return link;
    }

    /// <summary>True if driving <paramref name="target"/> from <paramref name="source"/> closes a loop.</summary>
    public bool WouldCycle(Operation source, Operation target)
    {
        var current = source.Id;
        var guard = 0;
        while (guard++ < 1000)
        {
            if (current == target.Id)
                return true;
            var incoming = IncomingLink(current);
            if (incoming == null)
                return false;
            current = incoming.SourceId;
        }
        return true;
    }

    public void SetLinkLag(OperationLink link, double lag)
    {
        link.Lag = lag;
        var source = FindOperation(link.SourceId);
        if (source != null)
            Propagate(source);
    }

    public void RenameRobot(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return;
        foreach (var op in Operations.Where(o => string.Equals(o.RobotName, oldName, StringComparison.OrdinalIgnoreCase)))
            op.RobotName = newName;

        // The robot's name is its row key, so the layout has to follow it.
        RobotRows.Rekey(oldName, newName);
        RekeyRobotDetail(oldName, newName);
    }

    /// <summary>
    /// Carries a robot's detail across a rename. Renaming onto a name already in use merges the two,
    /// and the place already set for the surviving name wins - it is the one still on screen.
    /// </summary>
    private void RekeyRobotDetail(string oldName, string newName)
    {
        var moving = FindRobot(oldName);
        if (moving == null)
            return;

        RobotDetails.Remove(moving);
        var existing = FindRobot(newName);
        if (existing == null)
        {
            moving.Name = newName;
            RobotDetails.Add(moving);
            return;
        }

        existing.Location ??= moving.Location;
    }
}
