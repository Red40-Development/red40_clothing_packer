using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Services;
using System.Xml.Linq;

namespace ClothingRepacker.Tests;

public class ExportXmlTests
{
    [Fact]
    public async Task ExportYmtsToXmlWritesSideBySideXmlFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"export-xml-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var sourcePath = Path.Combine(root, "mp_m_freemode_01_mp_m_gang_flags.ymt");
        File.Copy(Fixture("mp_m_freemode_01_mp_m_gang_flags.ymt"), sourcePath);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var result = await service.ExportYmtsToXmlAsync(root, overwrite: false);

        var xmlPath = sourcePath + ".xml";
        Assert.Contains(xmlPath, result.WrittenFiles);
        Assert.True(File.Exists(xmlPath));

        var xml = XDocument.Load(xmlPath);
        Assert.Equal("CPedVariationInfo", xml.Root?.Name.LocalName);
    }

    [Fact]
    public async Task ExportYmtsToXmlSkipsExistingFilesWithoutOverwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), $"export-xml-skip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var sourcePath = Path.Combine(root, "mp_f_freemode_01_mp_f_gang_flags.ymt");
        File.Copy(Fixture("mp_f_freemode_01_mp_f_gang_flags.ymt"), sourcePath);
        var xmlPath = sourcePath + ".xml";
        await File.WriteAllTextAsync(xmlPath, "<existing />");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var result = await service.ExportYmtsToXmlAsync(root, overwrite: false);

        Assert.Empty(result.WrittenFiles);
        Assert.Contains(xmlPath, result.SkippedFiles);
        Assert.Equal("<existing />", (await File.ReadAllTextAsync(xmlPath)).Trim());
    }

    private static string Fixture(string fileName)
        => TestFixturePaths.Ymt(fileName);
}
