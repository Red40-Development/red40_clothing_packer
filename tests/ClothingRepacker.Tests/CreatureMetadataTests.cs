using System.Xml.Linq;
using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using ClothingRepacker.Core.Xml;
using CodeWalker.GameFiles;

namespace ClothingRepacker.Tests;

public class CreatureMetadataTests
{
    [Fact]
    public void ReaderAcceptsHexValueAttributes()
    {
        var reader = new CreatureMetadataReader();
        var xml = new XDocument(
            new XElement("CCreatureMetaData",
                new XElement("shaderVariableComponents", new XAttribute("itemType", "CShaderVariableComponent")),
                new XElement("pedPropExpressions",
                    new XAttribute("itemType", "CPedPropExpressionData"),
                    new XElement("Item",
                        new XElement("pedPropID", new XAttribute("value", "0x0")),
                        new XElement("pedPropVarIndex", new XAttribute("value", "0xFFFFFFFF")),
                        new XElement("pedPropExpressionIndex", new XAttribute("value", "0x0")))),
                new XElement("pedCompExpressions",
                    new XAttribute("itemType", "CPedCompExpressionData"),
                    new XElement("Item",
                        new XElement("pedCompID", new XAttribute("value", "0x6")),
                        new XElement("pedCompVarIndex", new XAttribute("value", "0x2")),
                        new XElement("pedCompExpressionIndex", new XAttribute("value", "0x4"))))));

        var metadata = reader.Read(xml, "/tmp/mp_creaturemetadata.ymt.xml", "test", "/tmp");

        Assert.Equal(6, metadata.ComponentExpressions.Single().SlotId);
        Assert.Equal(2, metadata.ComponentExpressions.Single().VariationIndex);
        Assert.Equal(-1, metadata.PropExpressions.Single().VariationIndex);
    }

