namespace CellGanttChartEditor2.Models;

/// <summary>
/// Category assigned to a region when it is first defined.
/// PLACEHOLDER VALUES - replace the members below with the real category set.
/// Display text comes from <see cref="RegionCategoryInfo.Display"/>.
/// </summary>
public enum RegionCategory
{
    Uncategorised = 0,
    GeoStation = 1,
    Sealer = 2,
    TipChanger = 3,
    WeldStand = 4,
}

public static class RegionCategoryInfo
{
    /// <summary>Human readable name for a category. Update alongside the enum.</summary>
    public static string Display(RegionCategory category) => category switch
    {
        RegionCategory.Uncategorised => "Uncategorised",
        RegionCategory.GeoStation => "Geo Station",
        RegionCategory.Sealer => "Sealer",
        RegionCategory.TipChanger => "Tip Changer",
        RegionCategory.WeldStand => "Weld Stand",
        _ => category.ToString(),
    };

    public static IReadOnlyList<CategoryOption> Options { get; } =
        Enum.GetValues<RegionCategory>().Select(c => new CategoryOption(c, Display(c))).ToList();

    /// <summary>
    /// The key a category's colour is allocated against. These are a fixed set rather than named
    /// things, so the name of the member is a stable key without needing an id.
    /// </summary>
    public static string ColorKey(RegionCategory category) => category.ToString();
}

public sealed record CategoryOption(RegionCategory Value, string Text)
{
    public override string ToString() => Text;
}
