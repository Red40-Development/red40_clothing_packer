using System.Xml.Linq;
using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;

namespace ClothingRepacker.Tests;

public class CreatureMetadataTests
{
    [Fact]
    public async Task BuildPreservesBaseCreatureMetadataForFemaleAndMaleFreemodeFixtures()
    {
        var root = Path.Combine(Path.GetTempPath(), $"base-creature-metadata-fixture-test-{Guid.NewGuid():N}");
        var stream = Path.Combine(root, "resources", "base_pack", "stream");
        Directory.CreateDirectory(stream);
        File.Copy(TestFixturePaths.Ymt("mp_f_freemode_01.ymt.xml"), Path.Combine(stream, "mp_f_freemode_01.ymt.xml"));
        File.Copy(TestFixturePaths.Ymt("mp_m_freemode_01.ymt.xml"), Path.Combine(stream, "mp_m_freemode_01.ymt.xml"));
        File.Copy(TestFixturePaths.Ymt("mp_creaturemetadata.ymt.xml"), Path.Combine(stream, "mp_creaturemetadata.ymt.xml"));
        new XDocument(BuildShopMeta("mp_creaturemetadata")).Save(Path.Combine(root, "resources", "base_pack", "shop.meta"));

        var resources = Path.Combine(root, "resources");
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var expected = XDocument.Load(TestFixturePaths.Ymt("mp_creaturemetadata.ymt.xml"));
        var femaleMetadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var maleMetadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_m_001.ymt.xml");

        Assert.Equal(2, analyze.Plan.SourceYmts.Count);
        Assert.Single(analyze.Plan.SourceCreatureMetadata);
        Assert.Equal(2, analyze.Plan.TargetCollections.Count);
        AssertXmlEqual(expected, XDocument.Load(femaleMetadataPath));
        AssertXmlEqual(expected, XDocument.Load(maleMetadataPath));
    }

