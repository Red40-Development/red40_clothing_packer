using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Planning;

public sealed class MergePlanner
{
    public IReadOnlyList<OutputCollectionCapacity> Plan(IReadOnlyList<SourceYmt> sources, MergePlanSettings settings, List<string> warnings, List<string> errors)
    {
        var outputs = new List<OutputCollectionCapacity>();
        foreach (var group in sources
                     .GroupBy(source => (source.PedBaseName, source.Gender))
                     .OrderBy(group => group.Key.PedBaseName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.Key.Gender))
        {
            var groupSources = group.ToList();
            outputs.AddRange(settings.OptimizeYmtUsage
                ? PlanOptimizedGroup(groupSources, settings, warnings)
                : PlanPreservingSourcePacks(groupSources, settings, warnings));
        }

        if (outputs.Count == 0 && sources.Count > 0)
        {
            warnings.Add("No output collections were planned.");
        }

        return outputs;
    }

    private static IReadOnlyList<OutputCollectionCapacity> PlanPreservingSourcePacks(
        IReadOnlyList<SourceYmt> sources,
        MergePlanSettings settings,
        List<string> warnings)
    {
        var outputs = new List<OutputCollectionCapacity>();
        foreach (var source in OrderSourcesForPlanning(sources))
        {
            var contributions = SplitSource(source, settings.MaxDrawablesPerComponent, settings.MaxDrawablesPerProp);
            WarnIfSourceExceedsCapacity(source, contributions.Count, warnings);

            foreach (var contribution in contributions)
            {
                var output = outputs.FirstOrDefault(candidate =>
                    candidate.CanFit(contribution, settings.MaxDrawablesPerComponent, settings.MaxDrawablesPerProp));

                if (output is null)
                {
                    output = CreateOutput(source, settings, outputs.Count + 1);
                    outputs.Add(output);
                }

                output.Add(contribution);
            }
        }

        return outputs;
    }

    private static IReadOnlyList<OutputCollectionCapacity> PlanOptimizedGroup(
        IReadOnlyList<SourceYmt> sources,
        MergePlanSettings settings,
        List<string> warnings)
    {
        foreach (var source in OrderSourcesForPlanning(sources))
        {
            WarnIfSourceExceedsCapacity(
                source,
                SplitSource(source, settings.MaxDrawablesPerComponent, settings.MaxDrawablesPerProp).Count,
                warnings);
        }

        var laneContributions = sources
            .SelectMany(source => BuildLaneContributions(source, settings.MaxDrawablesPerComponent, settings.MaxDrawablesPerProp))
            .ToList();
        if (laneContributions.Count == 0)
        {
            return sources.Count == 0 ? [] : [BuildEmptyOutput(sources, settings)];
        }

        var packedLanes = laneContributions
            .GroupBy(contribution => new LaneKey(contribution.Kind, contribution.Range.SlotId))
            .OrderBy(group => group.Key.Kind)
            .ThenBy(group => group.Key.SlotId)
            .Select(group => new PackedLane(
                group.Key,
                PackLane(group, CapacityFor(group.Key, settings))))
            .ToList();
        var outputCount = packedLanes.Max(lane => lane.Bins.Count);
        var outputs = Enumerable
            .Range(1, outputCount)
            .Select(index => CreateOutput(sources[0], settings, index))
            .ToList();

        foreach (var lane in packedLanes)
        {
            for (var binIndex = 0; binIndex < lane.Bins.Count; binIndex++)
            {
                foreach (var contribution in lane.Bins[binIndex].Contributions)
                {
                    outputs[binIndex].Add(ToSourceContribution(contribution));
                }
            }
        }

        foreach (var source in OrderSourcesForPlanning(sources.Where(source => !HasLaneContributions(source))))
        {
            outputs[0].Add(new SourceYmtContribution(source, new Dictionary<int, SourceIndexRange>(), new Dictionary<int, SourceIndexRange>()));
        }

        return outputs;
    }

    private static OutputCollectionCapacity BuildEmptyOutput(IReadOnlyList<SourceYmt> sources, MergePlanSettings settings)
    {
        var output = CreateOutput(sources[0], settings, 1);
        foreach (var source in OrderSourcesForPlanning(sources))
        {
            output.Add(new SourceYmtContribution(source, new Dictionary<int, SourceIndexRange>(), new Dictionary<int, SourceIndexRange>()));
        }

        return output;
    }

    private static IEnumerable<SourceYmt> OrderSourcesForPlanning(IEnumerable<SourceYmt> sources)
        => sources
            .OrderByDescending(source => LargestContribution(source))
            .ThenBy(source => source.YmtPath, StringComparer.OrdinalIgnoreCase);

    private static void WarnIfSourceExceedsCapacity(SourceYmt source, int contributionCount, List<string> warnings)
    {
        if (contributionCount > 1)
        {
            warnings.Add($"Source YMT exceeds configured drawable capacity and will be split across {contributionCount} target collections: {source.YmtPath}");
        }
    }

    private static OutputCollectionCapacity CreateOutput(SourceYmt source, MergePlanSettings settings, int index)
    {
        var prefix = source.Gender == PedGender.Female ? settings.FemalePrefix : settings.MalePrefix;
        var collectionName = $"{prefix}_{index:000}";
        return new OutputCollectionCapacity
        {
            CollectionName = collectionName,
            FullCollectionName = $"{source.PedBaseName}_{collectionName}",
            PedBaseName = source.PedBaseName,
            Gender = source.Gender,
        };
    }

