using System.Text.Json;
using System.Text.Json.Nodes;
using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using System.Xml.Linq;

namespace ClothingRepacker.Tests;

public class ApplyRestoreTests
{
    [Fact]
    public async Task ApplyAndRestoreRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repacker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var resources = Path.Combine(root, "resources");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), Path.Combine(resources, "gang_flags"));

        var stream = Path.Combine(resources, "gang_flags", "stream");
        var sourceYmt = Path.Combine(stream, "mp_f_freemode_01_mp_f_gang_flags.ymt");
        var sourceDrawable = Path.Combine(stream, "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        await File.WriteAllTextAsync(sourceDrawable, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var planPath = Path.Combine(root, "plan.json");
        await service.SavePlanAsync(analyze.Plan, planPath);
        var plan = await service.LoadPlanAsync(planPath);

        var backupRoot = Path.Combine(root, "backups");
        var entries = await service.ApplyAsync(plan, backupRoot);

        Assert.False(File.Exists(sourceYmt));
        Assert.Contains(entries, entry => entry.Kind == "old-ymt");
        Assert.True(Directory.Exists(Path.Combine(root, "zz_merged_clothing_meta")));

        var manifest = Directory.GetFiles(backupRoot, "backup-manifest.json", SearchOption.AllDirectories).Single();
        await service.RestoreAsync(manifest);

        Assert.True(File.Exists(sourceYmt));
        Assert.True(File.Exists(sourceDrawable));
    }

    [Fact]
    public async Task ApplyLeavesNonFreemodeSourceFilesUnmodifiedWhenCopyModeIsOff()
    {
        var root = Path.Combine(Path.GetTempPath(), $"non-freemode-apply-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var stream = Path.Combine(resources, "animal_pack", "stream");
        Directory.CreateDirectory(stream);

        var sourceYmt = Path.Combine(stream, "a_c_horse_01_horse_pack.ymt.xml");
        var sourceDrawable = Path.Combine(stream, "a_c_horse_01_horse_pack^uppr_000_u.ydd");
        BuildMinimalPedVariationXml("horse_pack").Save(sourceYmt);
        await File.WriteAllTextAsync(sourceDrawable, "drawable");

        var ymtBefore = await File.ReadAllTextAsync(sourceYmt);
        var drawableBefore = await File.ReadAllTextAsync(sourceDrawable);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());

        var entries = await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"));

        Assert.True(File.Exists(sourceYmt));
        Assert.True(File.Exists(sourceDrawable));
        Assert.Equal(ymtBefore, await File.ReadAllTextAsync(sourceYmt));
        Assert.Equal(drawableBefore, await File.ReadAllTextAsync(sourceDrawable));
        Assert.DoesNotContain(entries, entry => entry.Kind == "old-ymt");
        Assert.False(Directory.Exists(Path.Combine(root, "zz_merged_clothing_meta")));
        Assert.False(Directory.Exists(Path.Combine(root, "zz_merged_clothing_meta_standalone_animal_pack")));
    }

    [Fact]
    public async Task ApplyCopiesNonFreemodeSourceResourceToGeneratedRootWhenCopyModeIsOn()
    {
        var root = Path.Combine(Path.GetTempPath(), $"non-freemode-copy-apply-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "animal_pack");
        var stream = Path.Combine(resourceRoot, "stream");
        Directory.CreateDirectory(stream);

        var sourceYmt = Path.Combine(stream, "a_c_horse_01_horse_pack.ymt.xml");
        var sourceDrawable = Path.Combine(stream, "a_c_horse_01_horse_pack^uppr_000_u.ydd");
        BuildMinimalPedVariationXml("horse_pack").Save(sourceYmt);
        await File.WriteAllTextAsync(sourceDrawable, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());

        var entries = await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = true,
        });

        var copiedResource = Path.Combine(generatedRoot, "animal_pack");
        Assert.True(File.Exists(sourceYmt));
        Assert.True(File.Exists(sourceDrawable));
        Assert.True(File.Exists(Path.Combine(copiedResource, "stream", "a_c_horse_01_horse_pack.ymt.xml")));
        Assert.True(File.Exists(Path.Combine(copiedResource, "stream", "a_c_horse_01_horse_pack^uppr_000_u.ydd")));
        Assert.False(Directory.Exists(Path.Combine(generatedRoot, "zz_merged_clothing_meta")));
        Assert.Contains(entries, entry => entry.Kind == "generated-resource" && entry.AppliedPath == copiedResource);
    }

    [Fact]
    public async Task ApplyUsesExplicitGeneratedResourcesRootFromPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"explicit-generated-root-apply-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var sourceDrawable = Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        await File.WriteAllTextAsync(sourceDrawable, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());

        await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"));

        Assert.True(Directory.Exists(Path.Combine(generatedRoot, "zz_merged_clothing_meta")));
        Assert.False(Directory.Exists(Path.Combine(root, "zz_merged_clothing_meta")));
    }

    [Fact]
    public async Task ApplyHonorsGeneratedOutputIncludeOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apply-include-options-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var sourceDrawable = Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        await File.WriteAllTextAsync(sourceDrawable, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());

        await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
        {
            IncludeYmtXml = false,
            IncludeDebugClient = false,
        });

        var generatedResource = Path.Combine(generatedRoot, "zz_merged_clothing_meta");
        Assert.True(Directory.Exists(generatedResource));
        Assert.Empty(Directory.GetFiles(generatedResource, "*.xml", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(generatedResource, "client", "validate_collections.lua")));
        Assert.DoesNotContain("client_script", await File.ReadAllTextAsync(Path.Combine(generatedResource, "fxmanifest.lua")));
    }

    [Fact]
    public async Task ApplyCanCopyResourcesToOutputBeforeRenaming()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copy-before-rename-apply-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var originalYmt = Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags.ymt");
        var originalDrawable = Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        var malformedDrawable = Path.Combine(resourceRoot, "stream", "bigb^decl_000_u.ydd");
        var originalAlternateVariations = Path.Combine(resourceRoot, "pedalternatevariations.meta");
        var originalFirstPersonAlternates = Path.Combine(resourceRoot, "first_person_alternates.meta");
        await File.WriteAllTextAsync(originalDrawable, "drawable");
        await File.WriteAllTextAsync(malformedDrawable, "malformed");
        BuildAlternateVariations().Save(originalAlternateVariations);
        BuildFirstPersonAlternates().Save(originalFirstPersonAlternates);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());

        var entries = await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = true,
        });

        var copiedResource = Path.Combine(generatedRoot, "gang_flags");
        var copiedYmt = Path.Combine(copiedResource, "stream", "mp_f_freemode_01_mp_f_gang_flags.ymt");
        var copiedDrawable = Path.Combine(copiedResource, "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        var copiedMalformedDrawable = Path.Combine(copiedResource, "stream", "bigb^decl_000_u.ydd");
        var copiedAlternateVariations = Path.Combine(copiedResource, "pedalternatevariations.meta");
        var copiedFirstPersonAlternates = Path.Combine(copiedResource, "first_person_alternates.meta");

        Assert.True(File.Exists(originalYmt));
        Assert.True(File.Exists(originalDrawable));
        Assert.True(File.Exists(malformedDrawable));
        Assert.True(File.Exists(originalAlternateVariations));
        Assert.True(File.Exists(originalFirstPersonAlternates));
        Assert.False(File.Exists(copiedYmt));
        Assert.False(File.Exists(copiedDrawable));
        Assert.True(File.Exists(copiedMalformedDrawable));
        Assert.False(File.Exists(copiedAlternateVariations));
        Assert.False(File.Exists(copiedFirstPersonAlternates));
        Assert.True(Directory.Exists(copiedResource));
        Assert.True(Directory.Exists(Path.Combine(generatedRoot, "zz_merged_clothing_meta")));
        Assert.Contains(entries, entry => entry.Kind == "stream-rename" && entry.OriginalPath.StartsWith(copiedResource, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.Kind == "old-ymt" && entry.OriginalPath.StartsWith(copiedResource, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.Kind == "source-alternate-metadata" && entry.OriginalPath.StartsWith(copiedResource, StringComparison.OrdinalIgnoreCase));

        var manifest = Directory.GetFiles(Path.Combine(root, "backups"), "backup-manifest.json", SearchOption.AllDirectories).Single();
        await service.RestoreAsync(manifest);

        Assert.True(File.Exists(originalYmt));
        Assert.True(File.Exists(originalDrawable));
        Assert.False(Directory.Exists(copiedResource));
        Assert.False(Directory.Exists(Path.Combine(generatedRoot, "zz_merged_clothing_meta")));
    }

    [Fact]
    public async Task ApplyCopyModePreservesResourceDirectoryWhenPathsHaveTrailingSeparators()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copy-before-rename-trailing-slash-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var originalDrawable = Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        await File.WriteAllTextAsync(originalDrawable, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(
            [resourceRoot + Path.DirectorySeparatorChar],
            generatedRoot + Path.DirectorySeparatorChar,
            "zz_merged_clothing_meta",
            new MergePlanSettings());

        await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = true,
        });

        var copiedResource = Path.Combine(generatedRoot, "gang_flags");
        Assert.True(Directory.Exists(copiedResource));
        Assert.True(File.Exists(Path.Combine(copiedResource, "fxmanifest.lua")));
        Assert.True(Directory.Exists(Path.Combine(copiedResource, "stream")));
        Assert.False(File.Exists(Path.Combine(generatedRoot, "gang_flagsfxmanifest.lua")));
        Assert.False(Directory.Exists(Path.Combine(generatedRoot, "gang_flagsstream")));
    }

    [Fact]
    public async Task ApplyCopyModeOverlaysGeneratedFilesWhenTargetResourceMatchesCopiedResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copy-before-rename-overlay-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var originalDrawable = Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        await File.WriteAllTextAsync(originalDrawable, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "gang_flags", new MergePlanSettings());

        await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = true,
        });

        var copiedResource = Path.Combine(generatedRoot, "gang_flags");
        Assert.True(File.Exists(originalDrawable));
        Assert.False(File.Exists(Path.Combine(copiedResource, "stream", "mp_f_freemode_01_mp_f_gang_flags.ymt")));
        Assert.False(File.Exists(Path.Combine(copiedResource, "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd")));
        Assert.True(File.Exists(Path.Combine(copiedResource, "stream", "mp_f_freemode_01_merged_f_001^decl_000_u.ydd")));
        Assert.True(File.Exists(Path.Combine(copiedResource, "stream", "mp_f_freemode_01_merged_f_001.ymt")));
        Assert.True(File.Exists(Path.Combine(copiedResource, "data", "mp_f_freemode_01_merged_f_001.meta")));
    }

    [Fact]
    public async Task ApplyCopyModeRemovesStraySourceYmtAndMetaFilesFromCopiedOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copy-before-rename-stray-files-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "gang_flags", new MergePlanSettings());

        var staleYmt = Path.Combine(resourceRoot, "stream", "legacy_source.ymt.xml");
        var staleMeta = Path.Combine(resourceRoot, "data", "legacy_source.meta");
        Directory.CreateDirectory(Path.GetDirectoryName(staleYmt)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staleMeta)!);
        await File.WriteAllTextAsync(staleYmt, "<root />");
        await File.WriteAllTextAsync(staleMeta, "stale meta");

        await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = true,
        });

        var copiedResource = Path.Combine(generatedRoot, "gang_flags");
        Assert.False(File.Exists(Path.Combine(copiedResource, "stream", "legacy_source.ymt.xml")));
        Assert.False(File.Exists(Path.Combine(copiedResource, "data", "legacy_source.meta")));
    }

    [Fact]
    public async Task ApplyCopyModeRejectsOutputThatWouldOverwriteSelectedResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copy-before-rename-unsafe-output-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], resources, "zz_merged_clothing_meta", new MergePlanSettings());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
            {
                CopyResourcesToOutputBeforeRename = true,
            }));

        Assert.Contains("output root separate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(resourceRoot, "gang_flags")));
        Assert.False(Directory.Exists(Path.Combine(resources, "zz_merged_clothing_meta")));
    }

    [Fact]
    public async Task ApplyFallsBackForOldPlansWithoutGeneratedResourcesRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"old-plan-apply-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), Path.Combine(resources, "gang_flags"));

        var sourceDrawable = Path.Combine(resources, "gang_flags", "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        await File.WriteAllTextAsync(sourceDrawable, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var planPath = Path.Combine(root, "old-plan.json");
        await service.SavePlanAsync(analyze.Plan, planPath);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(planPath))!.AsObject();
        json["generatedResourcesRoot"] = string.Empty;
        json["resourceRoots"] = new JsonArray();
        await File.WriteAllTextAsync(planPath, json.ToJsonString());
        var oldPlan = await service.LoadPlanAsync(planPath);

        await service.ApplyAsync(oldPlan, Path.Combine(root, "backups"));

        Assert.True(Directory.Exists(Path.Combine(root, "zz_merged_clothing_meta")));
    }

    [Fact]
    public async Task ApplyBacksUpBrokenCreatureMetadataWithoutHandlingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"broken-creature-apply-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var stream = Path.Combine(resources, "broken_pack", "stream");
        Directory.CreateDirectory(stream);

        var creatureMetadataPath = Path.Combine(stream, "mp_creaturemetadata.ymt.xml");
        BuildMinimalCreatureMetadataXml().Save(creatureMetadataPath);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());

        var entries = await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"));

        Assert.Empty(analyze.Plan.SourceCreatureMetadata);
        Assert.Single(analyze.Plan.BrokenCreatureMetadataBackups);
        Assert.False(File.Exists(creatureMetadataPath));
        Assert.Contains(entries, entry => entry.Kind == "broken-creature-metadata" && entry.OriginalPath == creatureMetadataPath && entry.BackupPath is not null && File.Exists(entry.BackupPath));

        var manifest = Directory.GetFiles(Path.Combine(root, "backups"), "backup-manifest.json", SearchOption.AllDirectories).Single();
        await service.RestoreAsync(manifest);

        Assert.True(File.Exists(creatureMetadataPath));
    }

    [Fact]
    public async Task ApplyBacksUpAndRestoresSourceAlternateMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alternate-metadata-apply-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var alternateVariations = Path.Combine(resourceRoot, "pedalternatevariations.meta");
        var firstPersonAlternates = Path.Combine(resourceRoot, "first_person_alternates.meta");
        BuildAlternateVariations().Save(alternateVariations);
        BuildFirstPersonAlternates().Save(firstPersonAlternates);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());

        var entries = await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"));

        Assert.Equal(2, analyze.Plan.SourceAlternateMetadataBackups.Count);
        Assert.False(File.Exists(alternateVariations));
        Assert.False(File.Exists(firstPersonAlternates));
        Assert.True(File.Exists(Path.Combine(root, "zz_merged_clothing_meta", "data", "pedalternatevariations.meta")));
        Assert.True(File.Exists(Path.Combine(root, "zz_merged_clothing_meta", "data", "first_person_alternates.meta")));
        Assert.Equal(2, entries.Count(entry => entry.Kind == "source-alternate-metadata" && entry.BackupPath is not null && File.Exists(entry.BackupPath)));

        var manifest = Directory.GetFiles(Path.Combine(root, "backups"), "backup-manifest.json", SearchOption.AllDirectories).Single();
        await service.RestoreAsync(manifest);

        Assert.True(File.Exists(alternateVariations));
        Assert.True(File.Exists(firstPersonAlternates));
    }

    private static XDocument BuildMinimalPedVariationXml(string collectionName)
        => new(
            new XElement("CPedVariationInfo",
                new XAttribute("name", collectionName),
                new XElement("availComp", "255 255 255 255 255 255 255 255 255 255 255 255"),
                new XElement("aComponentData3", new XAttribute("itemType", "CPVComponentData")),
                new XElement("compInfos", new XAttribute("itemType", "CComponentInfo")),
                new XElement("dlcName", "hash_00000000")));

    private static XDocument BuildMinimalCreatureMetadataXml()
        => new(
            new XElement("CCreatureMetaData",
                new XElement("shaderVariableComponents", new XAttribute("itemType", "CShaderVariableComponent")),
                new XElement("pedPropExpressions", new XAttribute("itemType", "CPedPropExpressionData")),
                new XElement("pedCompExpressions", new XAttribute("itemType", "CPedCompExpressionData"))));

    private static XDocument BuildAlternateVariations()
        => new(
            new XElement("CAlternateVariations",
                new XElement("peds",
                    new XElement("Item",
                        new XElement("name", "mp_f_freemode_01"),
                        new XElement("switches",
                            new XElement("Item",
                                new XElement("dlcNameHash", "mp_f_gang_flags"),
                                new XElement("component", new XAttribute("value", 11)),
                                new XElement("index", new XAttribute("value", 0)),
                                new XElement("alt", new XAttribute("value", 1)),
                                new XElement("sourceAssets")))))));

    private static XDocument BuildFirstPersonAlternates()
        => new(
            new XElement("FirstPersonAlternateData",
                new XElement("alternates",
                    new XElement("Item",
                        new XElement("assetName", "MP_F_Freemode_01_mp_f_gang_flags/jbib_000_u"),
                        new XElement("alternate", new XAttribute("value", 1))))));
}
