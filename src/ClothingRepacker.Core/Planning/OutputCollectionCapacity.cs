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

    public bool CanFit(SourceYmt source, int maxPerType)
    {
        foreach (var component in source.Components)
        {
            var current = ComponentCounts.GetValueOrDefault(component.ComponentId);
            if (current + component.Drawables.Count > maxPerType)
            {
                return false;
            }
        }

        foreach (var prop in source.Props)
        {
            var current = PropCounts.GetValueOrDefault(prop.AnchorId);
            if (current + prop.Props.Count > maxPerType)
            {
                return false;
            }
        }

        return true;
    }

    public void Add(SourceYmt source)
    {
        Sources.Add(source);
        foreach (var component in source.Components)
        {
            ComponentCounts[component.ComponentId] = ComponentCounts.GetValueOrDefault(component.ComponentId) + component.Drawables.Count;
        }

        foreach (var prop in source.Props)
        {
            PropCounts[prop.AnchorId] = PropCounts.GetValueOrDefault(prop.AnchorId) + prop.Props.Count;
        }
    }
}
