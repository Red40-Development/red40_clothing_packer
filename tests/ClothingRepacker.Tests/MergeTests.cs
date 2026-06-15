using ClothingRepacker.Core.Hashing;
using ClothingRepacker.Core.Planning;
using ClothingRepacker.Core.Xml;
using System.Xml.Linq;

namespace ClothingRepacker.Tests;

public class MergeTests
{
    [Fact]
    public void MergesFixturesIntoExpectedCollection()
    {
        var reader = new PedVariationReader();
        var red40 = reader.Read(XDocument.Load(Fixture("mp_f_freemode_01_red40_clothes.ymt.xml")), Fixture("mp_f_freemode_01_red40_clothes.ymt.xml"), "red40", "red40");
        var accessories = reader.Read(XDocument.Load(Fixture("mp_f_freemode_01_mp_f_accessoriesx2.ymt.xml")), Fixture("mp_f_freemode_01_mp_f_accessoriesx2.ymt.xml"), "accs", "accs");

        var builder = new OutputCollectionBuilder("merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", ClothingRepacker.Core.Models.PedGender.Female);
        builder.AddComponents(red40);
        builder.AddProps(red40);
        builder.AddComponents(accessories);
        builder.AddProps(accessories);

        var xml = builder.BuildXml();
        Assert.Equal("255 0 255 255 1 255 255 2 255 255 255 3", xml.Root!.Element("availComp")!.Value.Trim());
        Assert.Equal(5, xml.Root.Element("compInfos")!.Elements("Item").Count());
        Assert.Equal("1", xml.Root.Element("propInfo")!.Element("numAvailProps")!.Attribute("value")!.Value);
        Assert.Contains("ANCHOR_EYES", xml.Root.Element("propInfo")!.ToString());
        Assert.Contains("12", xml.Root.Element("propInfo")!.ToString());
        Assert.Equal($"hash_{JenkHash.Hash("merged_f_001"):X8}", xml.Root.Element("dlcName")!.Value.Trim());
    }

    private static string Fixture(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ymts", fileName);
}
