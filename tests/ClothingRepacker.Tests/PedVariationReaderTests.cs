using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Xml;
using System.Xml.Linq;

namespace ClothingRepacker.Tests;

public class PedVariationReaderTests
{
    private readonly PedVariationReader _reader = new();

    [Fact]
    public void ReadsFemaleGangFlagsFixture()
    {
        var path = TestFixturePaths.ResourceFile("gang_flags/stream/mp_f_freemode_01_mp_f_gang_flags.ymt.xml");
        var doc = XDocument.Load(path);
        var result = _reader.Read(doc, path, "gang_flags", TestFixturePaths.ResourceDirectory("gang_flags"));

        Assert.Equal("mp_f_gang_flags", result.CollectionName);
        Assert.Equal("hash_7D552BA1", result.DlcName);
        Assert.Equal(PedGender.Female, result.Gender);
        Assert.Single(result.Components.Single(c => c.ComponentId == 1).Drawables);
        Assert.Single(result.Components.Single(c => c.ComponentId == 10).Drawables);
        Assert.Empty(result.Props);
    }

    [Fact]
    public void ReadsMaleMerryweatherVestsFixture()
    {
        var path = TestFixturePaths.ResourceFile("gang_outfits/stream/mp_m_freemode_01_mp_m_merryweathervests.ymt.xml");
        var doc = XDocument.Load(path);
        var result = _reader.Read(doc, path, "gang_outfits", TestFixturePaths.ResourceDirectory("gang_outfits"));

        Assert.Equal("mp_m_merryweathervests", result.CollectionName);
        Assert.Equal("hash_43B32E42", result.DlcName);
        Assert.Equal(PedGender.Male, result.Gender);
        Assert.Equal(2, result.Components.Single(c => c.ComponentId == 9).Drawables.Count);
        Assert.Empty(result.Props);
    }

    [Fact]
    public void ReadsNamelessFreemodeBaseFixture()
    {
        var path = TestFixturePaths.Ymt("mp_f_freemode_01.ymt.xml");
        var doc = XDocument.Load(path);
        var result = _reader.Read(doc, path, "base", Path.GetDirectoryName(path)!);

        Assert.Equal(string.Empty, result.CollectionName);
        Assert.Equal("mp_f_freemode_01", result.FullCollectionName);
        Assert.Equal("mp_f_freemode_01", result.PedBaseName);
        Assert.Equal(PedGender.Female, result.Gender);
        Assert.NotEmpty(result.Components);
        Assert.NotEmpty(result.Props);
    }

    [Fact]
    public void InfersNamelessCollectionFromFreemodeFilename()
    {
        var path = Path.Combine(Path.GetTempPath(), "mp_m_freemode_01_custom_pack.ymt.xml");
        var doc = BuildMinimalPedVariationXml();
        var result = _reader.Read(doc, path, "custom", Path.GetTempPath());

        Assert.Equal("custom_pack", result.CollectionName);
        Assert.Equal("mp_m_freemode_01_custom_pack", result.FullCollectionName);
        Assert.Equal("mp_m_freemode_01", result.PedBaseName);
        Assert.Equal(PedGender.Male, result.Gender);
    }

    [Fact]
    public void ReportsMalformedPropIdentifiersAndSkipsInvalidEntriesDeterministically()
    {
        var path = Path.Combine(Path.GetTempPath(), "mp_m_freemode_01_malformed_props.ymt.xml");
        var doc = BuildPropVariationXml(
            new XElement("Item", new XElement("propId", new XAttribute("value", 0))),
            new XElement("Item", new XElement("anchorId", new XAttribute("value", 0))),
            new XElement("Item",
                new XElement("anchorId", new XAttribute("value", "not-an-integer")),
                new XElement("propId", new XAttribute("value", 0))),
            new XElement("Item",
                new XElement("anchorId", new XAttribute("value", 0)),
                new XElement("propId", new XAttribute("value", "not-an-integer"))),
            new XElement("Item",
                new XElement("anchorId", new XAttribute("value", -1)),
                new XElement("propId", new XAttribute("value", 0))),
            new XElement("Item",
                new XElement("anchorId", new XAttribute("value", 0)),
                new XElement("propId", new XAttribute("value", -1))),
            new XElement("Item",
                new XElement("anchorId", new XAttribute("value", 0)),
                new XElement("propId", new XAttribute("value", 2))),
            new XElement("Item",
                new XElement("anchorId", new XAttribute("value", 0)),
                new XElement("propId", new XAttribute("value", 2))));

        var result = _reader.Read(doc, path, "malformed", Path.GetTempPath());

        Assert.Equal(
            [
                "missing-prop-anchorId",
                "missing-prop-propId",
                "malformed-prop-anchorId",
                "malformed-prop-propId",
                "negative-prop-anchorId",
                "negative-prop-propId",
                "duplicate-propId",
            ],
            result.Messages.Select(message => message.Code));
        var propBlock = Assert.Single(result.Props);
        Assert.Equal([2], propBlock.Props.Select(item => int.Parse(item.Element("propId")!.Attribute("value")!.Value)));
    }

    [Fact]
    public void PreservesSparsePropOrderingByPropIdThenDocumentOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), "mp_m_freemode_01_sparse_props.ymt.xml");
        var doc = BuildPropVariationXml(
            new XElement("Item",
                new XElement("anchorId", new XAttribute("value", 0)),
                new XElement("propId", new XAttribute("value", 2))),
            new XElement("Item",
                new XElement("anchorId", new XAttribute("value", 0)),
                new XElement("propId", new XAttribute("value", 0))));

        var result = _reader.Read(doc, path, "sparse", Path.GetTempPath());

        var propBlock = Assert.Single(result.Props);
        Assert.Equal([0, 2], propBlock.Props.Select(item => int.Parse(item.Element("propId")!.Attribute("value")!.Value)));
        Assert.Empty(result.Messages);
    }

    private static XDocument BuildPropVariationXml(params XElement[] props)
        => new(
            new XElement("CPedVariationInfo",
                new XElement("availComp", "255 255 255 255 255 255 255 255 255 255 255 255"),
                new XElement("aComponentData3", new XAttribute("itemType", "CPVComponentData")),
                new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
                new XElement("propInfo",
                    new XElement("aPropMetaData", new XAttribute("itemType", "CPedPropMetaData"), props)),
                new XElement("dlcName", "hash_00000000")));

    private static XDocument BuildMinimalPedVariationXml()
        => new(
            new XElement("CPedVariationInfo",
                new XElement("availComp", "255 255 255 255 255 255 255 255 255 255 255 255"),
                new XElement("aComponentData3", new XAttribute("itemType", "CPVComponentData")),
                new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
                new XElement("dlcName", "hash_00000000")));
}
