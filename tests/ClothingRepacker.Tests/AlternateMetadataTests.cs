using System.Xml.Linq;
using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;

namespace ClothingRepacker.Tests;

public class AlternateMetadataTests
{
    [Fact]
    public async Task BuildRemapsAlternateMetadataIntoGeneratedResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alternate-metadata-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resourceRoot = Path.Combine(resources, "alt_pack");
        var stream = Path.Combine(resourceRoot, "stream");
        Directory.CreateDirectory(stream);
        BuildPedVariation("mp_f_zdwcp1").Save(Path.Combine(stream, "mp_f_freemode_01_mp_f_zdwcp1.ymt.xml"));
        BuildAlternateVariations().Save(Path.Combine(resourceRoot, "pedalternatevariations.meta"));
        BuildFirstPersonAlternates().Save(Path.Combine(resourceRoot, "first_person_alternates.meta"));

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var generatedRoot = Path.Combine(outputRoot, "zz_merged_clothing_meta");
        var alternateXml = XDocument.Load(Path.Combine(generatedRoot, "data", "pedalternatevariations.meta"));
        var firstPersonXml = XDocument.Load(Path.Combine(generatedRoot, "data", "first_person_alternates.meta"));
        var manifest = await File.ReadAllTextAsync(Path.Combine(generatedRoot, "fxmanifest.lua"));

        Assert.Equal(2, analyze.Plan.SourceAlternateMetadata.Count);
        Assert.Equal(2, analyze.Plan.AlternateMetadataOutputs.Count);
        Assert.Contains(alternateXml.Descendants("dlcNameHash"), element => element.Value.Trim() == "merged_f_001");
        Assert.Contains(alternateXml.Descendants("Item"), item =>
            item.Element("dlcNameHash")?.Value.Trim() == "mp_f_heist"
            && item.Element("sourceAssets")?.Element("Item")?.Element("dlcNameHash")?.Value.Trim() == "merged_f_001");
        Assert.DoesNotContain(alternateXml.Descendants("dlcNameHash"), element => element.Value.Trim() == "mp_f_zdwcp1");
        Assert.Contains(firstPersonXml.Descendants("assetName"), element => element.Value.Trim() == "mp_f_freemode_01_merged_f_001/jbib_001_u");
        Assert.DoesNotContain(firstPersonXml.Descendants("assetName"), element => element.Value.Contains("mp_f_zdwcp1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("data_file 'ALTERNATE_VARIATIONS_FILE' 'data/pedalternatevariations.meta'", manifest);
        Assert.Contains("data_file 'PED_FIRST_PERSON_ALTERNATE_DATA' 'data/first_person_alternates.meta'", manifest);
    }

    private static XDocument BuildPedVariation(string collectionName)
        => new(
            new XElement("CPedVariationInfo",
                new XAttribute("name", collectionName),
                new XElement("availComp", "255 255 255 255 255 255 255 255 255 255 255 0"),
                new XElement("aComponentData3",
                    new XAttribute("itemType", "CPVComponentData"),
                    new XElement("Item",
                        new XElement("numAvailTex", new XAttribute("value", 2)),
                        new XElement("aDrawblData3",
                            new XAttribute("itemType", "CPVDrawblData"),
                            new XElement("Item", new XElement("aTexData", new XAttribute("itemType", "CPVTextureData"))),
                            new XElement("Item", new XElement("aTexData", new XAttribute("itemType", "CPVTextureData")))))),
                new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
                new XElement("propInfo",
                    new XElement("numAvailProps", new XAttribute("value", 0)),
                    new XElement("aPropMetaData", new XAttribute("itemType", "CPedPropMetaData")),
                    new XElement("aAnchors", new XAttribute("itemType", "CAnchorProps"))),
                new XElement("dlcName", "hash_00000000")));

    private static XDocument BuildAlternateVariations()
        => new(
            new XElement("CAlternateVariations",
                new XElement("peds",
                    new XElement("Item",
                        new XElement("name", "mp_f_freemode_01"),
                        new XElement("switches",
                            new XElement("Item",
                                new XElement("dlcNameHash", "mp_f_zdwcp1"),
                                new XElement("component", new XAttribute("value", 11)),
                                new XElement("index", new XAttribute("value", 1)),
                                new XElement("alt", new XAttribute("value", 3)),
                                new XElement("sourceAssets")),
                            new XElement("Item",
                                new XElement("dlcNameHash", "mp_f_heist"),
                                new XElement("component", new XAttribute("value", 5)),
                                new XElement("index", new XAttribute("value", 0)),
                                new XElement("alt", new XAttribute("value", 19)),
                                new XElement("sourceAssets",
                                    new XElement("Item",
                                        new XElement("dlcNameHash", "mp_f_zdwcp1"),
                                        new XElement("component", new XAttribute("value", 11)),
                                        new XElement("index", new XAttribute("value", 0))))))))));

    private static XDocument BuildFirstPersonAlternates()
        => new(
            new XElement("FirstPersonAlternateData",
                new XElement("alternates",
                    new XElement("Item",
                        new XElement("assetName", "MP_F_Freemode_01_mp_f_zdwcp1/jbib_001_u"),
                        new XElement("alternate", new XAttribute("value", 1))),
                    new XElement("Item",
                        new XElement("assetName", "MP_F_Freemode_01_unrelated/jbib_001_u"),
                        new XElement("alternate", new XAttribute("value", 1))))));
}
