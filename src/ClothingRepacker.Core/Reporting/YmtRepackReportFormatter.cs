using System.Text;

namespace ClothingRepacker.Core.Reporting;

public sealed class YmtRepackReportFormatter
{
    public string Format(YmtRepackReport report)
    {
        var segments = report.Targets
            .SelectMany(target => target.Lanes)
            .SelectMany(lane => lane.UsedSegments)
            .OrderBy(segment => segment.TargetFullCollection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(segment => segment.Kind)
            .ThenBy(segment => segment.SlotId)
            .ThenBy(segment => segment.NewStartIndex)
            .ToList();

        if (segments.Count == 0)
        {
            return "No YMT repack mappings.";
        }

        var rows = segments.Select(segment => new ReportRow(
            Target: segment.TargetFullCollection,
            Kind: segment.Kind == YmtRepackLaneKind.Component ? "component" : "prop",
            Slot: $"{segment.SlotId} {segment.SlotName}",
            SourceResource: segment.SourceResource,
            SourceYmt: segment.SourceYmtPath,
            OldRange: segment.OldRange,
            NewRange: segment.NewRange,
            Count: segment.Count.ToString())).ToList();

        var widths = new[]
        {
            Math.Max("Target".Length, rows.Max(row => row.Target.Length)),
            Math.Max("Kind".Length, rows.Max(row => row.Kind.Length)),
            Math.Max("Slot".Length, rows.Max(row => row.Slot.Length)),
            Math.Max("Source Resource".Length, rows.Max(row => row.SourceResource.Length)),
            Math.Max("Source YMT".Length, rows.Max(row => row.SourceYmt.Length)),
            Math.Max("Old Range".Length, rows.Max(row => row.OldRange.Length)),
            Math.Max("New Range".Length, rows.Max(row => row.NewRange.Length)),
            Math.Max("Count".Length, rows.Max(row => row.Count.Length)),
        };

        var sb = new StringBuilder();
        AppendRow(sb, widths, "Target", "Kind", "Slot", "Source Resource", "Source YMT", "Old Range", "New Range", "Count");
        AppendSeparator(sb, widths);
        foreach (var row in rows)
        {
            AppendRow(sb, widths, row.Target, row.Kind, row.Slot, row.SourceResource, row.SourceYmt, row.OldRange, row.NewRange, row.Count);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendSeparator(StringBuilder sb, IReadOnlyList<int> widths)
    {
        for (var index = 0; index < widths.Count; index++)
        {
            if (index > 0)
            {
                sb.Append("-+-");
            }

            sb.Append('-', widths[index]);
        }

        sb.AppendLine();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<int> widths, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                sb.Append(" | ");
            }

            sb.Append(values[index].PadRight(widths[index]));
        }

        sb.AppendLine();
    }

    private sealed record ReportRow(
        string Target,
        string Kind,
        string Slot,
        string SourceResource,
        string SourceYmt,
        string OldRange,
        string NewRange,
        string Count);
}
