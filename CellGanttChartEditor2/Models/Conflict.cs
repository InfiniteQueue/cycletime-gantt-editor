namespace CellGanttChartEditor2.Models;

[Flags]
public enum ConflictKind
{
    None = 0,
    Robot = 1,
    Region = 2,
}

public sealed class Conflict
{
    public required Operation A { get; init; }
    public required Operation B { get; init; }
    public ConflictKind Kind { get; init; }
    public double Overlap { get; init; }

    public string KindText => Kind switch
    {
        ConflictKind.Robot => "Robot",
        ConflictKind.Region => "Region",
        ConflictKind.Robot | ConflictKind.Region => "Robot + Region",
        _ => "-",
    };
}

/// <summary>Result of a full conflict sweep: the pairs, plus per-operation overlap bands to draw.</summary>
public sealed class ConflictResult
{
    public static readonly ConflictResult Empty = new();

    public List<Conflict> Conflicts { get; } = new();

    /// <summary>Overlapping time bands per operation, already wrapped into [0, cycle] and merged.</summary>
    public Dictionary<Guid, List<(double From, double To)>> OverlapBands { get; } = new();

    public int Count => Conflicts.Count;
}
