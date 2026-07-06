using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using System.Xml.Linq;
using ClothingRepacker.CodeWalker;

namespace ClothingRepacker.Tests;

public class ShopMetaGenerationTests
{
    [Fact]
    public async Task BuildSkipsShopItemsWhenSourceShopEntriesAreMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shop-meta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var resources = Path.Combine(root, "resources");
        var gangFlagsResource = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), gangFlagsResource);
        DeleteShopMetaFiles(gangFlagsResource);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var metaPath = Path.Combine(outputRoot, "zz_merged_clothing_meta", "data", "mp_f_freemode_01_merged_f_001.meta");
        var xml = XDocument.Load(metaPath);

        Assert.Equal("ShopPedApparel", xml.Root?.Name.LocalName);
        Assert.Equal("mp_f_freemode_01", xml.Root?.Element("pedName")?.Value.Trim());
        Assert.Equal("merged_f_001", xml.Root?.Element("dlcName")?.Value.Trim());
        Assert.Equal("mp_f_freemode_01_merged_f_001", xml.Root?.Element("fullDlcName")?.Value.Trim());
        Assert.Equal("SCR_CHAR_MULTIPLAYER_F", xml.Root?.Element("eCharacter")?.Value.Trim());
        Assert.Equal("MP_CreatureMetadata_merged_f_001", xml.Root?.Element("creatureMetaData")?.Value.Trim());
        Assert.NotNull(xml.Root?.Element("pedOutfits"));
        Assert.Empty(xml.Root!.Element("pedComponents")!.Elements("Item"));
        Assert.Empty(xml.Root!.Element("pedProps")!.Elements("Item"));
    }

    [Fact]
    public async Task BuildGeneratesShopPedComponentEntriesFromSourceShopMeta()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shop-meta-component-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resource = Path.Combine(resources, "component_pack");
        var stream = Path.Combine(resource, "stream");
        Directory.CreateDirectory(stream);
        new XDocument(BuildPedVariationWithComponent()).Save(Path.Combine(stream, "mp_m_freemode_01_component_pack.ymt.xml"));
        new XDocument(BuildShopMetaWithComponent()).Save(Path.Combine(resource, "mp_m_freemode_01_component_pack.meta"));

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var metaPath = Path.Combine(outputRoot, "zz_merged_clothing_meta", "data", "mp_m_freemode_01_merged_m_001.meta");
        var xml = XDocument.Load(metaPath);

        var component = Assert.Single(xml.Root!.Element("pedComponents")!.Elements("Item"));
        var componentComment = Assert.IsType<XComment>(component.PreviousNode);
        Assert.Equal("Accessories: Accessories - mp_m_freemode_01_component_pack^teef_diff_000_b_uni", componentComment.Value.Trim());
        Assert.Empty(component.Element("lockHash")!.Value);
        Assert.Empty(component.Element("textLabel")!.Value);
        Assert.Equal("mp_m_freemode_01_merged_m_001_teef_000_01", component.Element("uniqueNameHash")!.Value.Trim());
        Assert.Equal("PV_COMP_TEEF", component.Element("eCompType")!.Value.Trim());
        Assert.Equal("0", component.Element("drawableIndex")!.Attribute("value")!.Value);
        Assert.Equal("0", component.Element("localDrawableIndex")!.Attribute("value")!.Value);
        Assert.Equal("1", component.Element("textureIndex")!.Attribute("value")!.Value);
        Assert.NotNull(component.Element("restrictionTags")!.Element("Item"));
        Assert.NotNull(component.Element("forcedComponents")!.Element("Item"));
        Assert.NotNull(component.Element("variantComponents")!.Element("Item"));
        Assert.Null(component.Element("componentId"));
        Assert.Null(component.Element("drawableId"));
        Assert.Null(component.Element("textureId"));
    }

    [Fact]
    public async Task BuildGeneratesShopPedPropEntriesFromSourceShopMeta()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shop-meta-prop-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resource = Path.Combine(resources, "prop_pack");
        var stream = Path.Combine(resource, "stream");
        Directory.CreateDirectory(stream);
        new XDocument(BuildPedVariationWithProp()).Save(Path.Combine(stream, "mp_f_freemode_01_prop_pack.ymt.xml"));
        new XDocument(BuildShopMetaWithProp()).Save(Path.Combine(resource, "mp_f_freemode_01_prop_pack.meta"));

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var metaPath = Path.Combine(outputRoot, "zz_merged_clothing_meta", "data", "mp_f_freemode_01_merged_f_001.meta");
        var xml = XDocument.Load(metaPath);

        Assert.Equal("MP_CreatureMetadata_merged_f_001", xml.Root?.Element("creatureMetaData")?.Value.Trim());
        var prop = Assert.Single(xml.Root!.Element("pedProps")!.Elements("Item"));
        var propComment = Assert.IsType<XComment>(prop.PreviousNode);
        Assert.Equal("Props: Glasses - mp_f_freemode_01_prop_pack^p_eyes_diff_000_a", propComment.Value.Trim());

        Assert.Empty(prop.Element("lockHash")!.Value);
        Assert.Empty(prop.Element("textLabel")!.Value);
        Assert.Equal("mp_f_freemode_01_merged_f_001_p_eyes_000_00", prop.Element("uniqueNameHash")!.Value.Trim());
        Assert.Equal("ANCHOR_EYES", prop.Element("eAnchorPoint")!.Value.Trim());
        Assert.Equal("0", prop.Element("propIndex")!.Attribute("value")!.Value);
        Assert.Equal("0", prop.Element("localPropIndex")!.Attribute("value")!.Value);
        Assert.Equal("0", prop.Element("textureIndex")!.Attribute("value")!.Value);
        Assert.NotNull(prop.Element("restrictionTags")!.Element("Item"));
        Assert.NotNull(prop.Element("forcedComponents"));
        Assert.NotNull(prop.Element("forcedProps")!.Element("Item"));
        Assert.NotNull(prop.Element("variantComponents"));
        Assert.NotNull(prop.Element("variantProps")!.Element("Item"));
        Assert.Null(prop.Element("anchorId"));
        Assert.Null(prop.Element("propId"));
        Assert.Null(prop.Element("textureId"));
    }

    [Fact]
    public async Task BuildCanSkipPreviewXmlAndDebugClientArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shop-meta-build-options-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var resources = Path.Combine(root, "resources");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), Path.Combine(resources, "gang_flags"));

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = false,
            IncludeDebugClient = false,
        });

        var resourceRoot = Path.Combine(outputRoot, "zz_merged_clothing_meta");
        var ymtPath = Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_merged_f_001.ymt");
        var previewXmlPath = ymtPath + ".xml";
        var validationPath = Path.Combine(resourceRoot, "client", "validate_collections.lua");
        var fxmanifestPath = Path.Combine(resourceRoot, "fxmanifest.lua");

        Assert.True(File.Exists(ymtPath));
        Assert.False(File.Exists(previewXmlPath));
        Assert.False(File.Exists(validationPath));
        Assert.DoesNotContain("client_script 'client/validate_collections.lua'", await File.ReadAllTextAsync(fxmanifestPath));
    }

    [Theory]
    [InlineData("mp_f_freemode_01_mp_f_gang_flags.meta", "mp_f_freemode_01", "mp_f_gang_flags", "SCR_CHAR_MULTIPLAYER_F")]
    [InlineData("mp_m_freemode_01_mp_m_gang_flags.meta", "mp_m_freemode_01", "mp_m_gang_flags", "SCR_CHAR_MULTIPLAYER")]
    public void SampleFixturesMatchExpectedTopLevelShape(string fileName, string pedName, string dlcName, string characterName)
    {
        var xml = XDocument.Load(TestFixturePaths.Meta(fileName));

        Assert.Equal("ShopPedApparel", xml.Root?.Name.LocalName);
        Assert.Equal(pedName, xml.Root?.Element("pedName")?.Value.Trim());
        Assert.Equal(dlcName, xml.Root?.Element("dlcName")?.Value.Trim());
        Assert.NotNull(xml.Root?.Element("fullDlcName"));
        Assert.Equal(characterName, xml.Root?.Element("eCharacter")?.Value.Trim());
        Assert.NotNull(xml.Root?.Element("creatureMetaData"));
        Assert.NotNull(xml.Root?.Element("pedOutfits"));
        Assert.NotNull(xml.Root?.Element("pedComponents"));
        Assert.NotNull(xml.Root?.Element("pedProps"));
    }

    private static XElement BuildPedVariationWithProp()
        => new("CPedVariationInfo",
            new XAttribute("name", "prop_pack"),
            new XElement("availComp", "255 255 255 255 255 255 255 255 255 255 255 255"),
            new XElement("aComponentData3", new XAttribute("itemType", "CPVComponentData")),
            new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
            new XElement("propInfo",
                new XElement("numAvailProps", new XAttribute("value", 1)),
                new XElement("aPropMetaData",
                    new XAttribute("itemType", "CPedPropMetaData"),
                    new XElement("Item",
                        new XElement("anchorId", new XAttribute("value", 1)),
                        new XElement("propId", new XAttribute("value", 0)),
                        new XElement("aTexData",
                            new XAttribute("itemType", "CPVTextureData"),
                            new XElement("Item", new XElement("texId", new XAttribute("value", 0))),
                            new XElement("Item", new XElement("texId", new XAttribute("value", 1)))))),
                new XElement("aAnchors", new XAttribute("itemType", "CAnchorProps"))),
            new XElement("dlcName", "hash_00000000"));

    private static XElement BuildPedVariationWithComponent()
        => new("CPedVariationInfo",
            new XAttribute("name", "component_pack"),
            new XElement("availComp", "255 255 255 255 255 255 255 0 255 255 255 255"),
            new XElement("aComponentData3",
                new XAttribute("itemType", "CPVComponentData"),
                new XElement("Item",
                    new XElement("numAvailTex", new XAttribute("value", 2)),
                    new XElement("aDrawblData3",
                        new XAttribute("itemType", "CPVDrawblData"),
                        new XElement("Item",
                            new XElement("aTexData",
                                new XAttribute("itemType", "CPVTextureData"),
                                new XElement("Item", new XElement("texId", new XAttribute("value", 0))),
                                new XElement("Item", new XElement("texId", new XAttribute("value", 1)))))))),
            new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
            new XElement("propInfo",
                new XElement("numAvailProps", new XAttribute("value", 0)),
                new XElement("aPropMetaData", new XAttribute("itemType", "CPedPropMetaData")),
                new XElement("aAnchors", new XAttribute("itemType", "CAnchorProps"))),
            new XElement("dlcName", "hash_00000000"));

    private static XElement BuildShopMetaWithComponent()
        => new("ShopPedApparel",
            new XElement("pedName", "mp_m_freemode_01"),
            new XElement("dlcName", "component_pack"),
            new XElement("fullDlcName", "mp_m_freemode_01_component_pack"),
            new XElement("eCharacter", "SCR_CHAR_MULTIPLAYER"),
            new XElement("pedOutfits"),
            new XElement("pedComponents",
                new XComment(" Accessories: Accessories - mp_m_freemode_01_component_pack^teef_diff_000_b_uni "),
                new XElement("Item",
                    new XElement("lockHash"),
                    new XElement("cost", new XAttribute("value", 15)),
                    new XElement("textLabel"),
                    new XElement("uniqueNameHash", "SOURCE_COMPONENT"),
                    new XElement("eShopEnum", "CLO_SHOP_NONE"),
                    new XElement("locate", new XAttribute("value", -99)),
                    new XElement("scriptSaveData", new XAttribute("value", 0)),
                    new XElement("restrictionTags", new XElement("Item", "SHOP_TEST_COMPONENT")),
                    new XElement("forcedComponents", new XElement("Item", "FORCED_COMPONENT")),
                    new XElement("variantComponents", new XElement("Item", "VARIANT_COMPONENT")),
                    new XElement("drawableIndex", new XAttribute("value", 0)),
                    new XElement("localDrawableIndex", new XAttribute("value", 0)),
                    new XElement("eCompType", "PV_COMP_TEEF"),
                    new XElement("textureIndex", new XAttribute("value", 1)),
                    new XElement("isInOutfit", new XAttribute("value", "false")))),
            new XElement("pedProps"));

    private static XElement BuildShopMetaWithProp()
        => new("ShopPedApparel",
            new XElement("pedName", "mp_f_freemode_01"),
            new XElement("dlcName", "prop_pack"),
            new XElement("fullDlcName", "mp_f_freemode_01_prop_pack"),
            new XElement("eCharacter", "SCR_CHAR_MULTIPLAYER_F"),
            new XElement("pedOutfits"),
            new XElement("pedComponents"),
            new XElement("pedProps",
                new XComment(" Props: Glasses - mp_f_freemode_01_prop_pack^p_eyes_diff_000_a "),
                new XElement("Item",
                    new XElement("lockHash"),
                    new XElement("cost", new XAttribute("value", 0)),
                    new XElement("textLabel"),
                    new XElement("uniqueNameHash", "SOURCE_PROP"),
                    new XElement("eShopEnum", "CLO_SHOP_NONE"),
                    new XElement("locate", new XAttribute("value", -99)),
                    new XElement("scriptSaveData", new XAttribute("value", 0)),
                    new XElement("restrictionTags", new XElement("Item", "SHOP_TEST_PROP")),
                    new XElement("forcedComponents"),
                    new XElement("forcedProps", new XElement("Item", "FORCED_PROP")),
                    new XElement("variantComponents"),
                    new XElement("variantProps", new XElement("Item", "VARIANT_PROP")),
                    new XElement("propIndex", new XAttribute("value", 0)),
                    new XElement("localPropIndex", new XAttribute("value", 0)),
                    new XElement("eAnchorPoint", "ANCHOR_EYES"),
                    new XElement("textureIndex", new XAttribute("value", 0)),
                    new XElement("isInOutfit", new XAttribute("value", "false")))));

    private static void DeleteShopMetaFiles(string resourceRoot)
    {
        foreach (var path in Directory.GetFiles(resourceRoot, "*.meta", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
    }
}