    [Fact]
    public async Task AnalyzeAcceptsBinaryCreatureMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"binary-creature-metadata-fixture-test-{Guid.NewGuid():N}");
        var stream = Path.Combine(root, "resources", "base_pack", "stream");
        Directory.CreateDirectory(stream);
        File.Copy(TestFixturePaths.Ymt("mp_f_freemode_01.ymt"), Path.Combine(stream, "mp_f_freemode_01.ymt"));
        File.Copy(TestFixturePaths.Ymt("mp_creaturemetadata.ymt"), Path.Combine(stream, "mp_creaturemetadata.ymt"));
        new XDocument(BuildShopMeta("mp_creaturemetadata")).Save(Path.Combine(root, "resources", "base_pack", "shop.meta"));

        var resources = Path.Combine(root, "resources");
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());

        Assert.Empty(analyze.Plan.Errors);
        Assert.Single(analyze.Plan.SourceYmts);
        Assert.Single(analyze.Plan.SourceCreatureMetadata);
        Assert.Equal("mp_creaturemetadata.ymt", Path.GetFileName(analyze.Plan.SourceCreatureMetadata[0].Path));
    }

    [Fact]
    public async Task AnalyzeTreatsCreatureMetadataWithoutCorrespondingShopMetadataAsBroken()
    {
        var root = Path.Combine(Path.GetTempPath(), $"broken-creature-metadata-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9, includeShopMeta: false);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());

        Assert.Empty(analyze.Plan.SourceCreatureMetadata);
        Assert.Single(analyze.Plan.BrokenCreatureMetadataBackups);
        Assert.Contains(analyze.Plan.Warnings, warning => warning.Contains("has no corresponding ShopPedApparel creatureMetaData reference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildDoesNotGenerateCreatureMetadataForTargetWithBrokenSourceMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"broken-creature-metadata-build-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9, includeShopMeta: false);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        var build = await service.BuildAsync(analyze.Plan, outputRoot);

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var metaPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "data",
            "mp_f_freemode_01_merged_f_001.meta");
        var shopMeta = XDocument.Load(metaPath);

        Assert.Single(analyze.Plan.BrokenCreatureMetadataBackups);
        Assert.False(File.Exists(metadataPath));
        Assert.DoesNotContain(build.WrittenFiles, file => file.EndsWith("MP_CreatureMetadata_merged_f_001.ymt", StringComparison.OrdinalIgnoreCase));
        Assert.Null(shopMeta.Root?.Element("creatureMetaData"));
    }

    [Fact]
    public async Task BuildMergesThreeCreatureMetadataFilesIntoExpectedFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"creature-metadata-fixture-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9);
        WriteResource(resources, "pack_b", componentExpressionIndex: 8, propExpressionIndex: 10);
        WriteResource(resources, "pack_c", componentExpressionIndex: 12, propExpressionIndex: 11);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var expectedPath = TestFixturePaths.Ymt("expected_merged_three_pack_creaturemetadata.ymt.xml");

        Assert.Equal(3, analyze.Plan.SourceCreatureMetadata.Count);
        AssertXmlEqual(XDocument.Load(expectedPath), XDocument.Load(metadataPath));
    }

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

    [Fact]
    public async Task AnalyzeWarnsAndBuildSkipsCreatureMetadataWhenShopReferenceIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"missing-creature-metadata-reference-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9, includeCreatureMetadata: false);
        new XDocument(BuildShopMeta("mp_creaturemetadata")).Save(Path.Combine(resources, "pack_a", "shop.meta"));

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        var build = await service.BuildAsync(analyze.Plan, outputRoot);

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var metaPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "data",
            "mp_f_freemode_01_merged_f_001.meta");
        var shopMeta = XDocument.Load(metaPath);

        Assert.Empty(analyze.Plan.SourceCreatureMetadata);
        Assert.Empty(analyze.Plan.BrokenCreatureMetadataBackups);
        Assert.Single(analyze.Plan.MissingCreatureMetadataReferences);
        Assert.Contains(analyze.Plan.Warnings, warning => warning.Contains("references missing creature metadata 'mp_creaturemetadata'", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(metadataPath));
        Assert.DoesNotContain(build.WrittenFiles, file => file.EndsWith("MP_CreatureMetadata_merged_f_001.ymt", StringComparison.OrdinalIgnoreCase));
        Assert.Null(shopMeta.Root?.Element("creatureMetaData"));
    }

    [Fact]
    public async Task BuildRepairsHighHeelCreatureMetadataWhenSourceMetadataIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"high-heel-repair-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9, includeCreatureMetadata: false, includeHighHeelSignal: true);
        WriteResource(resources, "pack_b", componentExpressionIndex: 4, propExpressionIndex: 9, includeCreatureMetadata: false, includeHighHeelSignal: true);

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
        Assert.Equal([0, 1], ReadValues(xml, "pedCompExpressions", "pedCompVarIndex"));
        Assert.Equal([4, 4], ReadValues(xml, "pedCompExpressions", "pedCompExpressionIndex"));
        Assert.Empty(xml.Root!.Element("pedPropExpressions")!.Elements("Item"));
    }

    [Fact]
    public async Task BuildRepairsHairScalePropCreatureMetadataWhenSourceMetadataIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hair-scale-repair-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9, includeCreatureMetadata: false, includeHairScalePropSignal: true);
        WriteResource(resources, "pack_b", componentExpressionIndex: 4, propExpressionIndex: 9, includeCreatureMetadata: false, includeHairScalePropSignal: true);

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
        Assert.Empty(xml.Root!.Element("pedCompExpressions")!.Elements("Item"));
        Assert.Equal([-1, 0, 1], ReadValues(xml, "pedPropExpressions", "pedPropVarIndex"));
        Assert.Equal(["4294967295", "0", "0"], ReadValueStrings(xml, "pedPropExpressions", "pedPropExpressionIndex"));
    }

    [Fact]
    public async Task BuildDoesNotDuplicateRepairedCreatureMetadataWhenSourceMetadataExists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"creature-repair-dedupe-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 8, propExpressionIndex: 10, includeHighHeelSignal: true, includeHairScalePropSignal: true);

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

        Assert.Single(analyze.Plan.SourceCreatureMetadata);
        Assert.Equal([0], ReadValues(xml, "pedCompExpressions", "pedCompVarIndex"));
        Assert.Equal([8], ReadValues(xml, "pedCompExpressions", "pedCompExpressionIndex"));
        Assert.Equal([-1, 0], ReadValues(xml, "pedPropExpressions", "pedPropVarIndex"));
        Assert.Equal([0, 10], ReadValues(xml, "pedPropExpressions", "pedPropExpressionIndex"));
    }

    [Fact]
    public async Task BuildPreserveCreatureMetadataModeKeepsEmptyOutputWhenSourceMetadataIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"preserve-creature-metadata-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9, includeCreatureMetadata: false, includeHighHeelSignal: true, includeHairScalePropSignal: true);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings
        {
            CreatureMetadataMode = "preserve",
        });
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var xml = XDocument.Load(metadataPath);

        Assert.Equal("preserve", analyze.Plan.Settings.CreatureMetadataMode);
        Assert.Empty(xml.Root!.Element("pedCompExpressions")!.Elements("Item"));
        Assert.Empty(xml.Root!.Element("pedPropExpressions")!.Elements("Item"));
    }

    private static void WriteResource(
        string resourcesRoot,
        string collectionName,
        int componentExpressionIndex,
        int propExpressionIndex,
        bool includeCreatureMetadata = true,
        bool includeShopMeta = true,
        bool includeHighHeelSignal = false,
        bool includeHairScalePropSignal = false)
    {
        var stream = Path.Combine(resourcesRoot, collectionName, "stream");
        Directory.CreateDirectory(stream);
        new XDocument(BuildPedVariation(collectionName, includeHighHeelSignal, includeHairScalePropSignal)).Save(Path.Combine(stream, $"mp_f_freemode_01_{collectionName}.ymt.xml"));
        if (includeCreatureMetadata)
        {
            new XDocument(BuildCreatureMetadata(componentExpressionIndex, propExpressionIndex)).Save(Path.Combine(stream, "mp_creaturemetadata.ymt.xml"));
            if (includeShopMeta)
            {
                new XDocument(BuildShopMeta("mp_creaturemetadata")).Save(Path.Combine(resourcesRoot, collectionName, "shop.meta"));
            }
        }
    }

    private static XElement BuildPedVariation(string collectionName, bool includeHighHeelSignal, bool includeHairScalePropSignal)
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
            new XElement("compInfos",
                new XAttribute("itemType", "CComponentInfo"),
                includeHighHeelSignal
                    ? new XElement("Item",
                        new XElement("pedXml_compIdx", new XAttribute("value", 6)),
                        new XElement("pedXml_drawblIdx", new XAttribute("value", 0)),
                        new XElement("pedXml_expressionMods", "0 0 0 0 1"))
                    : null),
            new XElement("propInfo",
                new XElement("numAvailProps", new XAttribute("value", 1)),
                new XElement("aPropMetaData",
                    new XAttribute("itemType", "CPedPropMetaData"),
                    new XElement("Item",
                        new XElement("anchorId", new XAttribute("value", 0)),
                        new XElement("propId", new XAttribute("value", 0)),
                        includeHairScalePropSignal ? new XElement("expressionMods", "1 0 0 0 0") : null,
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

    private static XElement BuildShopMeta(string creatureMetadata)
        => new("ShopPedApparel",
            new XElement("pedName", "mp_f_freemode_01"),
            new XElement("dlcName", "test"),
            new XElement("fullDlcName", "mp_f_freemode_01_test"),
            new XElement("eCharacter", "SCR_CHAR_MULTIPLAYER_F"),
            new XElement("creatureMetaData", creatureMetadata),
            new XElement("pedOutfits", new XAttribute("itemType", "ShopPedOutfit")),
            new XElement("pedComponents", new XAttribute("itemType", "ShopPedComponent")),
            new XElement("pedProps", new XAttribute("itemType", "ShopPedProp")));

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

    private static string[] ReadValueStrings(XDocument xml, string containerName, string valueElementName)
        => xml.Root!.Element(containerName)!.Elements("Item")
            .Select(item => item.Element(valueElementName)!.Attribute("value")!.Value)
            .ToArray();

    private static void AssertXmlEqual(XDocument expected, XDocument actual)
    {
        Assert.Equal(
            expected.ToString(SaveOptions.DisableFormatting),
            actual.ToString(SaveOptions.DisableFormatting));
    }
}
