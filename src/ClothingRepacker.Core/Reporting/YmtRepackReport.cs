using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Reporting;

public enum YmtRepackLaneKind
{
    Component = 0,
    Prop = 1,
}

public sealed record YmtRepackReport(
    IReadOnlyList<YmtRepackTarget> Targets,
    IReadOnlyList<YmtRepackSource> Sources)
{
    public int SegmentCount => Targets.SelectMany(target => target.Lanes).SelectMany(lane => lane.UsedSegments).Count();
}

public sealed record YmtRepackSource(
    string Resource,
    string YmtPath,
    string CollectionName,
    string FullCollectionName);

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
