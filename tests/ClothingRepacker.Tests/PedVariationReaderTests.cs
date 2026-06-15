using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Xml;
using System.Xml.Linq;

namespace ClothingRepacker.Tests;

public class PedVariationReaderTests
{
    private readonly PedVariationReader _reader = new();

    [Fact]
    public void ReadsRed40Fixture()
    {
        var doc = XDocument.Load(Fixture("mp_f_freemode_01_red40_clothes.ymt.xml"));
        var result = _reader.Read(doc, Fixture("mp_f_freemode_01_red40_clothes.ymt.xml"), "red40", "red40");

        Assert.Equal("red40_clothes", result.CollectionName);
        Assert.Equal("hash_7F16400D", result.DlcName);
        Assert.Equal(PedGender.Female, result.Gender);
        Assert.Equal(1, result.Components.Single(c => c.ComponentId == 1).Drawables.Count);
        Assert.Equal(1, result.Components.Single(c => c.ComponentId == 4).Drawables.Count);
        Assert.Equal(2, result.Components.Single(c => c.ComponentId == 11).Drawables.Count);
        Assert.Empty(result.Props);
    }

    [Fact]
    public void ReadsAccessoriesFixture()
    {
        var doc = XDocument.Load(Fixture("mp_f_freemode_01_mp_f_accessoriesx2.ymt.xml"));
        var result = _reader.Read(doc, Fixture("mp_f_freemode_01_mp_f_accessoriesx2.ymt.xml"), "accs", "accs");

        Assert.Equal("mp_f_accessoriesx2", result.CollectionName);
        Assert.Equal("hash_6BE06652", result.DlcName);
        Assert.Equal(1, result.Components.Single(c => c.ComponentId == 7).Drawables.Count);
        var prop = result.Props.Single(p => p.AnchorId == 1);
        Assert.Single(prop.Props);
        Assert.Equal(12, prop.Props.Single().Element("aTexData")!.Elements("Item").Count());
    }

    private static string Fixture(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ymts", fileName);
}
