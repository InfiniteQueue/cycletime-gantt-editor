using CellGanttChartEditor2.Models;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// Finds operations that overlap in wrapped time while sharing a robot or a region.
/// </summary>
public static class ConflictDetector
{
    public static ConflictResult Analyse(GanttDocument document)
    {
        var result = new ConflictResult();
        var ops = document.Operations;
        if (ops.Count < 2)
            return result;

        var cycle = document.CycleTime;
        var bands = ops.ToDictionary(o => o.Id, o => TimeMath.Bands(o.Start, o.Duration, cycle));

        for (var i = 0; i < ops.Count; i++)
        for (var j = i + 1; j < ops.Count; j++)
        {
            var a = ops[i];
            var b = ops[j];

            var kind = ConflictKind.None;
            if (string.Equals(a.RobotName, b.RobotName, StringComparison.OrdinalIgnoreCase))
                kind |= ConflictKind.Robot;
            if (a.RegionId == b.RegionId)
                kind |= ConflictKind.Region;
            if (kind == ConflictKind.None)
                continue;

            var overlap = TimeMath.Intersect(bands[a.Id], bands[b.Id]);
            var length = TimeMath.Length(overlap);
            if (length <= TimeMath.Epsilon)
                continue;

            result.Conflicts.Add(new Conflict { A = a, B = b, Kind = kind, Overlap = length });
            Append(result.OverlapBands, a.Id, overlap);
            Append(result.OverlapBands, b.Id, overlap);
        }

        foreach (var key in result.OverlapBands.Keys.ToList())
            result.OverlapBands[key] = TimeMath.Merge(result.OverlapBands[key]);

        return result;
    }

    private static void Append(
        Dictionary<Guid, List<(double From, double To)>> map,
        Guid key,
        List<(double From, double To)> bands)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<(double, double)>();
        list.AddRange(bands);
    }
}
