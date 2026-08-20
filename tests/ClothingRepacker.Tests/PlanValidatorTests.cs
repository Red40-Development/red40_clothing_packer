using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Validation;

namespace ClothingRepacker.Tests;

public class PlanValidatorTests
{
    [Fact]
    public void RejectsUnrepresentableConfiguredLimits()
    {
        var plan = new MergePlan
        {
            Settings = new MergePlanSettings
            {
                MaxDrawablesPerComponent = 256,
                MaxDrawablesPerProp = 256,
            },
        };

        var errors = new PlanValidator().Validate(plan);

        Assert.Contains(errors, error => error.Contains("component drawable capacity must be between 1 and 255"));
        Assert.Contains(errors, error => error.Contains("prop drawable capacity must be between 1 and 255"));
    }

    [Fact]
    public void RejectsTargetWhoseAggregatePropsExceedCapacity()
    {
        var plan = new MergePlan
        {
            Settings = new MergePlanSettings
            {
                MaxDrawablesPerProp = 255,
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

    [Fact]
    public void Rejects256PropsEvenWhenLoadedPlanClaimsHigherCapacity()
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
                    new Dictionary<int, int> { [0] = 256 })
            ],
        };

        var errors = new PlanValidator().Validate(plan);

        Assert.Contains(errors, error => error.Contains("prop drawable capacity must be between 1 and 255"));
        Assert.Contains(errors, error => error.Contains("256 aggregate props"));
    }
}
