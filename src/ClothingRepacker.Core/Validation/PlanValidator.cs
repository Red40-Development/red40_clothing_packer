using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Validation;

public sealed class PlanValidator
{
    public IReadOnlyList<string> Validate(MergePlan plan)
    {
        var errors = new List<string>();
        errors.AddRange(plan.Errors);
        var componentCapacity = Math.Clamp(
            plan.Settings.MaxDrawablesPerComponent,
            1,
            ClothingConstants.MaximumDrawablesPerComponent);
        var propCapacity = Math.Clamp(
            plan.Settings.MaxDrawablesPerProp,
            1,
            ClothingConstants.MaximumDrawablesPerProp);

        if (plan.Settings.MaxDrawablesPerComponent <= 0
            || plan.Settings.MaxDrawablesPerComponent > ClothingConstants.MaximumDrawablesPerComponent)
        {
            errors.Add($"Configured component drawable capacity must be between 1 and {ClothingConstants.MaximumDrawablesPerComponent}.");
        }

        if (plan.Settings.MaxDrawablesPerProp <= 0
            || plan.Settings.MaxDrawablesPerProp > ClothingConstants.MaximumDrawablesPerProp)
        {
            errors.Add($"Configured prop drawable capacity must be between 1 and {ClothingConstants.MaximumDrawablesPerProp}; 256 cannot be represented by the YMT numAvailProps field.");
        }

        foreach (var target in plan.TargetCollections)
        {
            if (string.IsNullOrWhiteSpace(target.CollectionName))
            {
                errors.Add("Target collection is missing a collection name.");
            }

            foreach (var component in target.ComponentCounts)
            {
                if (component.Value > componentCapacity)
                {
                    errors.Add($"Target collection {target.FullCollectionName} component {component.Key} exceeds the safe drawable capacity of {componentCapacity}.");
                }
            }

            var propCount = target.PropCounts.Values.Sum();
            if (propCount > propCapacity)
            {
                errors.Add($"Target collection {target.FullCollectionName} contains {propCount} aggregate props, exceeding the safe capacity of {propCapacity}.");
            }
        }

        foreach (var collision in plan.StreamRenames.GroupBy(rename => rename.TargetPath, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add($"Planned target path collision: {collision.Key}");
        }

        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
