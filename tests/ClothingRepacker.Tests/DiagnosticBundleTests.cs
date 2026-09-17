using System.IO.Compression;
using ClothingRepacker.Core.Services;

namespace ClothingRepacker.Tests;

public class DiagnosticBundleTests
{
    [Fact]
    public async Task BundlePreservesDiagnosticTextAndReplacesOtherContents()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"diagnostic-bundle-test-{Guid.NewGuid():N}");
        var source = Path.Combine(testRoot, "clothing-pack");
        var output = Path.Combine(testRoot, "support", "bundle.zip");
        Directory.CreateDirectory(Path.Combine(source, "data"));
        Directory.CreateDirectory(Path.Combine(source, "stream"));
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "fxmanifest.lua"), "fx_version 'cerulean'");
        await File.WriteAllTextAsync(Path.Combine(source, "data", "shop.meta"), "<ShopPedApparel />");
        await File.WriteAllBytesAsync(Path.Combine(source, "stream", "model.ydd"), [1, 2, 3, 4, 5]);
        await File.WriteAllTextAsync(Path.Combine(source, ".git", "config"), "private repository data");

        try
        {
            var result = await new DiagnosticBundleService().CreateAsync([source], output);

            Assert.Equal(Path.GetFullPath(output), result.OutputPath);
            Assert.Equal(3, result.FileCount);
            Assert.Equal(2, result.PreservedFileCount);
            Assert.Equal(1, result.PlaceholderFileCount);
            using var archive = ZipFile.OpenRead(output);
            Assert.Equal(
                "fx_version 'cerulean'",
                await ReadEntryAsync(archive, "clothing-pack/fxmanifest.lua"));
            Assert.Equal(
                "<ShopPedApparel />",
                await ReadEntryAsync(archive, "clothing-pack/data/shop.meta"));
            Assert.Equal(
                ".",
                await ReadEntryAsync(archive, "clothing-pack/stream/model.ydd"));
            Assert.Contains(archive.Entries, entry => entry.FullName == "clothing-pack/empty/");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("clothing-pack/.git/", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Missing ZIP entry: {path}");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
