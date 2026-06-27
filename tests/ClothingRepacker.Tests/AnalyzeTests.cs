using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using System.Xml.Linq;

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

    [Fact]
    public async Task AnalyzeAndBuildCopyNonFreemodePedsIntoStandaloneResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"non-freemode-standalone-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var streamRoot = Path.Combine(resources, "animal_pack", "stream");
        Directory.CreateDirectory(streamRoot);

        var ymtPath = Path.Combine(streamRoot, "a_c_horse_01_horse_pack.ymt.xml");
        var drawablePath = Path.Combine(streamRoot, "a_c_horse_01_horse_pack^uppr_000_u.ydd");
        BuildMinimalPedVariationXml("horse_pack").Save(ymtPath);
        await File.WriteAllTextAsync(drawablePath, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "out");

        var build = await service.BuildAsync(analyze.Plan, outputRoot);

        var standalone = Assert.Single(analyze.Plan.StandaloneResources);
        Assert.Empty(analyze.Plan.TargetCollections);
        Assert.Equal("zz_merged_clothing_meta_standalone_animal_pack", standalone.OutputResource);
        Assert.Contains(standalone.Files, file => file.SourcePath == ymtPath);
        Assert.Contains(standalone.Files, file => file.SourcePath == drawablePath);
        Assert.Contains(build.WrittenFiles, file => file.EndsWith("zz_merged_clothing_meta_standalone_animal_pack/stream/a_c_horse_01_horse_pack.ymt.xml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(build.WrittenFiles, file => file.EndsWith("zz_merged_clothing_meta_standalone_animal_pack/stream/a_c_horse_01_horse_pack^uppr_000_u.ydd", StringComparison.OrdinalIgnoreCase));
    }

    private static XDocument BuildMinimalPedVariationXml(string collectionName)
        => new(
            new XElement("CPedVariationInfo",
                new XAttribute("name", collectionName),
                new XElement("availComp", "255 255 255 255 255 255 255 255 255 255 255 255"),
                new XElement("aComponentData3", new XAttribute("itemType", "CPVComponentData")),
                new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
                new XElement("dlcName", "hash_00000000")));
}
