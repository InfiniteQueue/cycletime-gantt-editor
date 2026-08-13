using System.Windows;
using CellGanttChartEditor2.Models;

namespace CellGanttChartEditor2.Dialogs;

/// <summary>What the operation dialog was asked to go and fetch from the image.</summary>
public enum OperationPickRequest
{
    None,

    /// <summary>Draw a box on the image, or click a region already on it.</summary>
    Region,

    /// <summary>Click the spot where the robot sits.</summary>
    RobotLocation,
}

/// <summary>
/// An operation part way through being defined. It outlives the dialog because picking something on
/// the image means getting the dialog out of the way: the dialog closes, the host runs the pick, and
/// a new dialog opens on the same draft with the answer filled in.
/// </summary>
public sealed class OperationDraft
{
    /// <summary>The region chosen, if it is one that already exists.</summary>
    public Guid? RegionId { get; set; }

    /// <summary>True when a new region is being defined along with the operation.</summary>
    public bool NewRegion { get; set; }

    /// <summary>The new region's area. Empty is allowed - a region need not be on the image.</summary>
    public Rect NewRegionBounds { get; set; }

    public string NewRegionName { get; set; } = string.Empty;

    /// <summary>The new region's category, if one is being defined.</summary>
    public RegionCategory Category { get; set; } = RegionCategory.Uncategorised;

    /// <summary>What kind of work the operation itself is. Nothing to do with the region's.</summary>
    public OperationCategory OperationCategory { get; set; } = OperationCategory.Uncategorised;

    public string RobotName { get; set; } = string.Empty;

    /// <summary>Where to put the robot on the image, if the user asked to place it.</summary>
    public Point? RobotLocation { get; set; }

    public string OperationName { get; set; } = string.Empty;

    // Kept as text, so a half-typed number survives a trip out to the image and back.
    public string Start { get; set; } = "0";

    public string Duration { get; set; } = "5";
    public string Notes { get; set; } = string.Empty;
}
