using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Reporting;

public enum YmtRepackLaneKind
{
    Component = 0,
    Prop = 1,
}

public sealed record YmtRepackReport(
    IReadOnlyList<YmtRepackTarget> Targets,
    IReadOnlyList<YmtRepackSource> Sources,
    IReadOnlyList<YmtRepackCreatureMetadataSource> CreatureMetadataSources,
    IReadOnlyList<YmtRepackCreatureMetadataTarget> CreatureMetadataTargets)
{
    public int SegmentCount => Targets.SelectMany(target => target.Lanes).SelectMany(lane => lane.UsedSegments).Count();
}

public sealed record YmtRepackSource(
    string Resource,
    string YmtPath,
    string CollectionName,
    string FullCollectionName);

public sealed record YmtRepackCreatureMetadataSource(
    string Resource,
    string MetadataPath,
    int ShaderVariableComponentCount,
    int ComponentExpressionCount,
    int PropExpressionCount,
    IReadOnlyList<string> SourceYmtPaths);

public sealed record YmtRepackCreatureMetadataTarget(
    string TargetCollection,
    string TargetFullCollection,
    IReadOnlyList<string> SourceMetadataPaths,
    IReadOnlyList<string> SourceYmtPaths,
    bool HasRepairHints,
    bool IsRequired,
    string? OutputName,
    string? OutputYmtPath)
{
    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputName);
    public bool IsUnnecessaryOutput => HasOutput && !IsRequired;
    public bool IsMissingOutput => IsRequired && !HasOutput;
}

public sealed record YmtRepackTarget(
    string CollectionName,
    string FullCollectionName,
    PedGender Gender,
    string OutputYmtPath,
    IReadOnlyList<YmtRepackLane> Lanes);

public sealed record YmtRepackLane(
    YmtRepackLaneKind Kind,
    int SlotId,
    string SlotName,
    int Capacity,
    int UsedCount,
    IReadOnlyList<YmtRepackSegment> Segments)
{
    public IReadOnlyList<YmtRepackSegment> UsedSegments => Segments.Where(segment => !segment.IsFree).ToList();
    public int FreeCount => Math.Max(0, Capacity - UsedCount);
}

public sealed record YmtRepackSegment(
    string TargetCollection,
    string TargetFullCollection,
    string TargetYmtPath,
    YmtRepackLaneKind Kind,
    int SlotId,
    string SlotName,
    string SourceResource,
    string SourceYmtPath,
    string SourceCollection,
    string SourceFullCollection,
    int OldStartIndex,
    int OldEndIndex,
    int NewStartIndex,
    int NewEndIndex,
    int Count,
    int LaneCapacity,
    bool IsFree)
{
    public string OldRange => FormatRange(OldStartIndex, OldEndIndex);
    public string NewRange => FormatRange(NewStartIndex, NewEndIndex);

    private static string FormatRange(int start, int end)
        => start < 0 || end < 0
            ? string.Empty
            : start == end
                ? start.ToString()
                : $"{start}-{end}";
}
