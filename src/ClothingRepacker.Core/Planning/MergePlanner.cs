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
                if (!FitsSingleCollection(source, settings.MaxDrawablesPerComponent, settings.MaxDrawablesPerProp))
                {
                    errors.Add($"Source YMT exceeds capacity and needs manual review: {source.YmtPath}");
                    continue;
                }

                var output = outputs.FirstOrDefault(candidate =>
                    candidate.PedBaseName == source.PedBaseName &&
                    candidate.Gender == source.Gender &&
                    candidate.CanFit(source, settings.MaxDrawablesPerComponent, settings.MaxDrawablesPerProp));

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

                output.Add(source);
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

    private static bool FitsSingleCollection(SourceYmt source, int maxDrawablesPerComponent, int maxDrawablesPerProp)
        => source.Components.All(component => component.Drawables.Count <= maxDrawablesPerComponent)
           && source.Props.All(prop => prop.Props.Count <= maxDrawablesPerProp);
}
