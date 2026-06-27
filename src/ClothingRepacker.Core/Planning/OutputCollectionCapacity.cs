using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Planning;

public sealed class OutputCollectionCapacity
{
    public required string CollectionName { get; init; }
    public required string FullCollectionName { get; init; }
    public required string PedBaseName { get; init; }
    public required PedGender Gender { get; init; }

    public Dictionary<int, int> ComponentCounts { get; } = [];
    public Dictionary<int, int> PropCounts { get; } = [];
    public List<SourceYmt> Sources { get; } = [];
    public List<SourceYmtContribution> Contributions { get; } = [];

    public bool CanFit(SourceYmtContribution contribution, int maxDrawablesPerComponent, int maxDrawablesPerProp)
    {
        foreach (var component in contribution.ComponentRanges)
        {
            var current = ComponentCounts.GetValueOrDefault(component.Key);
            if (current + component.Value.Count > maxDrawablesPerComponent)
            {
                return false;
            }
        }

        foreach (var prop in contribution.PropRanges)
        {
            var current = PropCounts.GetValueOrDefault(prop.Key);
            if (current + prop.Value.Count > maxDrawablesPerProp)
            {
                return false;
            }
        }

        return true;
    }

    public void Add(SourceYmtContribution contribution)
    {
        Contributions.Add(contribution);
        if (!Sources.Contains(contribution.Source))
        {
            Sources.Add(contribution.Source);
        }

        foreach (var component in contribution.ComponentRanges)
        {
            ComponentCounts[component.Key] = ComponentCounts.GetValueOrDefault(component.Key) + component.Value.Count;
        }

        foreach (var prop in contribution.PropRanges)
        {
            PropCounts[prop.Key] = PropCounts.GetValueOrDefault(prop.Key) + prop.Value.Count;
        }
    }
}
