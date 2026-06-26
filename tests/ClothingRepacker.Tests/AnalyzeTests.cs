using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;

namespace ClothingRepacker.Tests;

public class AnalyzeTests
{
    [Fact]
    public async Task AnalyzeRecordsErroredFilePathForInvalidSourceXml()
    {
        var root = Path.Combine(Path.GetTempPath(), $"analyze-error-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resourceRoot = Path.Combine(resources, "bad_resource");
        var streamRoot = Path.Combine(resourceRoot, "stream");
        Directory.CreateDirectory(streamRoot);

        await File.WriteAllTextAsync(Path.Combine(resourceRoot, "fxmanifest.lua"), "fx_version 'cerulean'");

        var invalidPath = Path.Combine(streamRoot, "broken.ymt.xml");
        await File.WriteAllTextAsync(invalidPath, """
<CPedVariationInfo>
  <dlcName>hash_deadbeef</dlcName>
</CPedVariationInfo>
""");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var result = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());

        Assert.Contains(result.Plan.Errors, error => error.Contains(invalidPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Plan.Errors, error => error.Contains("Missing element 'availComp'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeReportsProgressWhileProcessingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"analyze-progress-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var resources = Path.Combine(root, "resources");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), Path.Combine(resources, "gang_flags"));

        var updates = new List<OperationProgress>();
        var progress = new Progress<OperationProgress>(update => updates.Add(update));
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

        var result = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings(), progress);

        Assert.NotEmpty(updates);
        Assert.Contains(updates, update => update.Operation == "analyze" && update.Stage == "process-source" && update.Current > 0);

        var completed = Assert.Single(updates, update => update.Operation == "analyze" && update.Stage == "complete");
        Assert.Equal(result.Plan.SourceYmts.Count, completed.SourceCount);
        Assert.Equal(result.Plan.TargetCollections.Count, completed.TargetCount);
        Assert.Equal(result.Plan.Errors.Count, completed.ErrorCount);
        Assert.Equal(result.Plan.Warnings.Count, completed.WarningCount);
    }
}
