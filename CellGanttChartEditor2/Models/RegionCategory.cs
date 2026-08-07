namespace CellGanttChartEditor2.Models;

/// <summary>
/// Category assigned to a region when it is first defined.
/// PLACEHOLDER VALUES - replace the members below with the real category set.
/// Display text comes from <see cref="RegionCategoryInfo.Display"/>.
/// </summary>
public enum RegionCategory
{
    Uncategorised = 0,
    PlaceholderCategoryA = 1,
    PlaceholderCategoryB = 2,
    PlaceholderCategoryC = 3,
    PlaceholderCategoryD = 4,
}

public static class RegionCategoryInfo
{
    /// <summary>Human readable name for a category. Update alongside the enum.</summary>
    public static string Display(RegionCategory category) => category switch
    {
        RegionCategory.Uncategorised => "Uncategorised",
        RegionCategory.PlaceholderCategoryA => "Placeholder category A",
        RegionCategory.PlaceholderCategoryB => "Placeholder category B",
        RegionCategory.PlaceholderCategoryC => "Placeholder category C",
        RegionCategory.PlaceholderCategoryD => "Placeholder category D",
        _ => category.ToString(),
    };

    public static IReadOnlyList<CategoryOption> Options { get; } =
        Enum.GetValues<RegionCategory>().Select(c => new CategoryOption(c, Display(c))).ToList();
}

public sealed record CategoryOption(RegionCategory Value, string Text)
{
    public override string ToString() => Text;
}
