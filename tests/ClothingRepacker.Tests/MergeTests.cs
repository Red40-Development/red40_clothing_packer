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
        var gangFlagsPath = TestFixturePaths.ResourceFile("gang_flags/stream/mp_f_freemode_01_mp_f_gang_flags.ymt.xml");
        var gangOutfitsPath = TestFixturePaths.ResourceFile("gang_outfits/stream/mp_f_freemode_01_mp_f_kickenit_gangs.ymt.xml");
        var gangFlags = reader.Read(XDocument.Load(gangFlagsPath), gangFlagsPath, "gang_flags", TestFixturePaths.ResourceDirectory("gang_flags"));
        var gangOutfits = reader.Read(XDocument.Load(gangOutfitsPath), gangOutfitsPath, "gang_outfits", TestFixturePaths.ResourceDirectory("gang_outfits"));

        var builder = new OutputCollectionBuilder("merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", ClothingRepacker.Core.Models.PedGender.Female);
        builder.AddComponents(gangFlags);
        builder.AddProps(gangFlags);
        builder.AddComponents(gangOutfits);
        builder.AddProps(gangOutfits);

        var xml = builder.BuildXml();
        Assert.Equal("255 0 255 255 255 255 255 255 255 1 2 255", xml.Root!.Element("availComp")!.Value.Trim());
        Assert.Equal(3, xml.Root.Element("compInfos")!.Elements("Item").Count());
        Assert.Equal("0", xml.Root.Element("propInfo")!.Element("numAvailProps")!.Attribute("value")!.Value);
        Assert.Equal($"hash_{JenkHash.Hash("merged_f_001"):X8}", xml.Root.Element("dlcName")!.Value.Trim());
    }
}
