using System.Xml.Linq;
using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;

namespace ClothingRepacker.Tests;

public class CreatureMetadataTests
{
    [Fact]
    public async Task BuildRecreatesCreatureMetadataWithRemappedIndexes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"creature-metadata-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9);
        WriteResource(resources, "pack_b", componentExpressionIndex: 8, propExpressionIndex: 10);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        Assert.Equal(2, analyze.Plan.SourceCreatureMetadata.Count);

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var xml = XDocument.Load(metadataPath);

        Assert.Equal("CCreatureMetaData", xml.Root?.Name.LocalName);
        Assert.Equal([0, 1], ReadValues(xml, "pedCompExpressions", "pedCompVarIndex"));
        Assert.Equal([4, 8], ReadValues(xml, "pedCompExpressions", "pedCompExpressionIndex"));
        Assert.Equal([-1, 0, 1], ReadValues(xml, "pedPropExpressions", "pedPropVarIndex"));
        Assert.Equal([0, 9, 10], ReadValues(xml, "pedPropExpressions", "pedPropExpressionIndex"));
        Assert.Single(xml.Root!.Element("shaderVariableComponents")!.Elements("Item"));
    }

    [Fact]
    public async Task BuildWritesEmptyCreatureMetadataWhenNoSourceMetadataExists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"empty-creature-metadata-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9, includeCreatureMetadata: false);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var xml = XDocument.Load(metadataPath);

        Assert.Empty(analyze.Plan.SourceCreatureMetadata);
        Assert.Equal("CCreatureMetaData", xml.Root?.Name.LocalName);
        Assert.Empty(xml.Root!.Element("pedCompExpressions")!.Elements("Item"));
        Assert.Empty(xml.Root!.Element("pedPropExpressions")!.Elements("Item"));
    }

    private static void WriteResource(string resourcesRoot, string collectionName, int componentExpressionIndex, int propExpressionIndex, bool includeCreatureMetadata = true)
    {
        var stream = Path.Combine(resourcesRoot, collectionName, "stream");
        Directory.CreateDirectory(stream);
        new XDocument(BuildPedVariation(collectionName)).Save(Path.Combine(stream, $"mp_f_freemode_01_{collectionName}.ymt.xml"));
        if (includeCreatureMetadata)
        {
            new XDocument(BuildCreatureMetadata(componentExpressionIndex, propExpressionIndex)).Save(Path.Combine(stream, "mp_creaturemetadata.ymt.xml"));
        }
    }

    private static XElement BuildPedVariation(string collectionName)
        => new("CPedVariationInfo",
            new XAttribute("name", collectionName),
            new XElement("availComp", "255 255 255 255 255 255 0 255 255 255 255 255"),
            new XElement("aComponentData3",
                new XAttribute("itemType", "CPVComponentData"),
                new XElement("Item",
                    new XElement("numAvailTex", new XAttribute("value", 1)),
                    new XElement("aDrawblData3",
                        new XAttribute("itemType", "CPVDrawblData"),
                        new XElement("Item",
                            new XElement("aTexData", new XAttribute("itemType", "CPVTextureData")))))),
            new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
            new XElement("propInfo",
                new XElement("numAvailProps", new XAttribute("value", 1)),
                new XElement("aPropMetaData",
                    new XAttribute("itemType", "CPedPropMetaData"),
                    new XElement("Item",
                        new XElement("anchorId", new XAttribute("value", 0)),
                        new XElement("propId", new XAttribute("value", 0)),
                        new XElement("aTexData", new XAttribute("itemType", "CPVTextureData")))),
                new XElement("aAnchors", new XAttribute("itemType", "CAnchorProps"))),
            new XElement("dlcName", "hash_00000000"));

    private static XElement BuildCreatureMetadata(int componentExpressionIndex, int propExpressionIndex)
        => new("CCreatureMetaData",
            new XElement("shaderVariableComponents",
                new XAttribute("itemType", "CShaderVariableComponent"),
                new XElement("Item",
                    new XElement("pedcompID", new XAttribute("value", 6)),
                    new XElement("maskID", new XAttribute("value", 1)),
                    new XElement("shaderVariableHashString", new XAttribute("value", 1234)),
                    new XElement("tracks", "33"),
                    new XElement("ids", "28462"),
                    new XElement("components", "1"))),
            new XElement("pedPropExpressions",
                new XAttribute("itemType", "CPedPropExpressionData"),
                BuildPropExpression(-1, 0),
                BuildPropExpression(0, propExpressionIndex)),
            new XElement("pedCompExpressions",
                new XAttribute("itemType", "CPedCompExpressionData"),
                BuildCompExpression(0, componentExpressionIndex)));

    private static XElement BuildCompExpression(int varIndex, int expressionIndex)
        => new("Item",
            new XElement("pedCompID", new XAttribute("value", 6)),
            new XElement("pedCompVarIndex", new XAttribute("value", varIndex)),
            new XElement("pedCompExpressionIndex", new XAttribute("value", expressionIndex)),
            new XElement("tracks", "33"),
            new XElement("ids", "28462"),
            new XElement("types", "2"),
            new XElement("components", "1"));

    private static XElement BuildPropExpression(int varIndex, int expressionIndex)
        => new("Item",
            new XElement("pedPropID", new XAttribute("value", 0)),
            new XElement("pedPropVarIndex", new XAttribute("value", varIndex)),
            new XElement("pedPropExpressionIndex", new XAttribute("value", expressionIndex)),
            new XElement("tracks", "33"),
            new XElement("ids", "13201"),
            new XElement("types", "2"),
            new XElement("components", "1"));

    private static int[] ReadValues(XDocument xml, string containerName, string valueElementName)
        => xml.Root!.Element(containerName)!.Elements("Item")
            .Select(item => int.Parse(item.Element(valueElementName)!.Attribute("value")!.Value))
            .ToArray();
}
