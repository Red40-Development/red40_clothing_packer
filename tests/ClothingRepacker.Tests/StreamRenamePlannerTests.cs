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

    [Fact]
    public void RenamesPropFilesFromPropCollection()
    {
        var planner = new StreamRenamePlanner();
        var propMappings = new[]
        {
            new PropMapping("zdwcp1", "source.ymt.xml", "mp_f_zdwcp1", "mp_f_freemode_01_mp_f_zdwcp1", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 0, 24, 7),
        };

        var files = new[]
        {
            new StreamFile("zdwcp1", "zdwcp1", "/tmp/mp_f_freemode_01_p_mp_f_zdwcp1^p_head_024.ydd", "mp_f_freemode_01_p_mp_f_zdwcp1^p_head_024.ydd", ".ydd", true),
            new StreamFile("zdwcp1", "zdwcp1", "/tmp/mp_f_freemode_01_p_mp_f_zdwcp1^p_head_diff_024_a.ytd", "mp_f_freemode_01_p_mp_f_zdwcp1^p_head_diff_024_a.ytd", ".ytd", true),
        };

        var result = planner.BuildRenamePlan(Array.Empty<DrawableMapping>(), propMappings, files);

        Assert.Contains(result, item => item.TargetPath.EndsWith("mp_f_freemode_01_p_merged_f_001^p_head_007.ydd"));
        Assert.Contains(result, item => item.TargetPath.EndsWith("mp_f_freemode_01_p_merged_f_001^p_head_diff_007_a.ytd"));
    }

    [Fact]
    public void ParsesEachStreamFilenameOnceIncludingUnrecognizedFiles()
    {
        var planner = new StreamRenamePlanner();
        var mappings = Enumerable.Range(0, 20)
            .Select(index => new DrawableMapping(
                "resource",
                "source.ymt.xml",
                $"source_{index}",
                $"mp_f_freemode_01_source_{index}",
                $"merged_{index}",
                $"mp_f_freemode_01_merged_{index}",
                "mp_f_freemode_01",
                11,
                0,
                index))
            .ToArray();
        var files = Enumerable.Range(0, 100)
            .Select(index => new StreamFile(
                "resource",
                "resource",
                $"/tmp/file-{index}.ydd",
                index == 0
                    ? "mp_f_freemode_01_source_0^jbib_000_u.ydd"
                    : $"unrecognized-{index}.bin",
                ".ydd",
                true))
            .ToArray();

        planner.BuildRenamePlan(mappings, Array.Empty<PropMapping>(), files);

        Assert.Equal(files.Length, planner.ParsedFilenameCount);
    }

    [Fact]
    public void ReportsAmbiguousSourceClaimsInsteadOfTakingTheFirstMapping()
    {
        var planner = new StreamRenamePlanner();
        var mappings = new[]
        {
            new DrawableMapping("resource", "one.ymt.xml", "source", "mp_f_freemode_01_source", "merged_a", "mp_f_freemode_01_merged_a", "mp_f_freemode_01", 11, 0, 1),
            new DrawableMapping("resource", "two.ymt.xml", "source", "mp_f_freemode_01_source", "merged_b", "mp_f_freemode_01_merged_b", "mp_f_freemode_01", 11, 0, 2),
        };
        var files = new[]
        {
            new StreamFile("resource", "resource", "/tmp/source^jbib_000_u.ydd", "mp_f_freemode_01_source^jbib_000_u.ydd", ".ydd", true),
        };

        var renames = planner.BuildRenamePlan(mappings, Array.Empty<PropMapping>(), files);
        var errors = planner.ValidateCollisions(renames);

        Assert.Empty(renames);
        var error = Assert.Single(errors);
        Assert.Contains("Ambiguous stream rename source claim", error);
        Assert.Contains(files[0].FullPath, error);
    }

    [Fact]
    public void ReportsTargetCollisionsDeterministically()
    {
        var planner = new StreamRenamePlanner();
        var mappings = new[]
        {
            new DrawableMapping("resource", "one.ymt.xml", "source_a", "mp_f_freemode_01_source_a", "merged", "mp_f_freemode_01_merged", "mp_f_freemode_01", 11, 0, 1),
            new DrawableMapping("resource", "two.ymt.xml", "source_b", "mp_f_freemode_01_source_b", "merged", "mp_f_freemode_01_merged", "mp_f_freemode_01", 11, 0, 1),
        };
        var files = new[]
        {
            new StreamFile("resource", "resource", "/tmp/source_b^jbib_000_u.ydd", "mp_f_freemode_01_source_b^jbib_000_u.ydd", ".ydd", true),
            new StreamFile("resource", "resource", "/tmp/source_a^jbib_000_u.ydd", "mp_f_freemode_01_source_a^jbib_000_u.ydd", ".ydd", true),
        };

        var renames = planner.BuildRenamePlan(mappings, Array.Empty<PropMapping>(), files);
        var errors = planner.ValidateCollisions(renames);

        Assert.Equal(2, renames.Count);
        var error = Assert.Single(errors);
        Assert.StartsWith("Planned target path collision:", error);
        Assert.EndsWith("mp_f_freemode_01_merged^jbib_001_u.ydd", error);
    }

    [Fact]
    public void LeavesUnrecognizedFilenamesUntouchedAndOrdersRenamesBySourcePath()
    {
        var planner = new StreamRenamePlanner();
        var mappings = new[]
        {
            new DrawableMapping("resource", "b.ymt.xml", "source_b", "mp_f_freemode_01_source_b", "merged_b", "mp_f_freemode_01_merged_b", "mp_f_freemode_01", 11, 0, 1),
            new DrawableMapping("resource", "a.ymt.xml", "source_a", "mp_f_freemode_01_source_a", "merged_a", "mp_f_freemode_01_merged_a", "mp_f_freemode_01", 11, 0, 1),
        };
        var files = new[]
        {
            new StreamFile("resource", "resource", "/tmp/z-source.bin", "unrecognized.bin", ".bin", true),
            new StreamFile("resource", "resource", "/tmp/source_b^jbib_000_u.ydd", "mp_f_freemode_01_source_b^jbib_000_u.ydd", ".ydd", true),
            new StreamFile("resource", "resource", "/tmp/source_a^jbib_000_u.ydd", "mp_f_freemode_01_source_a^jbib_000_u.ydd", ".ydd", true),
        };

        var renames = planner.BuildRenamePlan(mappings, Array.Empty<PropMapping>(), files);

        Assert.Equal(2, renames.Count);
        Assert.Equal(files[2].FullPath, renames[0].SourcePath);
        Assert.Equal(files[1].FullPath, renames[1].SourcePath);
    }

    [Fact]
    public void PreservesCaseInsensitiveSuffixExtensionAndEscrowSemantics()
    {
        var planner = new StreamRenamePlanner();
        var mapping = new DrawableMapping("resource", "source.ymt.xml", "source", "MP_F_FREEMODE_01_SOURCE", "merged", "mp_f_freemode_01_merged", "mp_f_freemode_01", 11, 0, 1);
        var files = new[]
        {
            new StreamFile("resource", "resource", "/tmp/MP_F_FREEMODE_01_SOURCE^JBIB_000_X.YLD", "MP_F_FREEMODE_01_SOURCE^JBIB_000_X.YLD", ".YLD", true),
        };

        var rename = Assert.Single(planner.BuildRenamePlan([mapping], Array.Empty<PropMapping>(), files));

        Assert.EndsWith("mp_f_freemode_01_merged^jbib_001_X.YLD", rename.TargetPath);
        Assert.True(rename.IsEscrowOpaque);
    }
}
