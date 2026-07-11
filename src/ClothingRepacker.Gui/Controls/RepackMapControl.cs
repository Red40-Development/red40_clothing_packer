using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClothingRepacker.Core.Reporting;

namespace ClothingRepacker.Gui.Controls;

public sealed class RepackMapControl : Control
{
    public static readonly StyledProperty<YmtRepackReport?> ReportProperty =
        AvaloniaProperty.Register<RepackMapControl, YmtRepackReport?>(nameof(Report));

    private const double MinimumMapWidth = 980;
    private const double MarginSize = 12;
    private const double LabelWidth = 245;
    private const double LaneHeight = 18;
    private const double LaneGap = 7;
    private const double TargetHeaderHeight = 24;
    private const double TargetGap = 16;
    private const double LegendRowHeight = 18;
    private const double LegendColumnWidth = 360;
    private const double MetadataRowHeight = 34;

    private static readonly IBrush BackgroundBrush = Brush.Parse("#F8FAFC");
    private static readonly IBrush TextBrush = Brush.Parse("#1A202C");
    private static readonly IBrush MutedTextBrush = Brush.Parse("#4A5568");
    private static readonly IBrush RequiredMetadataBrush = Brush.Parse("#2F855A");
    private static readonly IBrush UnnecessaryMetadataBrush = Brush.Parse("#C53030");
    private static readonly IBrush MissingMetadataBrush = Brush.Parse("#B7791F");
    private static readonly IBrush LaneBackgroundBrush = Brush.Parse("#EDF2F7");
    private static readonly IBrush FreeBrush = Brush.Parse("#E2E8F0");
    private static readonly IPen LaneBorderPen = new Pen(Brush.Parse("#CBD5E0"), 1);
    private static readonly IPen SegmentBorderPen = new Pen(Brush.Parse("#FFFFFF"), 1);
    private static readonly IPen HighlightPen = new Pen(Brush.Parse("#111827"), 2);
    private static readonly string[] Palette =
    [
        "#2B6CB0",
        "#2F855A",
        "#B7791F",
        "#C53030",
        "#0F766E",
        "#6B46C1",
        "#B83280",
        "#4A5568",
        "#DD6B20",
        "#3182CE",
    ];

    private readonly List<(Rect Rect, YmtRepackSegment Segment)> _hitRegions = [];
    private YmtRepackSegment? _hoveredSegment;

    public RepackMapControl()
    {
        ClipToBounds = true;
    }

