using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CellGanttChartEditor2.Models;

namespace CellGanttChartEditor2.Services;

/// <summary>
/// Reads and writes the ".cgc" chart file. The reference image is embedded, so a saved chart is
/// self-contained.
/// </summary>
public static class DocumentFile
{
    public const string Extension = ".cgc";
    public const string Filter = "Cell Gantt chart (*.cgc)|*.cgc|All files (*.*)|*.*";

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Save(GanttDocument document, string path)
    {
        var dto = new DocumentDto
        {
            Version = CurrentVersion,
            CycleTime = document.CycleTime,
            ImageFileName = document.ImageFileName,
            ImageData = document.ImageData is { Length: > 0 } ? Convert.ToBase64String(document.ImageData) : null,
            Regions = document.Regions.Select(r => new RegionDto
            {
                Id = r.Id,
                Name = r.Name,
                X = r.Bounds.X,
                Y = r.Bounds.Y,
                Width = r.Bounds.Width,
                Height = r.Bounds.Height,
                Category = r.Category.ToString(),
            }).ToList(),
            Operations = document.Operations.Select(o => new OperationDto
            {
                Id = o.Id,
                Name = o.Name,
                RobotName = o.RobotName,
                RegionId = o.RegionId,
                Start = o.Start,
                Duration = o.Duration,
            }).ToList(),
            Links = document.Links.Select(l => new LinkDto
            {
                Id = l.Id,
                SourceId = l.SourceId,
                TargetId = l.TargetId,
                Lag = l.Lag,
            }).ToList(),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(dto, Options));
    }

    public static GanttDocument Load(string path)
    {
        var dto = JsonSerializer.Deserialize<DocumentDto>(File.ReadAllText(path), Options)
                  ?? throw new InvalidDataException("The file did not contain a chart.");

        var document = new GanttDocument
        {
            CycleTime = dto.CycleTime <= 0 ? 60 : dto.CycleTime,
            ImageFileName = dto.ImageFileName,
            ImageData = string.IsNullOrEmpty(dto.ImageData) ? null : Convert.FromBase64String(dto.ImageData),
        };

        foreach (var r in dto.Regions)
        {
            document.Regions.Add(new ChartRegion
            {
                Id = r.Id,
                Name = r.Name ?? string.Empty,
                Bounds = new Rect(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height)),
                Category = Enum.TryParse<RegionCategory>(r.Category, out var category)
                    ? category
                    : RegionCategory.Uncategorised,
            });
        }

        foreach (var o in dto.Operations)
        {
            document.Operations.Add(new Operation
            {
                Id = o.Id,
                Name = o.Name ?? string.Empty,
                RobotName = o.RobotName ?? string.Empty,
                RegionId = o.RegionId,
                Start = o.Start,
                Duration = Math.Max(0, o.Duration),
            });
        }

        foreach (var l in dto.Links)
        {
            // Skip links whose endpoints did not survive.
            if (document.FindOperation(l.SourceId) == null || document.FindOperation(l.TargetId) == null)
                continue;
            document.Links.Add(new OperationLink
            {
                Id = l.Id,
                SourceId = l.SourceId,
                TargetId = l.TargetId,
                Lag = l.Lag,
            });
        }

        document.PruneOrphans();
        return document;
    }

    private sealed class DocumentDto
    {
        public int Version { get; set; }
        public double CycleTime { get; set; }
        public string? ImageFileName { get; set; }
        public string? ImageData { get; set; }
        public List<RegionDto> Regions { get; set; } = new();
        public List<OperationDto> Operations { get; set; } = new();
        public List<LinkDto> Links { get; set; } = new();
    }

    private sealed class RegionDto
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string? Category { get; set; }
    }

    private sealed class OperationDto
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? RobotName { get; set; }
        public Guid RegionId { get; set; }
        public double Start { get; set; }
        public double Duration { get; set; }
    }

    private sealed class LinkDto
    {
        public Guid Id { get; set; }
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public double Lag { get; set; }
    }
}
