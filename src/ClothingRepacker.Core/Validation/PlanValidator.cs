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
        }

        foreach (var collision in plan.StreamRenames.GroupBy(rename => rename.TargetPath, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add($"Planned target path collision: {collision.Key}");
        }

        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
