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
}
