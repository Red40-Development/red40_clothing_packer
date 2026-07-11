using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Reporting;

namespace ClothingRepacker.Tests;

public class YmtRepackReportTests
{
    [Fact]
    public void BuilderCompressesContiguousMappingsAndAddsFreeCapacity()
    {
        var report = new YmtRepackReportBuilder().Build(BuildReportPlan());
        var target = Assert.Single(report.Targets);
        var componentLane = target.Lanes.Single(lane => lane.Kind == YmtRepackLaneKind.Component && lane.SlotId == 11);
        var propLane = target.Lanes.Single(lane => lane.Kind == YmtRepackLaneKind.Prop && lane.SlotId == 0);

        Assert.Equal(10, componentLane.Capacity);
        Assert.Equal(4, componentLane.UsedCount);
        Assert.Equal(6, componentLane.FreeCount);
        Assert.Contains(componentLane.UsedSegments, segment =>
            segment.SourceYmtPath == "/tmp/a.ymt"
            && segment.OldRange == "0-1"
            && segment.NewRange == "0-1"
            && segment.Count == 2);
        Assert.Contains(componentLane.UsedSegments, segment =>
            segment.SourceYmtPath == "/tmp/a.ymt"
            && segment.OldRange == "3"
            && segment.NewRange == "2"
            && segment.Count == 1);
        Assert.Equal(6, componentLane.Segments.Last().Count);
        Assert.True(componentLane.Segments.Last().IsFree);
        Assert.Equal("4-9", componentLane.Segments.Last().NewRange);

        Assert.Equal(5, propLane.Capacity);
        Assert.Equal(2, propLane.UsedCount);
        Assert.Equal(3, propLane.FreeCount);
        Assert.Contains(propLane.UsedSegments, segment =>
            segment.Kind == YmtRepackLaneKind.Prop
            && segment.OldRange == "5-6"
            && segment.NewRange == "0-1"
            && segment.Count == 2);
    }

    [Fact]
    public void FormatterIncludesDetailedMappingColumns()
    {
        var report = new YmtRepackReportBuilder().Build(BuildReportPlan());
        var text = new YmtRepackReportFormatter().Format(report);

        Assert.Contains("Target", text);
        Assert.Contains("Kind", text);
        Assert.Contains("Source Resource", text);
        Assert.Contains("mp_f_freemode_01_merged_f_001", text);
        Assert.Contains("component", text);
        Assert.Contains("11 jbib", text);
        Assert.Contains("/tmp/a.ymt", text);
        Assert.Contains("0-1", text);
    }

    [Fact]
    public void BuilderReportsCreatureMetadataSourcesAndTargetOutputs()
    {
        var report = new YmtRepackReportBuilder().Build(BuildReportPlan());

        var source = Assert.Single(report.CreatureMetadataSources);
        Assert.Equal("/tmp/meta.ymt", source.MetadataPath);
        Assert.Equal(["/tmp/a.ymt"], source.SourceYmtPaths);

        var target = Assert.Single(report.CreatureMetadataTargets);
        Assert.True(target.IsRequired);
        Assert.False(target.IsUnnecessaryOutput);
        Assert.Equal("MP_CreatureMetadata_merged_f_001", target.OutputName);
    }

    [Fact]
    public void FormatterIncludesCreatureMetadataDiagnostics()
    {
        var text = new YmtRepackReportFormatter().Format(YmtRepackReportWithUnnecessaryOutput());

        Assert.Contains("Creature metadata sources", text);
        Assert.Contains("Creature metadata targets", text);
        Assert.Contains("UNNECESSARY OUTPUT", text);
        Assert.Contains("source metadata: none", text);
    }

    private static MergePlan BuildReportPlan()
        => new()
        {
            TargetResource = "zz_merged_clothing_meta",
            Settings = new MergePlanSettings
            {
                MaxDrawablesPerComponent = 10,
                MaxDrawablesPerProp = 5,
            },
            SourceYmts =
            [
                new SourceYmtSummary("pack_a", "/tmp/a.ymt", "mp_f_freemode_01", PedGender.Female, "pack_a", "mp_f_freemode_01_pack_a", "hash_a", [], []),
                new SourceYmtSummary("pack_b", "/tmp/b.ymt", "mp_f_freemode_01", PedGender.Female, "pack_b", "mp_f_freemode_01_pack_b", "hash_b", [], []),
            ],
            SourceCreatureMetadata =
            [
                new SourceCreatureMetadataSummary("pack_a", "/tmp/meta.ymt", 1, 2, 3, ["/tmp/a.ymt"]),
            ],
            CreatureMetadataOutputs =
            [
                new CreatureMetadataOutputPlan(
                    "MP_CreatureMetadata_merged_f_001",
                    "zz_merged_clothing_meta/stream/MP_CreatureMetadata_merged_f_001.ymt",
                    ["merged_f_001"],
                    [new CreatureMetadataSourceBinding("/tmp/a.ymt", "/tmp/meta.ymt")]),
            ],
            TargetCollections =
            [
                new TargetCollectionPlan(
                    "merged_f_001",
                    "mp_f_freemode_01_merged_f_001",
                    PedGender.Female,
                    "zz_merged_clothing_meta/stream/mp_f_freemode_01_merged_f_001.ymt",
                    ["/tmp/a.ymt", "/tmp/b.ymt"],
                    [],
                    [],
                    new Dictionary<int, int> { [11] = 4 },
                    new Dictionary<int, int> { [0] = 2 }),
            ],
            DrawableMappings =
            [
                new DrawableMapping("pack_a", "/tmp/a.ymt", "pack_a", "mp_f_freemode_01_pack_a", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 11, 0, 0),
                new DrawableMapping("pack_a", "/tmp/a.ymt", "pack_a", "mp_f_freemode_01_pack_a", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 11, 1, 1),
                new DrawableMapping("pack_a", "/tmp/a.ymt", "pack_a", "mp_f_freemode_01_pack_a", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 11, 3, 2),
                new DrawableMapping("pack_b", "/tmp/b.ymt", "pack_b", "mp_f_freemode_01_pack_b", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 11, 0, 3),
            ],
            PropMappings =
            [
                new PropMapping("pack_b", "/tmp/b.ymt", "pack_b", "mp_f_freemode_01_pack_b", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 0, 5, 0),
                new PropMapping("pack_b", "/tmp/b.ymt", "pack_b", "mp_f_freemode_01_pack_b", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 0, 6, 1),
            ],
        };

    private static YmtRepackReport YmtRepackReportWithUnnecessaryOutput()
        => new(
            [],
            [],
            [],
            [new YmtRepackCreatureMetadataTarget(
                "merged_f_001",
                "mp_f_freemode_01_merged_f_001",
                [],
                [],
                false,
                false,
                "MP_CreatureMetadata_merged_f_001",
                "zz_merged_clothing_meta/stream/MP_CreatureMetadata_merged_f_001.ymt")]);
}
