using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Validation;

public sealed class PlanValidator
{
    public IReadOnlyList<string> Validate(MergePlan plan)
    {
        var errors = new List<string>();
        errors.AddRange(plan.Errors);

        foreach (var target in plan.TargetCollections)
        {
            if (string.IsNullOrWhiteSpace(target.CollectionName))
            {
                errors.Add("Target collection is missing a collection name.");
            }

            foreach (var component in target.ComponentCounts)
            {
                if (component.Value > plan.Settings.MaxDrawablesPerComponent)
                {
                    errors.Add($"Target collection {target.FullCollectionName} component {component.Key} exceeds the configured drawable capacity.");
                }
            }

            var propCount = target.PropCounts.Values.Sum();
            if (propCount > plan.Settings.MaxDrawablesPerProp)
            {
                errors.Add($"Target collection {target.FullCollectionName} contains {propCount} aggregate props, exceeding the configured capacity of {plan.Settings.MaxDrawablesPerProp}.");
            }
        }

        foreach (var collision in plan.StreamRenames.GroupBy(rename => rename.TargetPath, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add($"Planned target path collision: {collision.Key}");
        }

        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