    public YmtRepackReport? Report
    {
        get => GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ReportProperty)
        {
            _hoveredSegment = null;
            _hitRegions.Clear();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? MinimumMapWidth
            : Math.Max(MinimumMapWidth, availableSize.Width);
        return new Size(width, EstimateHeight());
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _hitRegions.Clear();

        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        if (Report is not { } report || (report.Targets.Count == 0 && report.CreatureMetadataTargets.Count == 0))
        {
            DrawText(context, "Analyze resources to see the YMT repack map.", new Point(MarginSize, MarginSize), 13, MutedTextBrush);
            return;
        }

        var sourceBrushes = BuildSourceBrushes(report);
        var y = MarginSize;
        y = DrawLegend(context, report, sourceBrushes, y, Math.Max(MinimumMapWidth, bounds.Width));
        var mapLeft = MarginSize + LabelWidth;
        var mapWidth = Math.Max(420, Math.Max(MinimumMapWidth, bounds.Width) - mapLeft - MarginSize);

        foreach (var target in report.Targets)
        {
            DrawText(context, $"{target.FullCollectionName} -> {target.OutputYmtPath}", new Point(MarginSize, y), 13, TextBrush);
            y += TargetHeaderHeight;

            foreach (var lane in target.Lanes)
            {
                var laneLabel = $"{KindLabel(lane.Kind)} {lane.SlotId} {lane.SlotName}  {lane.UsedCount}/{lane.Capacity}";
                DrawText(context, laneLabel, new Point(MarginSize, y - 1), 12, MutedTextBrush);
                var laneRect = new Rect(mapLeft, y, mapWidth, LaneHeight);
                context.DrawRectangle(LaneBackgroundBrush, LaneBorderPen, laneRect, 2, 2);

                foreach (var segment in lane.Segments)
                {
                    var segmentRect = SegmentRect(laneRect, segment, lane.Capacity);
                    if (segmentRect.Width <= 0)
                    {
                        continue;
                    }

                    var brush = segment.IsFree
                        ? FreeBrush
                        : sourceBrushes.GetValueOrDefault(SourceKey(segment), Brush.Parse(Palette[0]));
                    var pen = Equals(segment, _hoveredSegment) ? HighlightPen : SegmentBorderPen;
                    context.DrawRectangle(brush, pen, segmentRect, 1.5, 1.5);
                    _hitRegions.Add((segmentRect, segment));
                }

                y += LaneHeight + LaneGap;
            }

            y += TargetGap;
        }

        y = DrawCreatureMetadata(context, report, y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var hit = _hitRegions.FirstOrDefault(region => region.Rect.Contains(point)).Segment;
        if (Equals(hit, _hoveredSegment))
        {
            return;
        }

        _hoveredSegment = hit;
        ToolTip.SetTip(this, hit is null ? null : BuildTip(hit));
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoveredSegment = null;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    private double EstimateHeight()
    {
        if (Report is not { } report)
        {
            return 80;
        }

        var legendRows = Math.Max(1, (int)Math.Ceiling(report.Sources.Count / 2.0));
        var targetHeight = report.Targets.Sum(target => TargetHeaderHeight + target.Lanes.Count * (LaneHeight + LaneGap) + TargetGap);
        var metadataHeight = report.CreatureMetadataTargets.Count == 0
            ? 0
            : TargetHeaderHeight + report.CreatureMetadataTargets.Count * MetadataRowHeight + TargetGap;
        return MarginSize * 2 + TargetHeaderHeight + legendRows * LegendRowHeight + TargetGap + targetHeight + metadataHeight;
    }

    private static double DrawLegend(
        DrawingContext context,
        YmtRepackReport report,
        IReadOnlyDictionary<string, IBrush> sourceBrushes,
        double y,
        double width)
    {
        DrawText(context, "Source legend", new Point(MarginSize, y), 13, TextBrush);
        y += TargetHeaderHeight;
        var columns = Math.Max(1, (int)((width - MarginSize * 2) / LegendColumnWidth));
        columns = Math.Min(columns, 3);
        for (var index = 0; index < report.Sources.Count; index++)
        {
            var source = report.Sources[index];
            var column = index % columns;
            var row = index / columns;
            var x = MarginSize + column * LegendColumnWidth;
            var itemY = y + row * LegendRowHeight;
            var key = SourceKey(source.Resource, source.YmtPath);
            var swatchRect = new Rect(x, itemY + 3, 12, 12);
            context.DrawRectangle(sourceBrushes.GetValueOrDefault(key, Brush.Parse(Palette[0])), null, swatchRect, 2, 2);
            DrawText(context, $"{source.Resource} | {Path.GetFileName(source.YmtPath)}", new Point(x + 18, itemY), 11, MutedTextBrush);
        }

        var rows = Math.Max(1, (int)Math.Ceiling(report.Sources.Count / (double)columns));
        return y + rows * LegendRowHeight + TargetGap;
    }

    private static double DrawCreatureMetadata(DrawingContext context, YmtRepackReport report, double y)
    {
        if (report.CreatureMetadataTargets.Count == 0)
        {
            return y;
        }

        DrawText(context, "Creature metadata", new Point(MarginSize, y), 13, TextBrush);
        y += TargetHeaderHeight;
        foreach (var target in report.CreatureMetadataTargets)
        {
            var status = target.IsUnnecessaryOutput
                ? "UNNECESSARY OUTPUT"
                : target.IsMissingOutput
                    ? "MISSING OUTPUT"
                    : target.IsRequired
                        ? "required"
                        : "not required";
            var brush = target.IsUnnecessaryOutput
                ? UnnecessaryMetadataBrush
                : target.IsMissingOutput
                    ? MissingMetadataBrush
                    : target.IsRequired
                        ? RequiredMetadataBrush
                        : MutedTextBrush;
            var output = target.OutputName is null ? "no output" : target.OutputName;
            var source = target.SourceMetadataPaths.Count == 0
                ? "no source metadata"
                : $"{target.SourceMetadataPaths.Count} source metadata file(s)";
            var sourceYmts = target.SourceYmtPaths.Count == 0
                ? string.Empty
                : $" | YMTs: {string.Join(", ", target.SourceYmtPaths.Select(Path.GetFileName))}";
            var repair = target.HasRepairHints ? " + repair hints" : string.Empty;
            DrawText(
                context,
                $"{target.TargetFullCollection}  {status}  |  {output}  |  {source}{sourceYmts}{repair}",
                new Point(MarginSize, y),
                12,
                brush);
            if (!string.IsNullOrWhiteSpace(target.OutputYmtPath))
            {
                DrawText(context, $"Output: {target.OutputYmtPath}", new Point(MarginSize + 18, y + 15), 10, MutedTextBrush);
            }

            y += MetadataRowHeight;
        }

        return y + TargetGap;
    }

    private static Rect SegmentRect(Rect laneRect, YmtRepackSegment segment, int capacity)
    {
        if (capacity <= 0)
        {
            return default;
        }

        var startRatio = Math.Clamp((double)segment.NewStartIndex / capacity, 0, 1);
        var widthRatio = Math.Clamp((double)segment.Count / capacity, 0, 1);
        var x = laneRect.X + laneRect.Width * startRatio;
        var width = Math.Max(1, laneRect.Width * widthRatio);
        if (x + width > laneRect.Right)
        {
            width = laneRect.Right - x;
        }

        return new Rect(x, laneRect.Y, Math.Max(0, width), laneRect.Height);
    }

    private static IReadOnlyDictionary<string, IBrush> BuildSourceBrushes(YmtRepackReport report)
    {
        var result = new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < report.Sources.Count; index++)
        {
            var source = report.Sources[index];
            result[SourceKey(source.Resource, source.YmtPath)] = Brush.Parse(Palette[index % Palette.Length]);
        }

        foreach (var segment in report.Targets.SelectMany(target => target.Lanes).SelectMany(lane => lane.UsedSegments))
        {
            var key = SourceKey(segment);
            if (!result.ContainsKey(key))
            {
                result[key] = Brush.Parse(Palette[result.Count % Palette.Length]);
            }
        }

        return result;
    }

    private static string BuildTip(YmtRepackSegment segment)
    {
        if (segment.IsFree)
        {
            return $"{segment.TargetFullCollection}\n{KindLabel(segment.Kind)} {segment.SlotId} {segment.SlotName}\nFree capacity: {segment.Count}";
        }

        return $"{segment.TargetFullCollection}\n"
               + $"{KindLabel(segment.Kind)} {segment.SlotId} {segment.SlotName}\n"
               + $"Source: {segment.SourceResource}\n"
               + $"{segment.SourceYmtPath}\n"
               + $"Old {segment.OldRange} -> New {segment.NewRange} ({segment.Count})";
    }

    private static string KindLabel(YmtRepackLaneKind kind)
        => kind == YmtRepackLaneKind.Component ? "component" : "prop";

    private static string SourceKey(YmtRepackSegment segment)
        => SourceKey(segment.SourceResource, segment.SourceYmtPath);

    private static string SourceKey(string resource, string ymtPath)
        => $"{resource}\n{ymtPath}";

    private static void DrawText(DrawingContext context, string text, Point point, double size, IBrush brush)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal),
            size,
            brush);
        context.DrawText(formatted, point);
    }
}
