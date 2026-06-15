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

        var output = Assert.Single(outputs);
        Assert.Contains(propAtLimit, output.Sources);
        Assert.DoesNotContain(componentOverflow, output.Sources);
        Assert.DoesNotContain(propOverflow, output.Sources);
        Assert.Equal(255, output.PropCounts[0]);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.Contains("component-overflow", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("prop-overflow", StringComparison.OrdinalIgnoreCase));
    }

    private static SourceYmt CreateSourceYmt(string pathSuffix, int componentDrawableCount, int propCount)
    {
        var xml = new XDocument(new XElement("CPedVariationInfo"));
        var components = componentDrawableCount > 0
            ? new[]
            {
                new ComponentBlock(
                    0,
                    Enumerable.Range(0, componentDrawableCount).Select(_ => new XElement("Item")).ToList(),
                    Array.Empty<XElement>())
            }
            : Array.Empty<ComponentBlock>();
        var props = propCount > 0
            ? new[]
            {
                new PropBlock(
                    0,
                    Enumerable.Range(0, propCount).Select(_ => new XElement("Item")).ToList())
            }
            : Array.Empty<PropBlock>();

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
            Messages: Array.Empty<ValidationMessage>());
    }
}
