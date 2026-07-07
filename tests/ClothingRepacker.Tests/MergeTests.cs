using ClothingRepacker.Core.Hashing;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Planning;
using ClothingRepacker.Core.Xml;
using System.Xml.Linq;

namespace ClothingRepacker.Tests;

public class MergeTests
{
    [Fact]
    public void MergesFixturesIntoExpectedCollection()
    {
        var reader = new PedVariationReader();
        var gangFlagsPath = TestFixturePaths.ResourceFile("gang_flags/stream/mp_f_freemode_01_mp_f_gang_flags.ymt.xml");
        var gangOutfitsPath = TestFixturePaths.ResourceFile("gang_outfits/stream/mp_f_freemode_01_mp_f_kickenit_gangs.ymt.xml");
        var gangFlags = reader.Read(XDocument.Load(gangFlagsPath), gangFlagsPath, "gang_flags", TestFixturePaths.ResourceDirectory("gang_flags"));
        var gangOutfits = reader.Read(XDocument.Load(gangOutfitsPath), gangOutfitsPath, "gang_outfits", TestFixturePaths.ResourceDirectory("gang_outfits"));

        var builder = new OutputCollectionBuilder("merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", ClothingRepacker.Core.Models.PedGender.Female);
        builder.AddComponents(gangFlags);
        builder.AddProps(gangFlags);
        builder.AddComponents(gangOutfits);
        builder.AddProps(gangOutfits);

        var xml = builder.BuildXml();
        Assert.Equal("255 0 255 255 255 255 255 255 255 1 2 255", xml.Root!.Element("availComp")!.Value.Trim());
        Assert.Equal(3, xml.Root.Element("compInfos")!.Elements("Item").Count());
        Assert.Equal("0", xml.Root.Element("propInfo")!.Element("numAvailProps")!.Attribute("value")!.Value);
        Assert.Equal($"hash_{JenkHash.Hash("merged_f_001"):X8}", xml.Root.Element("dlcName")!.Value.Trim());
    }

    [Fact]
    public void PlannerUsesSeparateComponentAndPropLimits()
    {
        var planner = new MergePlanner();
        var warnings = new List<string>();
        var errors = new List<string>();
        var settings = new MergePlanSettings
        {
            MaxDrawablesPerComponent = 128,
            MaxDrawablesPerProp = 255,
        };

        var componentOverflow = CreateSourceYmt("component-overflow", componentDrawableCount: 129, propCount: 0);
        var propAtLimit = CreateSourceYmt("prop-at-limit", componentDrawableCount: 0, propCount: 255);
        var propOverflow = CreateSourceYmt("prop-overflow", componentDrawableCount: 0, propCount: 256);

        var outputs = planner.Plan([componentOverflow, propAtLimit, propOverflow], settings, warnings, errors);

        Assert.Empty(errors);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(outputs, output => output.Sources.Contains(componentOverflow));
        Assert.Contains(outputs, output => output.Sources.Contains(propAtLimit));
        Assert.Contains(outputs, output => output.Sources.Contains(propOverflow));
        Assert.All(outputs, output =>
        {
            Assert.All(output.ComponentCounts.Values, count => Assert.InRange(count, 0, settings.MaxDrawablesPerComponent));
            Assert.All(output.PropCounts.Values, count => Assert.InRange(count, 0, settings.MaxDrawablesPerProp));
        });
        Assert.Equal(129, outputs.Sum(output => output.Contributions
            .Where(contribution => contribution.Source == componentOverflow)
            .SelectMany(contribution => contribution.ComponentRanges.Values)
            .Sum(range => range.Count)));
        Assert.Equal(256, outputs.Sum(output => output.Contributions
            .Where(contribution => contribution.Source == propOverflow)
            .SelectMany(contribution => contribution.PropRanges.Values)
            .Sum(range => range.Count)));
    }

    [Fact]
    public void OptimizedPlannerCanSplitSourceLanesToReduceTargetCollections()
    {
        var planner = new MergePlanner();
        var settings = new MergePlanSettings
        {
            MaxDrawablesPerComponent = 10,
            OptimizeYmtUsage = true,
        };
        var sources = new[]
        {
            CreateSourceYmt("a_pack", new Dictionary<int, int> { [0] = 6, [1] = 6 }),
            CreateSourceYmt("b_pack", new Dictionary<int, int> { [0] = 6, [2] = 6 }),
            CreateSourceYmt("c_pack", new Dictionary<int, int> { [1] = 6, [2] = 6 }),
        };

        var preservingOutputs = planner.Plan(
            sources,
            new MergePlanSettings
            {
                MaxDrawablesPerComponent = settings.MaxDrawablesPerComponent,
            },
            [],
            []);
        var optimizedOutputs = planner.Plan(
            sources,
            settings,
            [],
            []);

        Assert.Equal(3, preservingOutputs.Count);
        Assert.Equal(2, optimizedOutputs.Count);
        Assert.All(optimizedOutputs, output =>
        {
            Assert.All(output.ComponentCounts.Values, count => Assert.InRange(count, 0, settings.MaxDrawablesPerComponent));
        });
        Assert.Equal(2, optimizedOutputs.Count(output => output.Sources.Contains(sources[1])));
    }

    private static SourceYmt CreateSourceYmt(string pathSuffix, int componentDrawableCount, int propCount)
        => CreateSourceYmt(
            pathSuffix,
            componentDrawableCount > 0
                ? new Dictionary<int, int> { [0] = componentDrawableCount }
                : new Dictionary<int, int>(),
            propCount > 0
                ? new Dictionary<int, int> { [0] = propCount }
                : new Dictionary<int, int>());

    private static SourceYmt CreateSourceYmt(string pathSuffix, IReadOnlyDictionary<int, int> componentDrawableCounts, IReadOnlyDictionary<int, int>? propCounts = null)
    {
        var xml = new XDocument(new XElement("CPedVariationInfo"));
        var components = componentDrawableCounts
            .OrderBy(pair => pair.Key)
            .Select(pair => new ComponentBlock(
                pair.Key,
                Enumerable.Range(0, pair.Value).Select(_ => new XElement("Item")).ToList(),
                Array.Empty<XElement>()))
            .ToList();
        var props = (propCounts ?? new Dictionary<int, int>())
            .OrderBy(pair => pair.Key)
            .Select(pair => new PropBlock(
                pair.Key,
                Enumerable.Range(0, pair.Value).Select(index =>
                    new XElement("Item", new XElement("propId", new XAttribute("value", index)))).ToList()))
            .ToList();

        return new SourceYmt(
            YmtPath: $"/tmp/{pathSuffix}.ymt.xml",
            ResourceName: "test_resource",
            ResourceRoot: "/tmp/test_resource",
            PedBaseName: "mp_m_freemode_01",
            Gender: PedGender.Male,
            CollectionName: pathSuffix,
            FullCollectionName: $"mp_m_freemode_01_{pathSuffix}",
            DlcName: "hash_test",
            Xml: xml,
            Components: components,
            Props: props,
            CreatureComponentRepairHints: Array.Empty<CreatureComponentRepairHint>(),
            CreaturePropRepairHints: Array.Empty<CreaturePropRepairHint>(),
            Messages: Array.Empty<ValidationMessage>());
    }
}
