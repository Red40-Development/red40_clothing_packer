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

        var sb = new StringBuilder();
        if (segments.Count == 0)
        {
            sb.AppendLine("No YMT repack mappings.");
        }
        else
        {
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

            AppendRow(sb, widths, "Target", "Kind", "Slot", "Source Resource", "Source YMT", "Old Range", "New Range", "Count");
            AppendSeparator(sb, widths);
            foreach (var row in rows)
            {
                AppendRow(sb, widths, row.Target, row.Kind, row.Slot, row.SourceResource, row.SourceYmt, row.OldRange, row.NewRange, row.Count);
            }
        }

        AppendCreatureMetadataSection(sb, report);

        return sb.ToString().TrimEnd();
    }

    private static void AppendCreatureMetadataSection(StringBuilder sb, YmtRepackReport report)
    {
        sb.AppendLine();
        sb.AppendLine("Creature metadata sources");
        if (report.CreatureMetadataSources.Count == 0)
        {
            sb.AppendLine("  None detected.");
        }
        else
        {
            foreach (var source in report.CreatureMetadataSources)
            {
                sb.Append("  ")
                    .Append(source.Resource)
                    .Append(" | ")
                    .Append(source.MetadataPath)
                    .Append(" | YMTs: ")
                    .Append(source.SourceYmtPaths.Count)
                    .Append(" [")
                    .Append(string.Join(", ", source.SourceYmtPaths))
                    .Append("]")
                    .Append(" | expressions: ")
                    .Append(source.ComponentExpressionCount)
                    .Append(" component, ")
                    .Append(source.PropExpressionCount)
                    .AppendLine(" prop");
            }
        }

        sb.AppendLine("Creature metadata targets");
        if (report.CreatureMetadataTargets.Count == 0)
        {
            sb.AppendLine("  None planned.");
            return;
        }

        foreach (var target in report.CreatureMetadataTargets)
        {
            var status = target.IsUnnecessaryOutput
                ? "UNNECESSARY OUTPUT"
                : target.IsMissingOutput
                    ? "MISSING OUTPUT"
                    : target.IsRequired
                        ? "required"
                        : "not required";
            var output = target.OutputName is null
                ? "output: none"
                : $"output: {target.OutputName} [{target.OutputYmtPath}]";
            var sources = target.SourceMetadataPaths.Count == 0
                ? "source metadata: none"
                : $"source metadata: {string.Join(", ", target.SourceMetadataPaths)}";
            var repair = target.HasRepairHints ? ", repair hints: yes" : string.Empty;
            sb.Append("  ")
                .Append(target.TargetFullCollection)
                .Append(" | ")
                .Append(status)
                .Append(" | ")
                .Append(output)
                .Append(" | ")
                .Append(sources)
                .AppendLine(repair);
        }
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
