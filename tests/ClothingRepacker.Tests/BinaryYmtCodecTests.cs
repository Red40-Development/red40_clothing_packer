using System.Xml.Linq;
using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Planning;
using CodeWalker.GameFiles;

namespace ClothingRepacker.Tests;

public class BinaryYmtCodecTests
{
    private static readonly string[] BinaryFixtures =
    [
        "mp_f_freemode_01_mp_f_gang_flags.ymt",
        "mp_f_freemode_01_mp_f_kickenit_gangs.ymt",
        "mp_m_freemode_01_mp_m_gang_flags.ymt",
        "mp_m_freemode_01_mp_m_kickenit_gangs.ymt",
        "mp_m_freemode_01_mp_m_merryweathervests.ymt",
    ];

    private readonly CodeWalkerYmtCodec _codec = new();

    [Theory]
    [MemberData(nameof(GetBinaryFixtures))]
    public async Task DecodesBinaryFixtureToPedVariationXml(string fileName)
    {
        var path = Fixture(fileName);

        var xml = await _codec.DecodeToXmlAsync(path);

        Assert.Equal("CPedVariationInfo", xml.Root?.Name.LocalName);
        Assert.NotNull(xml.Root?.Attribute("name")?.Value);
        Assert.NotNull(xml.Root?.Element("availComp"));
        Assert.NotNull(xml.Root?.Element("aComponentData3"));
        Assert.NotNull(xml.Root?.Element("compInfos"));
        Assert.NotNull(xml.Root?.Element("propInfo"));
        Assert.NotNull(xml.Root?.Element("dlcName"));
    }

