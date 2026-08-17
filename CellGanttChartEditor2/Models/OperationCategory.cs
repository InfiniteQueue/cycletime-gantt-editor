using CellGanttChartEditor2.Services;

namespace CellGanttChartEditor2.Models;

/// <summary>
/// What kind of work an operation is. Independent of the region it happens in: the same fixture may
/// be welded at in one operation and inspected in the next.
///
/// This is free text rather than a fixed set, because the work a cell does is not something this
/// editor can enumerate in advance. <see cref="Presets"/> is what the dropdowns offer before the
/// document has anything of its own to suggest; typing a name that is not among them makes it a
/// category like any other. Region categories are still an enum - those name the kinds of place a
/// cell has, which is a much shorter and steadier list.
/// </summary>
public static class OperationCategories
{
    /// <summary>
    /// What an operation is in until it is put somewhere else, and what a blank field means. Every
    /// operation has a category, so this is a name like any other rather than the absence of one -
    /// it takes a colour and it is offered as a filter chip.
    /// </summary>
    public const string Uncategorised = "Uncategorised";

    /// <summary>
    /// PLACEHOLDER VALUES - the suggestions offered before the document supplies its own. Replace
    /// these with the real ones; nothing breaks if a saved file names a category not listed here.
    /// </summary>
    public static IReadOnlyList<string> Presets { get; } = new[]
    {
        Uncategorised,
        "Weld",
        "Handling",
        "Tip Dress",
        "Inspection",
        "Wait",
    };

    /// <summary>The presets, then anything the document uses that is not one of them.</summary>
    public static List<string> Suggestions(GanttDocument? document) =>
        Services.Presets.Combine(Presets, document?.Operations.Select(o => o.Category) ?? []);

    /// <summary>
    /// What a typed category becomes. Blank is <see cref="Uncategorised"/>, surrounding space goes,
    /// and a name differing from a known one only in case takes that one's spelling - so "weld"
    /// typed into one dialog joins the "Weld" that already exists rather than sitting beside it as a
    /// second category that looks identical.
    /// </summary>
    public static string Canonical(string? typed, GanttDocument? document)
    {
        var text = (typed ?? string.Empty).Trim();
        if (text.Length == 0)
            return Uncategorised;

        return Suggestions(document)
                   .FirstOrDefault(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase))
               ?? text;
    }

    /// <summary>
    /// The key a category's colour is allocated against. Case-folded, so a category that slipped
    /// past <see cref="Canonical"/> in some other spelling still draws in the one colour.
    /// </summary>
    public static string ColorKey(string category) => category.Trim().ToLowerInvariant();
}
