using System.Text;
using ClothingRepacker.Core.Localization;

namespace ClothingRepacker.Core.Reporting;

public sealed class YmtRepackReportFormatter
{
    private readonly LocalizationService _localization;

    public YmtRepackReportFormatter(LocalizationService? localization = null)
    {
        _localization = localization ?? new LocalizationService();
    }

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
            sb.AppendLine(T("report.noMappings"));
        }
        else
        {
            var rows = segments.Select(segment => new ReportRow(
                Target: segment.TargetFullCollection,
                Kind: segment.Kind == YmtRepackLaneKind.Component ? T("report.component") : T("report.prop"),
                Slot: $"{segment.SlotId} {segment.SlotName}",
                SourceResource: segment.SourceResource,
                SourceYmt: segment.SourceYmtPath,
                OldRange: segment.OldRange,
                NewRange: segment.NewRange,
                Count: segment.Count.ToString())).ToList();

            var widths = new[]
            {
                Math.Max(T("report.target").Length, rows.Max(row => row.Target.Length)),
                Math.Max(T("report.kind").Length, rows.Max(row => row.Kind.Length)),
                Math.Max(T("report.slot").Length, rows.Max(row => row.Slot.Length)),
                Math.Max(T("report.sourceResource").Length, rows.Max(row => row.SourceResource.Length)),
                Math.Max(T("report.sourceYmt").Length, rows.Max(row => row.SourceYmt.Length)),
                Math.Max(T("report.oldRange").Length, rows.Max(row => row.OldRange.Length)),
                Math.Max(T("report.newRange").Length, rows.Max(row => row.NewRange.Length)),
                Math.Max(T("report.count").Length, rows.Max(row => row.Count.Length)),
            };

            AppendRow(sb, widths, T("report.target"), T("report.kind"), T("report.slot"), T("report.sourceResource"), T("report.sourceYmt"), T("report.oldRange"), T("report.newRange"), T("report.count"));
            AppendSeparator(sb, widths);
            foreach (var row in rows)
            {
                AppendRow(sb, widths, row.Target, row.Kind, row.Slot, row.SourceResource, row.SourceYmt, row.OldRange, row.NewRange, row.Count);
            }
        }

        AppendCreatureMetadataSection(sb, report);

        return sb.ToString().TrimEnd();
    }

    private void AppendCreatureMetadataSection(StringBuilder sb, YmtRepackReport report)
    {
        sb.AppendLine();
        sb.AppendLine(T("report.creatureMetadataSources"));
        if (report.CreatureMetadataSources.Count == 0)
        {
            sb.AppendLine($"  {T("report.noneDetected")}");
        }
        else
        {
            foreach (var source in report.CreatureMetadataSources)
            {
                sb.Append("  ")
                    .Append(source.Resource)
                    .Append(" | ")
                    .Append(source.MetadataPath)
                    .Append(" | ")
                    .Append(T("report.ymts"))
                    .Append(": ")
                    .Append(source.SourceYmtPaths.Count)
                    .Append(" [")
                    .Append(string.Join(", ", source.SourceYmtPaths))
                    .Append("]")
                    .Append(" | ")
                    .Append(T("report.expressions"))
                    .Append(": ")
                    .Append(source.ComponentExpressionCount)
                    .Append(' ')
                    .Append(T("report.component"))
                    .Append(", ")
                    .Append(source.PropExpressionCount)
                    .Append(' ')
                    .AppendLine(T("report.prop"));
            }
        }

        sb.AppendLine(T("report.creatureMetadataTargets"));
        if (report.CreatureMetadataTargets.Count == 0)
        {
            sb.AppendLine($"  {T("report.none")}");
            return;
        }

        foreach (var target in report.CreatureMetadataTargets)
        {
            var status = target.IsUnnecessaryOutput
                ? T("map.unnecessaryOutput")
                : target.IsMissingOutput
                    ? T("map.missingOutput")
                    : target.IsRequired
                        ? T("map.required")
                        : T("map.notRequired");
            var output = target.OutputName is null
                ? T("report.outputNone")
                : T("report.output", new Dictionary<string, object?> { ["name"] = target.OutputName, ["path"] = target.OutputYmtPath });
            var sources = target.SourceMetadataPaths.Count == 0
                ? T("report.sourceMetadataNone")
                : T("report.sourceMetadata", new Dictionary<string, object?> { ["paths"] = string.Join(", ", target.SourceMetadataPaths) });
            var repair = target.HasRepairHints ? T("report.repairHints") : string.Empty;
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

    private string T(string key, IReadOnlyDictionary<string, object?>? arguments = null)
        => _localization.Translate(key, arguments);
}
