namespace CellGanttChartEditor2.Models;

/// <summary>
/// A collapsible set of chart rows, referenced by the same keys <see cref="RowLayout"/> orders by.
/// </summary>
public sealed class RowGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Group";

    public bool Collapsed { get; set; }

    public List<string> Members { get; init; } = new();
}

/// <summary>
/// How one grouping mode arranges its rows: the manual order the user dragged them into, plus any
/// groups. Rows are identified by a key whose meaning depends on the mode - a robot name when
/// grouping by region, a region id when grouping by robot - so every mode keeps its own layout.
/// Keys not mentioned in <see cref="Order"/> simply fall in after the ones that are, which is what
/// keeps rows appearing sensibly before anything has been dragged.
/// </summary>
public sealed class RowLayout
{
    public List<string> Order { get; init; } = new();

    public List<RowGroup> Groups { get; init; } = new();

    public RowGroup? GroupOf(string key) =>
        Groups.FirstOrDefault(g => g.Members.Contains(key, StringComparer.OrdinalIgnoreCase));

    public RowGroup? FindGroup(Guid id) => Groups.FirstOrDefault(g => g.Id == id);

    /// <summary>Position in the manual order, or <see cref="int.MaxValue"/> for a row never moved.</summary>
    public int IndexOf(string key)
    {
        for (var i = 0; i < Order.Count; i++)
            if (string.Equals(Order[i], key, StringComparison.OrdinalIgnoreCase))
                return i;
        return int.MaxValue;
    }

    /// <summary>
    /// Rewrites the manual order to exactly <paramref name="keys"/>. Called with the full displayed
    /// order after a drag, so the stored order never drifts from what is on screen.
    /// </summary>
    public void SetOrder(IEnumerable<string> keys)
    {
        Order.Clear();
        Order.AddRange(keys);
    }

    /// <summary>Puts <paramref name="key"/> in <paramref name="group"/>, out of any other.</summary>
    public void AddToGroup(RowGroup group, string key)
    {
        LeaveGroup(key);
        if (!group.Members.Contains(key, StringComparer.OrdinalIgnoreCase))
            group.Members.Add(key);
        if (!Groups.Contains(group))
            Groups.Add(group);
    }

    /// <summary>Removes a row from whatever group holds it, dissolving one left too small to matter.</summary>
    public void LeaveGroup(string key)
    {
        var group = GroupOf(key);
        if (group == null)
            return;
        group.Members.RemoveAll(m => string.Equals(m, key, StringComparison.OrdinalIgnoreCase));
        if (group.Members.Count < 2)
            Groups.Remove(group);
    }

    /// <summary>
    /// Drops keys that no longer name anything - a deleted robot, region or operation - so a stale
    /// layout cannot resurrect a group around rows that are gone.
    /// </summary>
    public void Prune(IReadOnlyCollection<string> live)
    {
        var keep = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
        Order.RemoveAll(k => !keep.Contains(k));
        foreach (var group in Groups)
            group.Members.RemoveAll(k => !keep.Contains(k));
        Groups.RemoveAll(g => g.Members.Count < 2);
    }

    /// <summary>Follows a row key that changed, as happens when a robot is renamed.</summary>
    public void Rekey(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return;

        for (var i = 0; i < Order.Count; i++)
            if (string.Equals(Order[i], from, StringComparison.OrdinalIgnoreCase))
                Order[i] = to;

        foreach (var group in Groups)
            for (var i = 0; i < group.Members.Count; i++)
                if (string.Equals(group.Members[i], from, StringComparison.OrdinalIgnoreCase))
                    group.Members[i] = to;
    }
}
