using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using System.Xml.Linq;
using ClothingRepacker.CodeWalker;

namespace ClothingRepacker.Tests;

public class ShopMetaGenerationTests
{
    [Fact]
    public async Task BuildGeneratesShopPedApparelMetaInSampleShape()
    {
        var root = Path.Combine(Path.GetTempPath(), $"shop-meta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var resources = Path.Combine(root, "resources");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), Path.Combine(resources, "gang_flags"));

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
        Assert.NotNull(xml.Root?.Element("pedComponents"));
        Assert.NotNull(xml.Root?.Element("pedProps"));
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

}
