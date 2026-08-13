namespace CellGanttChartEditor2.Models;

/// <summary>
/// What kind of work an operation is. Independent of the region it happens in: the same fixture may
/// be welded at in one operation and inspected in the next.
/// PLACEHOLDER VALUES - replace the members below with the real category set.
/// Display text comes from <see cref="OperationCategoryInfo.Display"/>.
/// </summary>
public enum OperationCategory
{
    Uncategorised = 0,
    Weld = 1,
    Handling = 2,
    TipDress = 3,
    Inspection = 4,
    Wait = 5,
}

public static class OperationCategoryInfo
{
    /// <summary>Human readable name for a category. Update alongside the enum.</summary>
    public static string Display(OperationCategory category) => category switch
    {
        OperationCategory.Uncategorised => "Uncategorised",
        OperationCategory.Weld => "Weld",
        OperationCategory.Handling => "Handling",
        OperationCategory.TipDress => "Tip Dress",
        OperationCategory.Inspection => "Inspection",
        OperationCategory.Wait => "Wait",
        _ => category.ToString(),
    };

    public static IReadOnlyList<OperationCategoryOption> Options { get; } =
        Enum.GetValues<OperationCategory>().Select(c => new OperationCategoryOption(c, Display(c))).ToList();

    /// <summary>
    /// The key a category's colour is allocated against. Categories are a fixed set rather than
    /// named things, so the name of the member is a stable key without needing an id.
    /// </summary>
    public static string ColorKey(OperationCategory category) => category.ToString();
}

public sealed record OperationCategoryOption(OperationCategory Value, string Text)
{
    public override string ToString() => Text;
}
