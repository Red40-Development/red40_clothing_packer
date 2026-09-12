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
    public async Task ApplyCopyModeRemovesSourceMetaManifestEntriesFromCopiedResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"copy-before-rename-manifest-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var sourceManifest = Path.Combine(resourceRoot, "fxmanifest.lua");
        const string unrelatedFilesEntry = "  'data/unrelated.meta'";
        const string unrelatedDataFileEntry = "data_file 'SHOP_PED_APPAREL_META_FILE' 'data/unrelated.meta'";
        var unrelatedMetadata = Path.Combine(resourceRoot, "data", "unrelated.meta");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedMetadata)!);
        await File.WriteAllTextAsync(unrelatedMetadata, "<Unrelated />");
        await File.AppendAllTextAsync(
            sourceManifest,
            $"{Environment.NewLine}files {{{Environment.NewLine}{unrelatedFilesEntry}{Environment.NewLine}}}{Environment.NewLine}{unrelatedDataFileEntry}{Environment.NewLine}");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());

        await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = true,
        });

        var copiedManifest = Path.Combine(generatedRoot, "gang_flags", "fxmanifest.lua");
        Assert.True(File.Exists(copiedManifest));
        var manifestText = await File.ReadAllTextAsync(copiedManifest);
        Assert.DoesNotContain("ALTERNATE_VARIATIONS_FILE", manifestText);
        Assert.DoesNotContain("mp_m_freemode_01_mp_m_gang_flags.meta", manifestText);
        Assert.DoesNotContain("mp_f_freemode_01_mp_f_gang_flags.meta", manifestText);
        Assert.Contains("fx_version", manifestText);
        Assert.Contains(unrelatedFilesEntry, manifestText);
        Assert.Contains(unrelatedDataFileEntry, manifestText);
        Assert.Equal("<Unrelated />", await File.ReadAllTextAsync(Path.Combine(generatedRoot, "gang_flags", "data", "unrelated.meta")));
    }

    [Fact]
    public async Task ApplyRenameInPlaceBacksUpAndSanitizesOriginalManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rename-in-place-manifest-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var manifestPath = Path.Combine(resourceRoot, "fxmanifest.lua");
        var originalManifest = await File.ReadAllTextAsync(manifestPath);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());

        var entries = await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"), new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = false,
        });

        var updatedManifest = await File.ReadAllTextAsync(manifestPath);
        Assert.NotEqual(originalManifest, updatedManifest);
        Assert.DoesNotContain("SHOP_PED_APPAREL_META_FILE", updatedManifest);
        Assert.DoesNotContain("ALTERNATE_VARIATIONS_FILE", updatedManifest);
        Assert.DoesNotContain("mp_m_freemode_01_mp_m_gang_flags.meta", updatedManifest);
        Assert.DoesNotContain("mp_f_freemode_01_mp_f_gang_flags.meta", updatedManifest);
        Assert.Contains(entries, entry => entry.Kind == "resource-manifest" && entry.OriginalPath == manifestPath && entry.BackupPath is not null && File.Exists(entry.BackupPath));

        var manifest = Directory.GetFiles(Path.Combine(root, "backups"), "backup-manifest.json", SearchOption.AllDirectories).Single();
        await service.RestoreAsync(manifest);
        Assert.Equal(originalManifest, await File.ReadAllTextAsync(manifestPath));
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
    public async Task LoadPlanRejectsLegacySchemaWithoutFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"old-plan-load-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var planPath = Path.Combine(root, "old-plan.json");
        await File.WriteAllTextAsync(planPath, """{"schemaVersion":1,"resourceRoots":[],"sourceFiles":[]}""");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadPlanAsync(planPath));

        Assert.Contains("unsupported plan schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task ApplyRejectsChangedManifestBeforeCreatingBackupRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"changed-source-apply-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], Path.Combine(root, "generated"), "zz_merged_clothing_meta", new MergePlanSettings());
        var manifestPath = Path.Combine(resourceRoot, "fxmanifest.lua");
        await File.AppendAllTextAsync(manifestPath, $"{Environment.NewLine}-- changed after Analyze");
        var backupRoot = Path.Combine(root, "backups");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(analyze.Plan, backupRoot));

        Assert.Contains(manifestPath, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(backupRoot));
        Assert.False(Directory.Exists(Path.Combine(root, "generated")));
    }

    [Fact]
    public async Task RestoreRejectsModifiedGeneratedFileWithoutMutatingSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"modified-output-restore-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var sourceYmt = Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags.ymt");
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());
        await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"));
        var backupManifest = Directory.GetFiles(Path.Combine(root, "backups"), "backup-manifest.json", SearchOption.AllDirectories).Single();
        var generatedMetadata = Directory.GetFiles(
            Path.Combine(generatedRoot, "zz_merged_clothing_meta", "data"),
            "*.meta",
            SearchOption.TopDirectoryOnly).First();
        var generatedBefore = await File.ReadAllBytesAsync(generatedMetadata);
        await File.AppendAllTextAsync(generatedMetadata, $"{Environment.NewLine}user modification");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backupManifest));

        Assert.Contains(generatedMetadata, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(sourceYmt));
        Assert.EndsWith("user modification", await File.ReadAllTextAsync(generatedMetadata));

        await File.WriteAllBytesAsync(generatedMetadata, generatedBefore);
        await service.RestoreAsync(backupManifest);
        Assert.True(File.Exists(sourceYmt));
        Assert.False(Directory.Exists(Path.Combine(generatedRoot, "zz_merged_clothing_meta")));
    }

    [Fact]
    public async Task RestorePreviewRejectsManifestPathOutsideRecordedRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tampered-restore-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());
        await service.ApplyAsync(analyze.Plan, Path.Combine(root, "backups"));
        var backupManifest = Directory.GetFiles(Path.Combine(root, "backups"), "backup-manifest.json", SearchOption.AllDirectories).Single();
        var document = JsonNode.Parse(await File.ReadAllTextAsync(backupManifest))!.AsObject();
        var entry = document["entries"]!.AsArray()
            .Select(node => node!.AsObject())
            .First(node => node["kind"]!.GetValue<string>() == "old-ymt");
        var outsidePath = Path.Combine(Path.GetTempPath(), $"outside-restore-{Guid.NewGuid():N}.ymt");
        entry["originalPath"] = outsidePath;
        await File.WriteAllTextAsync(backupManifest, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadRestoreManifestPreviewAsync(backupManifest));

        Assert.Contains("outside recorded roots", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public async Task SavePlanCreatesNestedParentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nested-plan-save-test-{Guid.NewGuid():N}");
        var planPath = Path.Combine(root, "nested", "plans", "plan.json");
        var service = new RepackerService(new XmlPassthroughYmtCodec());

        await service.SavePlanAsync(new MergePlan(), planPath);

        Assert.True(File.Exists(planPath));
        var saved = JsonSerializer.Deserialize<MergePlan>(await File.ReadAllTextAsync(planPath));
        Assert.NotNull(saved);
    }

    [Fact]
    public async Task CopyModeApplyCancellationLeavesRestorableJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apply-cancellation-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<OperationProgress>(update =>
        {
            if (update.Stage == "copy-source-file" && update.Current == 1)
            {
                cancellation.Cancel();
            }
        });
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync([resourceRoot], generatedRoot, "zz_merged_clothing_meta", new MergePlanSettings());
        var backupRoot = Path.Combine(root, "backups");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyAsync(
            analyze.Plan,
            backupRoot,
            new ApplyOptions { CopyResourcesToOutputBeforeRename = true },
            progress,
            cancellation.Token));

        var backupManifest = Directory.GetFiles(backupRoot, "backup-manifest.json", SearchOption.AllDirectories).Single();
        Assert.True(File.Exists(Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags.ymt")));
        await service.RestoreAsync(backupManifest);
        Assert.False(Directory.Exists(Path.Combine(generatedRoot, "gang_flags")));
        Assert.False(Directory.Exists(Path.Combine(generatedRoot, "zz_merged_clothing_meta")));
    }

    [Fact]
    public async Task ApplyRejectsGeneratedTargetContainingSourceResource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apply-source-container-collision-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var resourceRoot = Path.Combine(resources, "gang_flags");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(
            [resourceRoot],
            root,
            "resources",
            new MergePlanSettings());
        var backupRoot = Path.Combine(root, "backups");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(analyze.Plan, backupRoot));

        Assert.Contains("must not overlap a selected source root", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags.ymt")));
        Assert.False(Directory.Exists(backupRoot));
        Assert.False(File.Exists(Path.Combine(resources, ".clothing-repacker-owned")));
    }

    [Fact]
    public async Task RestoreReconcilesPlannedStreamMoveFromRecordedHash()
    {
        var root = Path.Combine(Path.GetTempPath(), $"planned-stream-restore-test-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "resource");
        var generatedRoot = Path.Combine(root, "generated");
        var backupRoot = Path.Combine(root, "backups");
        var manifestDirectory = Path.Combine(backupRoot, "run");
        var originalPath = Path.Combine(sourceRoot, "stream", "old.ydd");
        var appliedPath = Path.Combine(sourceRoot, "stream", "new.ydd");
        Directory.CreateDirectory(Path.GetDirectoryName(appliedPath)!);
        Directory.CreateDirectory(manifestDirectory);
        await File.WriteAllTextAsync(appliedPath, "stream contents");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(appliedPath)));
        var manifest = new BackupManifest
        {
            BackupRoot = backupRoot,
            SourceRoots = [sourceRoot],
            GeneratedResourcesRoot = generatedRoot,
            Entries =
            [
                new BackupEntry("stream-rename", originalPath, null, appliedPath, hash, null, DateTimeOffset.UtcNow, "planned"),
            ],
        };
        var manifestPath = Path.Combine(manifestDirectory, "backup-manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var service = new RepackerService(new XmlPassthroughYmtCodec());

        await service.RestoreAsync(manifestPath);

        Assert.Equal("stream contents", await File.ReadAllTextAsync(originalPath));
        Assert.False(File.Exists(appliedPath));
    }

    [Fact]
    public async Task RestoreRejectsUnreconciledPlannedStreamMove()
    {
        var root = Path.Combine(Path.GetTempPath(), $"planned-stream-conflict-test-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "resource");
        var generatedRoot = Path.Combine(root, "generated");
        var backupRoot = Path.Combine(root, "backups");
        var manifestDirectory = Path.Combine(backupRoot, "run");
        var originalPath = Path.Combine(sourceRoot, "stream", "old.ydd");
        var appliedPath = Path.Combine(sourceRoot, "stream", "new.ydd");
        Directory.CreateDirectory(Path.GetDirectoryName(appliedPath)!);
        Directory.CreateDirectory(manifestDirectory);
        await File.WriteAllTextAsync(appliedPath, "modified contents");
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("original contents"u8));
        var manifest = new BackupManifest
        {
            BackupRoot = backupRoot,
            SourceRoots = [sourceRoot],
            GeneratedResourcesRoot = generatedRoot,
            Entries =
            [
                new BackupEntry("stream-rename", originalPath, null, appliedPath, expectedHash, null, DateTimeOffset.UtcNow, "planned"),
            ],
        };
        var manifestPath = Path.Combine(manifestDirectory, "backup-manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var service = new RepackerService(new XmlPassthroughYmtCodec());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(manifestPath));

        Assert.Contains("could not be reconciled safely", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(originalPath));
        Assert.Equal("modified contents", await File.ReadAllTextAsync(appliedPath));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task LegacyRestoreRestoresHashedBackupButSkipsGeneratedDirectoryDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"legacy-restore-test-{Guid.NewGuid():N}");
        var manifestDirectory = Path.Combine(root, "backup");
        var backupPath = Path.Combine(manifestDirectory, "source.ymt");
        var originalPath = Path.Combine(root, "resources", "source.ymt");
        var generatedResource = Path.Combine(root, "generated", "tool-output");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(generatedResource);
        await File.WriteAllTextAsync(backupPath, "original source");
        await File.WriteAllTextAsync(Path.Combine(generatedResource, "keep.txt"), "keep generated output");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(backupPath)));
        var entries = new List<BackupEntry>
        {
            new("old-ymt", originalPath, backupPath, null, hash, hash, DateTimeOffset.UtcNow),
            new("generated-resource", generatedResource, null, generatedResource, string.Empty, null, DateTimeOffset.UtcNow),
        };
        var backupManifest = Path.Combine(manifestDirectory, "backup-manifest.json");
        await File.WriteAllTextAsync(
            backupManifest,
            JsonSerializer.Serialize(entries, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var service = new RepackerService(new XmlPassthroughYmtCodec());

        var preview = await service.LoadRestoreManifestPreviewAsync(backupManifest);

        Assert.Contains(preview.Actions, action => action.Kind == "copy-backup-file" && action.DestinationPath == originalPath);
        Assert.Contains(preview.SkippedActions, action => action.Kind == "delete-generated-resource" && action.DestinationPath == generatedResource);
        await service.RestoreAsync(backupManifest);
        Assert.Equal("original source", await File.ReadAllTextAsync(originalPath));
        Assert.True(File.Exists(Path.Combine(generatedResource, "keep.txt")));
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