    [Fact]
    public async Task RoundTripsBinaryFixtureThroughEncoder()
    {
        var inputPath = Fixture("mp_m_freemode_01_mp_m_gang_flags.ymt");
        var tempDir = Path.Combine(Path.GetTempPath(), $"binary-ymt-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var xml = await _codec.DecodeToXmlAsync(inputPath);
        var outputPath = Path.Combine(tempDir, "roundtrip.ymt");

        await _codec.EncodeFromXmlAsync(xml, outputPath);
        var roundTrippedXml = await _codec.DecodeToXmlAsync(outputPath);

        Assert.Equal("CPedVariationInfo", roundTrippedXml.Root?.Name.LocalName);
        Assert.Equal(xml.Root?.Attribute("name")?.Value, roundTrippedXml.Root?.Attribute("name")?.Value);
        Assert.Equal(xml.Root?.Element("dlcName")?.Value.Trim(), roundTrippedXml.Root?.Element("dlcName")?.Value.Trim());
        Assert.Equal(
            xml.Root?.Element("aComponentData3")?.Elements("Item").Count(),
            roundTrippedXml.Root?.Element("aComponentData3")?.Elements("Item").Count());
    }

    [Fact]
    public async Task RoundTripsGeneratedPropsWithTextureCountsAndAllSupportedAnchors()
    {
        var source = CreatePropSource();
        var builder = new OutputCollectionBuilder(
            "merged_f_001",
            "mp_f_freemode_01_merged_f_001",
            "mp_f_freemode_01",
            PedGender.Female);
        builder.AddProps(source);

        var tempDir = Path.Combine(Path.GetTempPath(), $"binary-prop-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "mp_f_freemode_01_merged_f_001.ymt");

        await _codec.EncodeFromXmlAsync(builder.BuildXml(), outputPath);
        var roundTrippedXml = await _codec.DecodeToXmlAsync(outputPath);
        var propInfo = roundTrippedXml.Root!.Element("propInfo")!;

        Assert.Equal("5", propInfo.Element("numAvailProps")!.Attribute("value")!.Value);
        Assert.Equal(
            [0, 1, 2, 6, 7],
            propInfo.Element("aPropMetaData")!.Elements("Item")
                .Select(item => int.Parse(item.Element("anchorId")!.Attribute("value")!.Value)));
        Assert.Equal(
            ["ANCHOR_HEAD", "ANCHOR_EYES", "ANCHOR_EARS", "ANCHOR_LEFT_WRIST", "ANCHOR_RIGHT_WRIST"],
            propInfo.Element("aAnchors")!.Elements("Item").Select(item => item.Element("anchor")!.Value.Trim()));
        Assert.Equal(
            ["1", "2", "3", "4", "5"],
            propInfo.Element("aAnchors")!.Elements("Item").Select(item => item.Element("props")!.Value.Trim()));
    }

    [Fact]
    public async Task RoundTripsMaximumGeneratedComponentDrawables()
    {
        var source = CreateComponentSource(ClothingConstants.MaximumDrawablesPerComponent);
        var builder = new OutputCollectionBuilder(
            "merged_f_001",
            "mp_f_freemode_01_merged_f_001",
            "mp_f_freemode_01",
            PedGender.Female);
        var mappings = builder.AddComponents(source);
        var xml = builder.BuildXml();
        var tempDir = Path.Combine(Path.GetTempPath(), $"binary-component-limit-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "mp_f_freemode_01_merged_f_001.ymt");

        await _codec.EncodeFromXmlAsync(xml, outputPath);
        var roundTrippedXml = await _codec.DecodeToXmlAsync(outputPath);
        var component = roundTrippedXml.Root!.Element("aComponentData3")!.Element("Item")!;

        Assert.Equal(Enumerable.Range(0, ClothingConstants.MaximumDrawablesPerComponent), mappings.Select(mapping => mapping.NewDrawableIndex));
        Assert.Equal(ClothingConstants.MaximumDrawablesPerComponent, component.Element("aDrawblData3")!.Elements("Item").Count());
        Assert.Contains(component.Element("aDrawblData3")!.Elements("Item"), item =>
            item.Element("aTexData")!.Elements("Item").Any());
    }

    [Fact]
    public void ConvertsUnsignedDecimalSignedVariationIndexBeforeMetaEncoding()
    {
        var xml = new XDocument(
            new XElement("CPedVariationInfo",
                new XElement("pedCompVarIndex", new XAttribute("value", uint.MaxValue)),
                new XElement("flags", new XAttribute("value", uint.MaxValue))));

        var prepared = CodeWalkerYmtCodec.PrepareXmlForCodeWalkerMeta(xml);

        Assert.Equal("-1", prepared.Root!.Element("pedCompVarIndex")!.Attribute("value")!.Value);
        Assert.Equal("4294967295", prepared.Root.Element("flags")!.Attribute("value")!.Value);
    }

    [Fact]
    public async Task DecodesBinaryCreatureMetadataFixture()
    {
        var path = Fixture("mp_creaturemetadata.ymt");

        var xml = await _codec.DecodeToXmlAsync(path);

        Assert.Equal("CCreatureMetaData", xml.Root?.Name.LocalName);
        Assert.NotNull(xml.Root?.Element("shaderVariableComponents"));
        Assert.NotNull(xml.Root?.Element("pedPropExpressions"));
        Assert.NotNull(xml.Root?.Element("pedCompExpressions"));
    }

    [Fact]
    public async Task RoundTripsCreatureMetadataThroughRbfEncoder()
    {
        var inputPath = Fixture("mp_creaturemetadata.ymt");
        var tempDir = Path.Combine(Path.GetTempPath(), $"binary-creature-metadata-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var xml = await _codec.DecodeToXmlAsync(inputPath);
        var outputPath = Path.Combine(tempDir, "mp_creaturemetadata.ymt");

        await _codec.EncodeFromXmlAsync(xml, outputPath);
        var roundTrippedXml = await _codec.DecodeToXmlAsync(outputPath);

        Assert.True(IsRbfFile(outputPath));
        Assert.Equal("CCreatureMetaData", roundTrippedXml.Root?.Name.LocalName);
        Assert.NotNull(roundTrippedXml.Root?.Element("shaderVariableComponents"));
        Assert.NotNull(roundTrippedXml.Root?.Element("pedPropExpressions"));
        Assert.NotNull(roundTrippedXml.Root?.Element("pedCompExpressions"));
    }

    public static IEnumerable<object[]> GetBinaryFixtures()
        => BinaryFixtures.Select(path => new object[] { path });

    private static string Fixture(string fileName)
        => TestFixturePaths.Ymt(fileName);

    private static SourceYmt CreatePropSource()
    {
        var textureCounts = new Dictionary<int, int>
        {
            [0] = 1,
            [1] = 2,
            [2] = 3,
            [6] = 4,
            [7] = 5,
        };
        var props = textureCounts.Select(pair =>
            new PropBlock(pair.Key, [CreatePropItem(pair.Key, pair.Value)])).ToList();

        return new SourceYmt(
            YmtPath: "/tmp/mp_f_freemode_01_prop_source.ymt.xml",
            ResourceName: "test_resource",
            ResourceRoot: "/tmp/test_resource",
            PedBaseName: "mp_f_freemode_01",
            Gender: PedGender.Female,
            CollectionName: "prop_source",
            FullCollectionName: "mp_f_freemode_01_prop_source",
            DlcName: "hash_test",
            Xml: new XDocument(new XElement("CPedVariationInfo")),
            Components: Array.Empty<ComponentBlock>(),
            Props: props,
            CreatureComponentRepairHints: Array.Empty<CreatureComponentRepairHint>(),
            CreaturePropRepairHints: Array.Empty<CreaturePropRepairHint>(),
            Messages: Array.Empty<ValidationMessage>());
    }

    private static SourceYmt CreateComponentSource(int drawableCount)
    {
        var drawables = Enumerable.Range(0, drawableCount)
            .Select(_ => new XElement("Item",
                new XElement("propMask", new XAttribute("value", 1)),
                new XElement("numAlternatives", new XAttribute("value", 0)),
                new XElement("aTexData", new XAttribute("itemType", "CPVTextureData"),
                    new XElement("Item",
                        new XElement("texId", new XAttribute("value", 0)),
                        new XElement("distribution", new XAttribute("value", 255)))),
                new XElement("clothData",
                    new XElement("ownsCloth", new XAttribute("value", "false")))))
            .ToList();

        return new SourceYmt(
            YmtPath: "/tmp/mp_f_freemode_01_component_source.ymt.xml",
            ResourceName: "test_resource",
            ResourceRoot: "/tmp/test_resource",
            PedBaseName: "mp_f_freemode_01",
            Gender: PedGender.Female,
            CollectionName: "component_source",
            FullCollectionName: "mp_f_freemode_01_component_source",
            DlcName: "hash_test",
            Xml: new XDocument(new XElement("CPedVariationInfo")),
            Components: [new ComponentBlock(11, drawables, Array.Empty<XElement>())],
            Props: Array.Empty<PropBlock>(),
            CreatureComponentRepairHints: Array.Empty<CreatureComponentRepairHint>(),
            CreaturePropRepairHints: Array.Empty<CreaturePropRepairHint>(),
            Messages: Array.Empty<ValidationMessage>());
    }

    private static XElement CreatePropItem(int anchorId, int textureCount)
        => new("Item",
            new XElement("audioId", "none"),
            new XElement("expressionMods", "0 0 0 0 0"),
            new XElement("texData", new XAttribute("itemType", "CPedPropTexData"),
                Enumerable.Range(0, textureCount).Select(textureIndex =>
                    new XElement("Item",
                        new XElement("inclusions", 0),
                        new XElement("exclusions", 0),
                        new XElement("texId", new XAttribute("value", textureIndex)),
                        new XElement("inclusionId", new XAttribute("value", 0)),
                        new XElement("exclusionId", new XAttribute("value", 0)),
                        new XElement("distribution", new XAttribute("value", 255))))),
            new XElement("renderFlags"),
            new XElement("propFlags", new XAttribute("value", 0)),
            new XElement("flags", new XAttribute("value", 0)),
            new XElement("anchorId", new XAttribute("value", anchorId)),
            new XElement("propId", new XAttribute("value", 0)),
            new XElement("stickyness", new XAttribute("value", 0)));

    private static bool IsRbfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return RbfFile.IsRBF(stream);
    }
}