    private static int LargestContribution(SourceYmt source)
        => Math.Max(
            source.Components.Select(component => component.Drawables.Count).DefaultIfEmpty(0).Max(),
            source.Props.Select(prop => prop.Props.Count).DefaultIfEmpty(0).Max());

    private static IEnumerable<LaneContribution> BuildLaneContributions(
        SourceYmt source,
        int maxDrawablesPerComponent,
        int maxDrawablesPerProp)
    {
        foreach (var component in source.Components.OrderBy(component => component.ComponentId))
        {
            foreach (var range in BuildRanges(source.YmtPath, component.ComponentId, component.Drawables.Count, maxDrawablesPerComponent))
            {
                yield return new LaneContribution(source, LaneKind.Component, range);
            }
        }

        foreach (var prop in source.Props.OrderBy(prop => prop.AnchorId))
        {
            foreach (var range in BuildRanges(source.YmtPath, prop.AnchorId, prop.Props.Count, maxDrawablesPerProp))
            {
                yield return new LaneContribution(source, LaneKind.Prop, range);
            }
        }
    }

    private static bool HasLaneContributions(SourceYmt source)
        => source.Components.Any(component => component.Drawables.Count > 0)
           || source.Props.Any(prop => prop.Props.Count > 0);

    private static IReadOnlyList<LaneBin> PackLane(IEnumerable<LaneContribution> contributions, int capacity)
    {
        var bins = new List<LaneBin>();
        foreach (var contribution in contributions
                     .OrderByDescending(contribution => contribution.Range.Count)
                     .ThenBy(contribution => contribution.Source.YmtPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(contribution => contribution.Range.StartIndex))
        {
            var bin = bins.FirstOrDefault(candidate => candidate.Used + contribution.Range.Count <= capacity);
            if (bin is null)
            {
                bin = new LaneBin();
                bins.Add(bin);
            }

            bin.Add(contribution);
        }

        return bins;
    }

    private static int CapacityFor(LaneKey key, MergePlanSettings settings)
        => key.Kind == LaneKind.Component ? settings.MaxDrawablesPerComponent : settings.MaxDrawablesPerProp;

    private static SourceYmtContribution ToSourceContribution(LaneContribution contribution)
        => contribution.Kind == LaneKind.Component
            ? new SourceYmtContribution(
                contribution.Source,
                new Dictionary<int, SourceIndexRange> { [contribution.Range.SlotId] = contribution.Range },
                new Dictionary<int, SourceIndexRange>())
            : new SourceYmtContribution(
                contribution.Source,
                new Dictionary<int, SourceIndexRange>(),
                new Dictionary<int, SourceIndexRange> { [contribution.Range.SlotId] = contribution.Range });

    private static IReadOnlyList<SourceYmtContribution> SplitSource(SourceYmt source, int maxDrawablesPerComponent, int maxDrawablesPerProp)
    {
        var componentPages = source.Components
            .Select(component => PageCount(component.Drawables.Count, maxDrawablesPerComponent))
            .DefaultIfEmpty(1)
            .Max();
        var propPages = source.Props
            .Select(prop => PageCount(prop.Props.Count, maxDrawablesPerProp))
            .DefaultIfEmpty(1)
            .Max();
        var pageCount = Math.Max(componentPages, propPages);
        var result = new List<SourceYmtContribution>();

        for (var page = 0; page < pageCount; page++)
        {
            var componentRanges = source.Components
                .Select(component => BuildRange(source.YmtPath, component.ComponentId, component.Drawables.Count, page, maxDrawablesPerComponent))
                .Where(range => range.Count > 0)
                .ToDictionary(range => range.SlotId, range => range);
            var propRanges = source.Props
                .Select(prop => BuildRange(source.YmtPath, prop.AnchorId, prop.Props.Count, page, maxDrawablesPerProp))
                .Where(range => range.Count > 0)
                .ToDictionary(range => range.SlotId, range => range);

            if (componentRanges.Count > 0 || propRanges.Count > 0 || pageCount == 1)
            {
                result.Add(new SourceYmtContribution(source, componentRanges, propRanges));
            }
        }

        return result;
    }

    private static int PageCount(int count, int pageSize)
        => count == 0 ? 0 : (count + pageSize - 1) / pageSize;

    private static SourceIndexRange BuildRange(string sourceYmtPath, int slotId, int count, int page, int pageSize)
    {
        var start = page * pageSize;
        if (start >= count)
        {
            return new SourceIndexRange(sourceYmtPath, slotId, start, 0);
        }

        return new SourceIndexRange(sourceYmtPath, slotId, start, Math.Min(pageSize, count - start));
    }

    private static IEnumerable<SourceIndexRange> BuildRanges(string sourceYmtPath, int slotId, int count, int pageSize)
    {
        for (var start = 0; start < count; start += pageSize)
        {
            yield return new SourceIndexRange(sourceYmtPath, slotId, start, Math.Min(pageSize, count - start));
        }
    }

    private enum LaneKind
    {
        Component = 0,
        Prop = 1,
    }

    private readonly record struct LaneKey(LaneKind Kind, int SlotId);

    private sealed record LaneContribution(SourceYmt Source, LaneKind Kind, SourceIndexRange Range);

    private sealed record PackedLane(LaneKey Key, IReadOnlyList<LaneBin> Bins);

    private sealed class LaneBin
    {
        public int Used { get; private set; }
        public List<LaneContribution> Contributions { get; } = [];

        public void Add(LaneContribution contribution)
        {
            Contributions.Add(contribution);
            Used += contribution.Range.Count;
        }
    }
}
