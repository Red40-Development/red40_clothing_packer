using System.Xml.Linq;
using ClothingRepacker.CodeWalker;
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

    private static bool IsRbfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return RbfFile.IsRBF(stream);
    }
}
