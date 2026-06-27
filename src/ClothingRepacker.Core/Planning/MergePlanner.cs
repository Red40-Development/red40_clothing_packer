using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Planning;

public sealed class MergePlanner
{
    public IReadOnlyList<OutputCollectionCapacity> Plan(IReadOnlyList<SourceYmt> sources, MergePlanSettings settings, List<string> warnings, List<string> errors)
    {
        var outputs = new List<OutputCollectionCapacity>();
        foreach (var group in sources.GroupBy(source => (source.PedBaseName, source.Gender)).OrderBy(group => group.Key.PedBaseName))
        {
            var orderedSources = group
                .OrderByDescending(source => LargestContribution(source))
                .ThenBy(source => source.YmtPath, StringComparer.OrdinalIgnoreCase);

            var index = 1;
            foreach (var source in orderedSources)
            {
                var contributions = SplitSource(source, settings.MaxDrawablesPerComponent, settings.MaxDrawablesPerProp);
                if (contributions.Count > 1)
                {
                    warnings.Add($"Source YMT exceeds configured drawable capacity and will be split across {contributions.Count} target collections: {source.YmtPath}");
                }

                foreach (var contribution in contributions)
                {
                    var output = outputs.FirstOrDefault(candidate =>
                        candidate.PedBaseName == source.PedBaseName &&
                        candidate.Gender == source.Gender &&
                        candidate.CanFit(contribution, settings.MaxDrawablesPerComponent, settings.MaxDrawablesPerProp));

                    if (output is null)
                    {
                        var prefix = source.Gender == PedGender.Female ? settings.FemalePrefix : settings.MalePrefix;
                        var collectionName = $"{prefix}_{index:000}";
                        output = new OutputCollectionCapacity
                        {
                            CollectionName = collectionName,
                            FullCollectionName = $"{source.PedBaseName}_{collectionName}",
                            PedBaseName = source.PedBaseName,
                            Gender = source.Gender,
                        };

                        outputs.Add(output);
                        index++;
                    }

                    output.Add(contribution);
                }
            }
        }

        if (outputs.Count == 0 && sources.Count > 0)
        {
            warnings.Add("No output collections were planned.");
        }

        return outputs;
    }

    private static int LargestContribution(SourceYmt source)
        => Math.Max(
            source.Components.Select(component => component.Drawables.Count).DefaultIfEmpty(0).Max(),
            source.Props.Select(prop => prop.Props.Count).DefaultIfEmpty(0).Max());

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
}
