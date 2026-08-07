using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Validation;

namespace ClothingRepacker.Tests;

public class PlanValidatorTests
{
    [Fact]
    public void RejectsTargetWhoseAggregatePropsExceedCapacity()
    {
        var plan = new MergePlan
        {
            Settings = new MergePlanSettings
            {
                MaxDrawablesPerProp = 256,
            },
            TargetCollections =
            [
                new TargetCollectionPlan(
                    "merged_m_001",
                    "mp_m_freemode_01_merged_m_001",
                    PedGender.Male,
                    "target.ymt",
                    [],
                    [],
                    [],
                    [],
                    new Dictionary<int, int>
                    {
                        [0] = 128,
                        [1] = 129,
                    })
            ],
        };

        var errors = new PlanValidator().Validate(plan);

        var error = Assert.Single(errors);
        Assert.Contains("257 aggregate props", error);
    }
}
