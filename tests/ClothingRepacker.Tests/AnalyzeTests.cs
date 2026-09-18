using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using System.Xml.Linq;

namespace ClothingRepacker.Tests;

public class AnalyzeTests
{
    [Theory]
    [InlineData(256, 255, "Component drawable limit cannot exceed 255")]
    [InlineData(255, 256, "Prop drawable limit cannot exceed 255")]
    public async Task AnalyzeRejectsUnrepresentableDrawableLimits(int componentLimit, int propLimit, string expectedMessage)
    {
        var resources = Path.Combine(Path.GetTempPath(), $"invalid-drawable-limit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resources);
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(
            resources,
            "zz_merged_clothing_meta",
            new MergePlanSettings
            {
                MaxDrawablesPerComponent = componentLimit,
                MaxDrawablesPerProp = propLimit,
            }));

        Assert.Contains(expectedMessage, exception.Message);
    }

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
    public async Task AnalyzeDoesNotRediscoverRepackerBackupsAsResources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"analyze-ignore-backups-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var liveResource = Path.Combine(resources, "gang_flags");
        var backupRun = Path.Combine(resources, "backups", "2026-09-17T120000Z-test");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), liveResource);
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), Path.Combine(backupRun, "gang_flags"));
        await File.WriteAllTextAsync(Path.Combine(backupRun, "backup-manifest.json"), "{}");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var explicitAnalyze = await service.AnalyzeAsync(
            [liveResource, Path.Combine(resources, "backups")],
            Path.Combine(root, "generated"),
            "zz_merged_clothing_meta",
            new MergePlanSettings());

        foreach (var result in new[] { analyze, explicitAnalyze })
        {
            Assert.NotEmpty(result.Plan.SourceYmts);
            Assert.All(result.Plan.SourceYmts, source => Assert.Equal("gang_flags", source.Resource));
            Assert.DoesNotContain(
                result.Plan.ResourceRoots,
                resourceRoot => resourceRoot.Equals(Path.Combine(resources, "backups"), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                result.Plan.Errors,
                error => error.Contains("Ambiguous stream rename source claim", StringComparison.OrdinalIgnoreCase));
        }
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
        Assert.Contains(updates, update => update.Operation == "analyze" && update.Stage == "finalize-plan");

        var completed = Assert.Single(updates, update => update.Operation == "analyze" && update.Stage == "complete");
        Assert.Equal(result.Plan.SourceYmts.Count, completed.SourceCount);
        Assert.Equal(result.Plan.TargetCollections.Count, completed.TargetCount);
        Assert.Equal(result.Plan.Errors.Count, completed.ErrorCount);
        Assert.Equal(result.Plan.Warnings.Count, completed.WarningCount);
    }

    [Fact]
    public async Task AnalyzeSkipsXmlSidecarWhenMatchingYmtIsIdentical()
    {
        var root = Path.Combine(Path.GetTempPath(), $"analyze-skip-duplicate-xml-test-{Guid.NewGuid():N}");
        var streamRoot = Path.Combine(root, "resources", "gang_flags", "stream");
        Directory.CreateDirectory(streamRoot);

        var ymtPath = Path.Combine(streamRoot, "mp_f_freemode_01_mp_f_gang_flags.ymt");
        var xmlPath = ymtPath + ".xml";
        File.Copy(TestFixturePaths.Ymt("mp_f_freemode_01_mp_f_gang_flags.ymt"), ymtPath);
        File.Copy(TestFixturePaths.Ymt("mp_f_freemode_01_mp_f_gang_flags.ymt.xml"), xmlPath);

        var codec = new CountingYmtCodec(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var service = new RepackerService(codec);
        var result = await service.AnalyzeAsync(Path.Combine(root, "resources"), "zz_merged_clothing_meta", new MergePlanSettings());

        var source = Assert.Single(result.Plan.SourceYmts);
        Assert.Equal(ymtPath, source.Path);
        Assert.DoesNotContain(result.Plan.SourceYmts, source => source.Path.Equals(xmlPath, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, codec.DecodeCounts[ymtPath]);
        Assert.Equal(1, codec.DecodeCounts[xmlPath]);
    }

    [Fact]
    public async Task AnalyzeAndBuildSkipsNonFreemodePeds()
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

        Assert.Empty(analyze.Plan.TargetCollections);
        Assert.Empty(build.WrittenFiles);
        Assert.False(Directory.Exists(Path.Combine(outputRoot, "zz_merged_clothing_meta")));
        Assert.Contains(analyze.Plan.Warnings, warning => warning.Contains("Non-freemode YMT skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeAndBuildSplitOversizedFreemodeYmtIntoMergedOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"oversized-freemode-split-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var streamRoot = Path.Combine(resources, "oversized_pack", "stream");
        Directory.CreateDirectory(streamRoot);

        var ymtPath = Path.Combine(streamRoot, "mp_f_freemode_01_oversized_pack.ymt.xml");
        BuildPedVariationXmlWithDrawables("oversized_pack", drawableCount: 129).Save(ymtPath);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings
        {
            MaxDrawablesPerComponent = 128,
        });
        var outputRoot = Path.Combine(root, "out");

        await service.BuildAsync(analyze.Plan, outputRoot, new BuildOptions
        {
            IncludeYmtXml = true,
        });

        Assert.Empty(analyze.Plan.Errors);
        Assert.Contains(analyze.Plan.Warnings, warning => warning.Contains("will be split across 2 target collections", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, analyze.Plan.TargetCollections.Count);
        Assert.Equal([128, 1], analyze.Plan.TargetCollections
            .OrderBy(target => target.CollectionName)
            .Select(target => target.ComponentCounts[0])
            .ToArray());
        Assert.Equal([128, 1], analyze.Plan.TargetCollections
            .OrderBy(target => target.CollectionName)
            .Select(target => target.ComponentRanges.Single().Count)
            .ToArray());

        var firstXml = XDocument.Load(Path.Combine(outputRoot, "zz_merged_clothing_meta", "stream", "mp_f_freemode_01_merged_f_001.ymt.xml"));
        var secondXml = XDocument.Load(Path.Combine(outputRoot, "zz_merged_clothing_meta", "stream", "mp_f_freemode_01_merged_f_002.ymt.xml"));

        Assert.Equal(128, firstXml.Root!.Element("aComponentData3")!.Element("Item")!.Element("aDrawblData3")!.Elements("Item").Count());
        Assert.Single(secondXml.Root!.Element("aComponentData3")!.Element("Item")!.Element("aDrawblData3")!.Elements("Item"));
    }

    [Fact]
    public async Task AnalyzeExplicitResourceFoldersScansOnlySelectedResources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"explicit-resource-scan-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var selected = Path.Combine(resources, "gang_flags");
        var sibling = Path.Combine(resources, "gang_outfits");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), selected);
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_outfits"), sibling);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([selected], Path.Combine(root, "generated"), "zz_merged_clothing_meta", new MergePlanSettings());

        Assert.NotEmpty(analyze.Plan.SourceYmts);
        Assert.All(analyze.Plan.SourceYmts, source => Assert.Equal("gang_flags", source.Resource));
        Assert.DoesNotContain(analyze.Plan.SourceYmts, source => source.Resource.Equals("gang_outfits", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([Path.GetFullPath(selected)], analyze.Plan.ResourceRoots);
        Assert.Equal(Path.GetFullPath(resources), analyze.Plan.ResourcesRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "generated")), analyze.Plan.GeneratedResourcesRoot);
    }

    [Fact]
    public async Task ExplicitResourceFoldersPopulateManifestDependenciesFromSelectedRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"explicit-resource-dependency-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var first = Path.Combine(resources, "gang_flags");
        var second = Path.Combine(resources, "stream_only_pack");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), first);
        Directory.CreateDirectory(Path.Combine(second, "stream"));
        await File.WriteAllTextAsync(Path.Combine(second, "fxmanifest.lua"), "fx_version 'cerulean'");
        await File.WriteAllTextAsync(Path.Combine(second, "stream", "shirt.ydd"), "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([first, second], Path.Combine(root, "generated"), "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "preview");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var manifest = await File.ReadAllTextAsync(Path.Combine(outputRoot, "zz_merged_clothing_meta", "fxmanifest.lua"));
        Assert.Contains("'gang_flags'", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'stream_only_pack'", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(resources), analyze.Plan.ResourcesRoot);
    }

    [Fact]
    public async Task AnalyzeExplicitBracketFolderUsesChildResourceNamesForManifestDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bracket-resource-dependency-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var category = Path.Combine(resources, "[clothing]");
        var first = Path.Combine(category, "gang_flags");
        var second = Path.Combine(category, "gang_outfits");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), first);
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_outfits"), second);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([category], Path.Combine(root, "generated"), "zz_merged_clothing_meta", new MergePlanSettings());
        var outputRoot = Path.Combine(root, "preview");

        await service.BuildAsync(analyze.Plan, outputRoot);

        var manifest = await File.ReadAllTextAsync(Path.Combine(outputRoot, "zz_merged_clothing_meta", "fxmanifest.lua"));
        Assert.Contains("'gang_flags'", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'gang_outfits'", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[clothing]", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], analyze.Plan.ResourceRoots);
        Assert.Equal(Path.GetFullPath(resources), analyze.Plan.ResourcesRoot);
        Assert.DoesNotContain(analyze.Plan.SourceYmts, source => source.Resource.Contains('['));
    }

    [Fact]
    public async Task AnalyzeExplicitResourceFoldersRejectsOutputInsideSelectedResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"explicit-resource-nested-output-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var selected = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), selected);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeAsync([selected], Path.Combine(selected, "output"), "zz_merged_clothing_meta", new MergePlanSettings()));

        Assert.Contains("outside selected resource folders", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeExplicitResourceFoldersRejectsCopyModeOutputThatWouldOverwriteSelectedResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"explicit-resource-copy-output-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var selected = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), selected);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeAsync([selected], resources, "zz_merged_clothing_meta", new MergePlanSettings
            {
                RenameStreamsInPlace = false,
            }));

        Assert.Contains("output root separate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildRejectsOutputInsideSelectedResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"build-nested-output-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var selected = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), selected);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([selected], Path.Combine(root, "generated"), "zz_merged_clothing_meta", new MergePlanSettings());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BuildAsync(analyze.Plan, Path.Combine(selected, "preview")));

        Assert.Contains("outside selected resource folders", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeExplicitResourceFoldersMergesMultipleSelectedResources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"explicit-multi-resource-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var first = Path.Combine(resources, "gang_flags");
        var second = Path.Combine(resources, "gang_outfits");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), first);
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_outfits"), second);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([first, second], Path.Combine(root, "generated"), "zz_merged_clothing_meta", new MergePlanSettings());

        Assert.Contains(analyze.Plan.SourceYmts, source => source.Resource.Equals("gang_flags", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(analyze.Plan.SourceYmts, source => source.Resource.Equals("gang_outfits", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, analyze.Plan.ResourceRoots.Count);
    }

    [Fact]
    public async Task AnalyzeExplicitResourceFoldersPreservesInputOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"explicit-resource-order-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var first = Path.Combine(resources, "z_pack");
        var second = Path.Combine(resources, "a_pack");
        Directory.CreateDirectory(Path.Combine(first, "stream"));
        Directory.CreateDirectory(Path.Combine(second, "stream"));
        File.WriteAllText(Path.Combine(first, "fxmanifest.lua"), "fx_version 'cerulean'");
        File.WriteAllText(Path.Combine(second, "fxmanifest.lua"), "fx_version 'cerulean'");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([first, second], Path.Combine(root, "generated"), "zz_merged_clothing_meta", new MergePlanSettings());


        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], analyze.Plan.ResourceRoots);
    }
    [Fact]
    public async Task AnalyzeIgnoresUnrelatedMalformedAndValidXmlFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"analyze-unrelated-xml-test-{Guid.NewGuid():N}");
        var resource = Path.Combine(root, "resources", "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resource);

        var malformedPath = Path.Combine(resource, "stream", "unrelated-malformed.xml");
        var validPath = Path.Combine(resource, "stream", "unrelated-valid.xml");
        await File.WriteAllTextAsync(malformedPath, "<not-closed>");
        await File.WriteAllTextAsync(validPath, "<unrelated />");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var result = await service.AnalyzeAsync(Path.Combine(root, "resources"), "zz_merged_clothing_meta", new MergePlanSettings());

        Assert.NotEmpty(result.Plan.SourceYmts);
        Assert.DoesNotContain(result.Plan.Errors, error => error.Contains(malformedPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Plan.Errors, error => error.Contains(validPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeHonorsCancellationBetweenSourceFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"analyze-cancellation-test-{Guid.NewGuid():N}");
        var resourceRoot = Path.Combine(root, "resources", "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<OperationProgress>(update =>
        {
            if (update.Stage == "process-source" && update.Current == 1)
            {
                cancellation.Cancel();
            }
        });
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeAsync(
            [resourceRoot],
            Path.Combine(root, "generated"),
            "zz_merged_clothing_meta",
            new MergePlanSettings(),
            progress,
            cancellation.Token));
    }

    [Fact]
    public async Task BuildRejectsOutputTargetThatEqualsSourceResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"build-source-collision-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(
            [resourceRoot],
            Path.Combine(root, "generated"),
            "gang_flags",
            new MergePlanSettings());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(analyze.Plan, resources));

        Assert.Contains("must not overlap a selected source root", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags.ymt")));
        Assert.False(File.Exists(Path.Combine(resourceRoot, ".clothing-repacker-owned")));
    }

    private static XDocument BuildMinimalPedVariationXml(string collectionName)
        => new(
            new XElement("CPedVariationInfo",
                new XAttribute("name", collectionName),
                new XElement("availComp", "255 255 255 255 255 255 255 255 255 255 255 255"),
                new XElement("aComponentData3", new XAttribute("itemType", "CPVComponentData")),
                new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
                new XElement("dlcName", "hash_00000000")));

    private static XDocument BuildPedVariationXmlWithDrawables(string collectionName, int drawableCount)
        => new(
            new XElement("CPedVariationInfo",
                new XAttribute("name", collectionName),
                new XElement("availComp", "0 255 255 255 255 255 255 255 255 255 255 255"),
                new XElement("aComponentData3",
                    new XAttribute("itemType", "CPVComponentData"),
                    new XElement("Item",
                        new XElement("numAvailTex", new XAttribute("value", drawableCount)),
                        new XElement("aDrawblData3",
                            new XAttribute("itemType", "CPVDrawblData"),
                            Enumerable.Range(0, drawableCount).Select(_ =>
                                new XElement("Item",
                                    new XElement("aTexData", new XAttribute("itemType", "CPVTextureData"))))))),
                new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
                new XElement("dlcName", "hash_00000000")));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CountingYmtCodec(IYmtCodec inner) : IYmtCodec
    {
        public Dictionary<string, int> DecodeCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task<XDocument> DecodeToXmlAsync(string ymtPath, CancellationToken cancellationToken = default)
        {
            DecodeCounts[ymtPath] = DecodeCounts.GetValueOrDefault(ymtPath) + 1;
            return await inner.DecodeToXmlAsync(ymtPath, cancellationToken);
        }

        public Task EncodeFromXmlAsync(XDocument xml, string outputYmtPath, CancellationToken cancellationToken = default)
            => inner.EncodeFromXmlAsync(xml, outputYmtPath, cancellationToken);
    }
}