    [Fact]
    public async Task BuildPreservesBaseCreatureMetadataForFemaleAndMaleFreemodeFixtures()
    {
        var root = Path.Combine(Path.GetTempPath(), $"base-creature-metadata-fixture-test-{Guid.NewGuid():N}");
        var stream = Path.Combine(root, "resources", "base_pack", "stream");
        Directory.CreateDirectory(stream);
        File.Copy(TestFixturePaths.Ymt("mp_f_freemode_01.ymt.xml"), Path.Combine(stream, "mp_f_freemode_01.ymt.xml"));
        File.Copy(TestFixturePaths.Ymt("mp_m_freemode_01.ymt.xml"), Path.Combine(stream, "mp_m_freemode_01.ymt.xml"));
        File.Copy(TestFixturePaths.Ymt("mp_creaturemetadata.ymt.xml"), Path.Combine(stream, "mp_creaturemetadata.ymt.xml"));
        new XDocument(BuildShopMeta("mp_creaturemetadata", string.Empty, "mp_f_freemode_01")).Save(Path.Combine(root, "resources", "base_pack", "shop_f.meta"));
        new XDocument(BuildShopMeta("mp_creaturemetadata", string.Empty, "mp_m_freemode_01")).Save(Path.Combine(root, "resources", "base_pack", "shop_m.meta"));

        var resources = Path.Combine(root, "resources");
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = true,
        });

        var expected = XDocument.Load(TestFixturePaths.Ymt("mp_creaturemetadata.ymt.xml"));
        var sharedMetadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_001.ymt.xml");
        var femaleShopMetaPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "data",
            "mp_f_freemode_01_merged_f_001.meta");
        var maleShopMetaPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "data",
            "mp_m_freemode_01_merged_m_001.meta");

        Assert.Equal(2, analyze.Plan.SourceYmts.Count);
        Assert.Single(analyze.Plan.SourceCreatureMetadata);
        Assert.Single(analyze.Plan.CreatureMetadataOutputs);
        Assert.Equal(2, analyze.Plan.TargetCollections.Count);
        var sharedMetadataXml = XDocument.Load(sharedMetadataPath);
        AssertXmlEqual(expected, sharedMetadataXml);
        AssertValueAttributesAreHex(sharedMetadataXml);
        AssertYmtIsRbf(Path.ChangeExtension(sharedMetadataPath, null));
        Assert.Equal("MP_CreatureMetadata_merged_001", XDocument.Load(femaleShopMetaPath).Root?.Element("creatureMetaData")?.Value.Trim());
        Assert.Equal("MP_CreatureMetadata_merged_001", XDocument.Load(maleShopMetaPath).Root?.Element("creatureMetaData")?.Value.Trim());
    }

    [Fact]
    public async Task AnalyzeAcceptsBinaryCreatureMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"binary-creature-metadata-fixture-test-{Guid.NewGuid():N}");
        var stream = Path.Combine(root, "resources", "base_pack", "stream");
        Directory.CreateDirectory(stream);
        File.Copy(TestFixturePaths.Ymt("mp_f_freemode_01.ymt"), Path.Combine(stream, "mp_f_freemode_01.ymt"));
        File.Copy(TestFixturePaths.Ymt("mp_creaturemetadata.ymt"), Path.Combine(stream, "mp_creaturemetadata.ymt"));
        new XDocument(BuildShopMeta("mp_creaturemetadata", string.Empty)).Save(Path.Combine(root, "resources", "base_pack", "shop.meta"));

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

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = true,
        });

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var expectedPath = TestFixturePaths.Ymt("expected_merged_three_pack_creaturemetadata.ymt.xml");

        Assert.Equal(3, analyze.Plan.SourceCreatureMetadata.Count);
        var metadataXml = XDocument.Load(metadataPath);
        AssertXmlEqual(XDocument.Load(expectedPath), metadataXml);
        AssertValueAttributesAreHex(metadataXml);
        AssertYmtIsRbf(Path.ChangeExtension(metadataPath, null));
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

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = true,
        });

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
        AssertValueAttributesAreHex(xml);
        AssertYmtIsRbf(Path.ChangeExtension(metadataPath, null));
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

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = true,
        });

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var xml = XDocument.Load(metadataPath);

        Assert.Empty(analyze.Plan.SourceCreatureMetadata);
        Assert.Equal("CCreatureMetaData", xml.Root?.Name.LocalName);
        AssertValueAttributesAreHex(xml);
        Assert.Empty(xml.Root!.Element("pedCompExpressions")!.Elements("Item"));
        Assert.Empty(xml.Root!.Element("pedPropExpressions")!.Elements("Item"));
    }

    [Fact]
    public async Task AnalyzeWarnsAndBuildSkipsCreatureMetadataWhenShopReferenceIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"missing-creature-metadata-reference-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        WriteResource(resources, "pack_a", componentExpressionIndex: 4, propExpressionIndex: 9, includeCreatureMetadata: false);
        new XDocument(BuildShopMeta("mp_creaturemetadata", "pack_a")).Save(Path.Combine(resources, "pack_a", "shop.meta"));

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

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = true,
        });

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var xml = XDocument.Load(metadataPath);

        Assert.Empty(analyze.Plan.SourceCreatureMetadata);
        Assert.Equal([0, 1], ReadValues(xml, "pedCompExpressions", "pedCompVarIndex"));
        Assert.Equal([4, 4], ReadValues(xml, "pedCompExpressions", "pedCompExpressionIndex"));
        AssertValueAttributesAreHex(xml);
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

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = true,
        });

        var metadataPath = Path.Combine(
            outputRoot,
            "zz_merged_clothing_meta",
            "stream",
            "MP_CreatureMetadata_merged_f_001.ymt.xml");
        var xml = XDocument.Load(metadataPath);

        Assert.Empty(analyze.Plan.SourceCreatureMetadata);
        Assert.Empty(xml.Root!.Element("pedCompExpressions")!.Elements("Item"));
        Assert.Equal([-1, 0, 1], ReadValues(xml, "pedPropExpressions", "pedPropVarIndex"));
        Assert.Equal(["0xFFFFFFFF", "0x0", "0x0"], ReadValueStrings(xml, "pedPropExpressions", "pedPropExpressionIndex"));
        AssertValueAttributesAreHex(xml);
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

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = true,
        });

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
        AssertValueAttributesAreHex(xml);
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
                new XDocument(BuildShopMeta("mp_creaturemetadata", collectionName)).Save(Path.Combine(resourcesRoot, collectionName, "shop.meta"));
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

    private static XElement BuildShopMeta(string creatureMetadata, string collectionName = "test", string pedName = "mp_f_freemode_01")
    {
        var fullDlcName = string.IsNullOrWhiteSpace(collectionName)
            ? pedName
            : $"{pedName}_{collectionName}";
        var characterName = pedName.Equals("mp_f_freemode_01", StringComparison.OrdinalIgnoreCase)
            ? "SCR_CHAR_MULTIPLAYER_F"
            : "SCR_CHAR_MULTIPLAYER";

        return BuildShopMeta(creatureMetadata, collectionName, fullDlcName, pedName, characterName);
    }

    private static XElement BuildShopMeta(string creatureMetadata, string collectionName, string fullDlcName, string pedName, string characterName)
        => new("ShopPedApparel",
            new XElement("pedName", pedName),
            new XElement("dlcName", collectionName),
            new XElement("fullDlcName", fullDlcName),
            new XElement("eCharacter", characterName),
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
            .Select(item => XmlHelpers.ParseIntValue(item.Element(valueElementName)!.Attribute("value")!.Value))
            .ToArray();

    private static string[] ReadValueStrings(XDocument xml, string containerName, string valueElementName)
        => xml.Root!.Element(containerName)!.Elements("Item")
            .Select(item => item.Element(valueElementName)!.Attribute("value")!.Value)
            .ToArray();

    private static void AssertXmlEqual(XDocument expected, XDocument actual)
    {
        var normalizedExpected = NormalizeCreatureMetadataValueAttributes(expected);
        var normalizedActual = NormalizeCreatureMetadataValueAttributes(actual);

        Assert.Equal(
            normalizedExpected.ToString(SaveOptions.DisableFormatting),
            normalizedActual.ToString(SaveOptions.DisableFormatting));
    }

    private static XDocument NormalizeCreatureMetadataValueAttributes(XDocument xml)
    {
        var clone = new XDocument(xml);
        foreach (var attribute in clone.Descendants().Attributes("value"))
        {
            attribute.Value = XmlHelpers.ParseIntValue(attribute.Value).ToString();
        }

        return clone;
    }

    private static void AssertValueAttributesAreHex(XDocument xml)
    {
        var nonHexValues = xml.Descendants()
            .Attributes("value")
            .Where(attribute => !attribute.Value.Trim().StartsWith("0x", StringComparison.Ordinal))
            .Select(attribute => $"{attribute.Parent?.Name.LocalName}={attribute.Value}")
            .ToList();

        Assert.Empty(nonHexValues);
    }

    private static void AssertYmtIsRbf(string path)
    {
        using var stream = File.OpenRead(path);
        Assert.True(RbfFile.IsRBF(stream));
    }
}
