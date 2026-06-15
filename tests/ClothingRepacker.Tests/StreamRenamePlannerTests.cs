using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Planning;

namespace ClothingRepacker.Tests;

public class StreamRenamePlannerTests
{
    [Fact]
    public void RenamesDrawableAndTextureFiles()
    {
        var planner = new StreamRenamePlanner();
        var drawableMappings = new[]
        {
            new DrawableMapping("red40", "source.ymt.xml", "red40_clothes", "mp_f_freemode_01_red40_clothes", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 11, 0, 37),
        };

        var propMappings = Array.Empty<PropMapping>();
        var files = new[]
        {
            new StreamFile("red40", "red40", "/tmp/mp_f_freemode_01_red40_clothes^jbib_000_u.ydd", "mp_f_freemode_01_red40_clothes^jbib_000_u.ydd", ".ydd", true),
            new StreamFile("red40", "red40", "/tmp/mp_f_freemode_01_red40_clothes^jbib_diff_000_a_uni.ytd", "mp_f_freemode_01_red40_clothes^jbib_diff_000_a_uni.ytd", ".ytd", true),
        };

        var result = planner.BuildRenamePlan(drawableMappings, propMappings, files);

        Assert.Contains(result, item => item.TargetPath.EndsWith("mp_f_freemode_01_merged_f_001^jbib_037_u.ydd"));
        Assert.Contains(result, item => item.TargetPath.EndsWith("mp_f_freemode_01_merged_f_001^jbib_diff_037_a_uni.ytd"));
    }
}
