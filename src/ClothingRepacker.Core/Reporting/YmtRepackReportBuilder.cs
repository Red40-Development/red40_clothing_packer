using ClothingRepacker.Core;
using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Reporting;

public sealed class YmtRepackReportBuilder
{
    public YmtRepackReport Build(MergePlan plan)
    {
        var sourceLookup = plan.SourceYmts.ToDictionary(source => source.Path, StringComparer.OrdinalIgnoreCase);
        var sources = plan.DrawableMappings
            .Select(mapping => new MappingGroupKey(mapping.SourceResource, mapping.SourceYmtPath, mapping.SourceCollection, mapping.SourceFullCollection))
            .Concat(plan.PropMappings.Select(mapping => new MappingGroupKey(mapping.SourceResource, mapping.SourceYmtPath, mapping.SourceCollection, mapping.SourceFullCollection)))
            .Select(key => SourceFrom(key, sourceLookup))
            .DistinctBy(source => source.YmtPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source.Resource, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.YmtPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var orderedTargetPlans = plan.TargetCollections
            .OrderBy(target => target.FullCollectionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var targets = new List<YmtRepackTarget>();
        foreach (var target in orderedTargetPlans)
        {
            var lanes = new List<YmtRepackLane>();
            lanes.AddRange(BuildComponentLanes(plan, target, sourceLookup));
            lanes.AddRange(BuildPropLanes(plan, target, sourceLookup));

            targets.Add(new YmtRepackTarget(
                target.CollectionName,
                target.FullCollectionName,
                target.Gender,
                target.OutputYmtPath,
                lanes));
        }

        var creatureMetadataSources = plan.SourceCreatureMetadata
            .Select(metadata => new YmtRepackCreatureMetadataSource(
                metadata.Resource,
                metadata.Path,
                metadata.ShaderVariableComponentCount,
                metadata.ComponentExpressionCount,
                metadata.PropExpressionCount,
                metadata.SourceYmts))
            .OrderBy(metadata => metadata.Resource, StringComparer.OrdinalIgnoreCase)
            .ThenBy(metadata => metadata.MetadataPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var creatureMetadataTargets = orderedTargetPlans
            .Select(target => BuildCreatureMetadataTarget(plan, target, creatureMetadataSources))
            .ToList();

        return new YmtRepackReport(targets, sources, creatureMetadataSources, creatureMetadataTargets);
    }

    private static YmtRepackCreatureMetadataTarget BuildCreatureMetadataTarget(
        MergePlan plan,
        TargetCollectionPlan target,
        IReadOnlyList<YmtRepackCreatureMetadataSource> metadataSources)
    {
        var sourceYmtPaths = target.SourceYmts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetMetadataSources = metadataSources
            .Where(metadata => metadata.SourceYmtPaths.Any(sourceYmtPath => sourceYmtPaths.Contains(sourceYmtPath)))
            .ToList();
        var hasRepairHints = plan.SourceYmts
            .Where(source => sourceYmtPaths.Contains(source.Path))
            .Any(source => source.HasCreatureRepairHints);
        var output = plan.CreatureMetadataOutputs
            .FirstOrDefault(output => output.TargetCollections.Contains(target.CollectionName, StringComparer.OrdinalIgnoreCase));
        var isRequired = targetMetadataSources.Count > 0 || hasRepairHints;

        return new YmtRepackCreatureMetadataTarget(
            target.CollectionName,
            target.FullCollectionName,
            targetMetadataSources.Select(metadata => metadata.MetadataPath).ToList(),
            targetMetadataSources.SelectMany(metadata => metadata.SourceYmtPaths)
                .Where(sourceYmtPath => sourceYmtPaths.Contains(sourceYmtPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(sourceYmtPath => sourceYmtPath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            hasRepairHints,
            isRequired,
            output?.Name,
            output?.OutputYmtPath);
    }

    private static IEnumerable<YmtRepackLane> BuildComponentLanes(
        MergePlan plan,
        TargetCollectionPlan target,
        IReadOnlyDictionary<string, SourceYmtSummary> sourceLookup)
    {
        var mappings = plan.DrawableMappings
            .Where(mapping => MappingTargetsCollection(mapping.TargetCollection, mapping.TargetFullCollection, target))
            .ToList();
        var slotIds = target.ComponentCounts
            .Where(item => item.Value > 0)
            .Select(item => item.Key)
            .Concat(mappings.Select(mapping => mapping.ComponentId))
            .Distinct()
            .Order();

        foreach (var slotId in slotIds)
        {
            var usedSegments = BuildDrawableSegments(plan, target, slotId, mappings, sourceLookup).ToList();
            var usedCount = EffectiveUsedCount(target.ComponentCounts.GetValueOrDefault(slotId), usedSegments);
            var segments = AddFreeSegment(target, YmtRepackLaneKind.Component, slotId, SlotName(YmtRepackLaneKind.Component, slotId), plan.Settings.MaxDrawablesPerComponent, usedCount, usedSegments);
            if (segments.Count == 0)
            {
                continue;
            }

            yield return new YmtRepackLane(
                YmtRepackLaneKind.Component,
                slotId,
                SlotName(YmtRepackLaneKind.Component, slotId),
                plan.Settings.MaxDrawablesPerComponent,
                usedCount,
                segments);
        }
    }

    private static IEnumerable<YmtRepackLane> BuildPropLanes(
        MergePlan plan,
        TargetCollectionPlan target,
        IReadOnlyDictionary<string, SourceYmtSummary> sourceLookup)
    {
        var mappings = plan.PropMappings
            .Where(mapping => MappingTargetsCollection(mapping.TargetCollection, mapping.TargetFullCollection, target))
            .ToList();
        var slotIds = target.PropCounts
            .Where(item => item.Value > 0)
            .Select(item => item.Key)
            .Concat(mappings.Select(mapping => mapping.AnchorId))
            .Distinct()
            .Order();

        foreach (var slotId in slotIds)
        {
            var usedSegments = BuildPropSegments(plan, target, slotId, mappings, sourceLookup).ToList();
            var usedCount = EffectiveUsedCount(target.PropCounts.GetValueOrDefault(slotId), usedSegments);
            var segments = AddFreeSegment(target, YmtRepackLaneKind.Prop, slotId, SlotName(YmtRepackLaneKind.Prop, slotId), plan.Settings.MaxDrawablesPerProp, usedCount, usedSegments);
            if (segments.Count == 0)
            {
                continue;
            }

            yield return new YmtRepackLane(
                YmtRepackLaneKind.Prop,
                slotId,
                SlotName(YmtRepackLaneKind.Prop, slotId),
                plan.Settings.MaxDrawablesPerProp,
                usedCount,
                segments);
        }
    }

    private static bool MappingTargetsCollection(string targetCollection, string targetFullCollection, TargetCollectionPlan target)
        => targetCollection.Equals(target.CollectionName, StringComparison.OrdinalIgnoreCase)
           || targetFullCollection.Equals(target.FullCollectionName, StringComparison.OrdinalIgnoreCase);

    private static int EffectiveUsedCount(int plannedUsedCount, IReadOnlyList<YmtRepackSegment> usedSegments)
        => Math.Max(plannedUsedCount, usedSegments.Select(segment => segment.NewEndIndex + 1).DefaultIfEmpty(0).Max());

    private static IEnumerable<YmtRepackSegment> BuildDrawableSegments(
        MergePlan plan,
        TargetCollectionPlan target,
        int componentId,
        IReadOnlyList<DrawableMapping> mappings,
        IReadOnlyDictionary<string, SourceYmtSummary> sourceLookup)
    {
        var groups = mappings
            .Where(mapping => mapping.ComponentId == componentId)
            .GroupBy(mapping => new MappingGroupKey(
                mapping.SourceResource,
                mapping.SourceYmtPath,
                mapping.SourceCollection,
                mapping.SourceFullCollection),
                MappingGroupKey.Comparer);

        foreach (var group in groups)
        {
            var source = SourceFrom(group.Key, sourceLookup);
            foreach (var range in BuildContiguousRanges(
                         group.Select(mapping => new IndexPair(mapping.OldDrawableIndex, mapping.NewDrawableIndex))))
            {
                yield return new YmtRepackSegment(
                    target.CollectionName,
                    target.FullCollectionName,
                    target.OutputYmtPath,
                    YmtRepackLaneKind.Component,
                    componentId,
                    SlotName(YmtRepackLaneKind.Component, componentId),
                    source.Resource,
                    source.YmtPath,
                    source.CollectionName,
                    source.FullCollectionName,
                    range.OldStart,
                    range.OldEnd,
                    range.NewStart,
                    range.NewEnd,
                    range.Count,
                    plan.Settings.MaxDrawablesPerComponent,
                    IsFree: false);
            }
        }
    }

    private static IEnumerable<YmtRepackSegment> BuildPropSegments(
        MergePlan plan,
        TargetCollectionPlan target,
        int anchorId,
        IReadOnlyList<PropMapping> mappings,
        IReadOnlyDictionary<string, SourceYmtSummary> sourceLookup)
    {
        var groups = mappings
            .Where(mapping => mapping.AnchorId == anchorId)
            .GroupBy(mapping => new MappingGroupKey(
                mapping.SourceResource,
                mapping.SourceYmtPath,
                mapping.SourceCollection,
                mapping.SourceFullCollection),
                MappingGroupKey.Comparer);

        foreach (var group in groups)
        {
            var source = SourceFrom(group.Key, sourceLookup);
            foreach (var range in BuildContiguousRanges(
                         group.Select(mapping => new IndexPair(mapping.OldPropIndex, mapping.NewPropIndex))))
            {
                yield return new YmtRepackSegment(
                    target.CollectionName,
                    target.FullCollectionName,
                    target.OutputYmtPath,
                    YmtRepackLaneKind.Prop,
                    anchorId,
                    SlotName(YmtRepackLaneKind.Prop, anchorId),
                    source.Resource,
                    source.YmtPath,
                    source.CollectionName,
                    source.FullCollectionName,
                    range.OldStart,
                    range.OldEnd,
                    range.NewStart,
                    range.NewEnd,
                    range.Count,
                    plan.Settings.MaxDrawablesPerProp,
                    IsFree: false);
            }
        }
    }

    private static IReadOnlyList<YmtRepackSegment> AddFreeSegment(
        TargetCollectionPlan target,
        YmtRepackLaneKind kind,
        int slotId,
        string slotName,
        int capacity,
        int usedCount,
        IReadOnlyList<YmtRepackSegment> usedSegments)
    {
        if (usedCount <= 0 && usedSegments.Count == 0)
        {
            return [];
        }

        var result = usedSegments
            .OrderBy(segment => segment.NewStartIndex)
            .ThenBy(segment => segment.SourceResource, StringComparer.OrdinalIgnoreCase)
            .ThenBy(segment => segment.SourceYmtPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var freeCount = Math.Max(0, capacity - usedCount);
        if (freeCount > 0)
        {
            result.Add(new YmtRepackSegment(
                target.CollectionName,
                target.FullCollectionName,
                target.OutputYmtPath,
                kind,
                slotId,
                slotName,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                -1,
                -1,
                usedCount,
                capacity - 1,
                freeCount,
                capacity,
                IsFree: true));
        }

        return result;
    }

    private static IEnumerable<IndexRange> BuildContiguousRanges(IEnumerable<IndexPair> pairs)
    {
        using var enumerator = pairs
            .OrderBy(pair => pair.NewIndex)
            .ThenBy(pair => pair.OldIndex)
            .GetEnumerator();
        if (!enumerator.MoveNext())
        {
            yield break;
        }

        var oldStart = enumerator.Current.OldIndex;
        var oldEnd = enumerator.Current.OldIndex;
        var newStart = enumerator.Current.NewIndex;
        var newEnd = enumerator.Current.NewIndex;
        var count = 1;

        while (enumerator.MoveNext())
        {
            var pair = enumerator.Current;
            if (pair.OldIndex == oldEnd + 1 && pair.NewIndex == newEnd + 1)
            {
                oldEnd = pair.OldIndex;
                newEnd = pair.NewIndex;
                count++;
                continue;
            }

            yield return new IndexRange(oldStart, oldEnd, newStart, newEnd, count);
            oldStart = pair.OldIndex;
            oldEnd = pair.OldIndex;
            newStart = pair.NewIndex;
            newEnd = pair.NewIndex;
            count = 1;
        }

        yield return new IndexRange(oldStart, oldEnd, newStart, newEnd, count);
    }

    private static YmtRepackSource SourceFrom(MappingGroupKey key, IReadOnlyDictionary<string, SourceYmtSummary> sourceLookup)
    {
        if (sourceLookup.TryGetValue(key.SourceYmtPath, out var source))
        {
            return new YmtRepackSource(source.Resource, source.Path, source.CollectionName, source.FullCollectionName);
        }

        return new YmtRepackSource(key.SourceResource, key.SourceYmtPath, key.SourceCollection, key.SourceFullCollection);
    }

    private static string SlotName(YmtRepackLaneKind kind, int slotId)
        => kind == YmtRepackLaneKind.Component
            ? ClothingConstants.ComponentPrefixes.GetValueOrDefault(slotId, $"component_{slotId}")
            : ClothingConstants.PropPrefixes.GetValueOrDefault(slotId, $"prop_{slotId}");

    private sealed record MappingGroupKey(
        string SourceResource,
        string SourceYmtPath,
        string SourceCollection,
        string SourceFullCollection)
    {
        public static IEqualityComparer<MappingGroupKey> Comparer { get; } = new MappingGroupKeyComparer();

        private sealed class MappingGroupKeyComparer : IEqualityComparer<MappingGroupKey>
        {
            public bool Equals(MappingGroupKey? x, MappingGroupKey? y)
                => x is not null
                   && y is not null
                   && x.SourceResource.Equals(y.SourceResource, StringComparison.OrdinalIgnoreCase)
                   && x.SourceYmtPath.Equals(y.SourceYmtPath, StringComparison.OrdinalIgnoreCase)
                   && x.SourceCollection.Equals(y.SourceCollection, StringComparison.OrdinalIgnoreCase)
                   && x.SourceFullCollection.Equals(y.SourceFullCollection, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(MappingGroupKey obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceResource),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceYmtPath),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceCollection),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceFullCollection));
        }
    }

    private readonly record struct IndexPair(int OldIndex, int NewIndex);
    private readonly record struct IndexRange(int OldStart, int OldEnd, int NewStart, int NewEnd, int Count);
}
