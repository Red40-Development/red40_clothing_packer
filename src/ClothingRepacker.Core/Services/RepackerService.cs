using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Hashing;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Planning;
using ClothingRepacker.Core.Scanning;
using ClothingRepacker.Core.Validation;
using ClothingRepacker.Core.Xml;

namespace ClothingRepacker.Core.Services;

public sealed class RepackerService
{
    private readonly ResourceScanner _scanner = new();
    private readonly PedVariationReader _reader = new();
    private readonly CreatureMetadataReader _creatureMetadataReader = new();
    private readonly MergePlanner _mergePlanner = new();
    private readonly StreamRenamePlanner _streamRenamePlanner = new();
    private readonly PlanValidator _planValidator = new();
    private readonly IYmtCodec _codec;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RepackerService(IYmtCodec codec)
    {
        _codec = codec;
    }

    public async Task<AnalyzeResult> AnalyzeAsync(string resourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsCreatureMetadataMode(settings.CreatureMetadataMode))
        {
            throw new InvalidOperationException("CreatureMetadataMode must be 'repair' or 'preserve'.");
        }

        var scanItems = _scanner.ScanResources(resourcesRoot);
        var sources = new List<SourceYmt>();
        var creatureMetadata = new List<SourceCreatureMetadata>();
        var streamFiles = new List<StreamFile>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var manifestWarnings = new List<SourceManifestWarning>();
        var workItems = new List<(ResourceScanItem Item, string Path)>();

        foreach (var item in scanItems)
        {
            streamFiles.AddRange(item.StreamFiles);
            if (item.ManifestPath is not null)
            {
                manifestWarnings.AddRange(ReadManifestWarnings(item));
            }

            foreach (var path in item.YmtFiles.Where(IsLikelyPedVariationXml))
            {
                workItems.Add((item, path));
            }
        }

        progress?.Report(new OperationProgress(
            "analyze",
            "start",
            Total: workItems.Count,
            Message: $"Found {scanItems.Count} resources, {workItems.Count} YMT/XML candidates, {streamFiles.Count} stream files."));

        for (var index = 0; index < workItems.Count; index++)
        {
            var (item, path) = workItems[index];
            try
            {
                var xml = await _codec.DecodeToXmlAsync(path, cancellationToken);
                if (xml.Root?.Name.LocalName == "CCreatureMetaData")
                {
                    creatureMetadata.Add(_creatureMetadataReader.Read(xml, path, item.ResourceName, item.ResourceRoot));
                    continue;
                }

                if (xml.Root?.Name.LocalName != "CPedVariationInfo")
                {
                    continue;
                }

                var source = _reader.Read(xml, path, item.ResourceName, item.ResourceRoot);
                warnings.AddRange(source.Messages.Where(message => message.Severity == ValidationSeverity.Warning).Select(message => $"{path}: {message.Message}"));
                errors.AddRange(source.Messages.Where(message => message.Severity == ValidationSeverity.Error).Select(message => $"{path}: {message.Message}"));
                sources.Add(source);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
            finally
            {
                progress?.Report(new OperationProgress(
                    "analyze",
                    "process-source",
                    index + 1,
                    workItems.Count,
                    path,
                    SourceCount: sources.Count,
                    WarningCount: warnings.Count,
                    ErrorCount: errors.Count));
            }
        }

        progress?.Report(new OperationProgress(
            "analyze",
            "plan-targets",
            workItems.Count,
            workItems.Count,
            Message: "Planning merged target collections.",
            SourceCount: sources.Count,
            WarningCount: warnings.Count,
            ErrorCount: errors.Count));

        var mergeableSources = sources.Where(IsMergeableFreemodeSource).ToList();
        var standaloneResources = BuildStandaloneResourcePlans(sources.Except(mergeableSources).ToList(), streamFiles, targetResource, warnings);
        var targets = _mergePlanner.Plan(mergeableSources, settings, warnings, errors);
        var drawableMappings = new List<DrawableMapping>();
        var propMappings = new List<PropMapping>();
        var targetPlans = new List<TargetCollectionPlan>();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var builder = new OutputCollectionBuilder(target.CollectionName, target.FullCollectionName, target.PedBaseName, target.Gender);
            foreach (var source in target.Sources)
            {
                drawableMappings.AddRange(builder.AddComponents(source));
                propMappings.AddRange(builder.AddProps(source));
            }

            var outputYmtPath = Path.Combine(targetResource, "stream", $"{target.FullCollectionName}.ymt");
            targetPlans.Add(new TargetCollectionPlan(
                target.CollectionName,
                target.FullCollectionName,
                target.Gender,
                outputYmtPath.Replace(Path.DirectorySeparatorChar, '/'),
                target.Sources.Select(source => source.YmtPath).ToList(),
                builder.GetComponentCounts(),
                builder.GetPropCounts()));

            progress?.Report(new OperationProgress(
                "analyze",
                "build-plan",
                index + 1,
                targets.Count,
                target.FullCollectionName,
                SourceCount: sources.Count,
                WarningCount: warnings.Count,
                ErrorCount: errors.Count,
                TargetCount: targetPlans.Count));
        }

        var streamRenames = _streamRenamePlanner.BuildRenamePlan(drawableMappings, propMappings, streamFiles);
        errors.AddRange(_streamRenamePlanner.ValidateCollisions(streamRenames));

        progress?.Report(new OperationProgress(
            "analyze",
            "complete",
            workItems.Count,
            workItems.Count,
            Message: "Analyze complete.",
            SourceCount: sources.Count,
            WarningCount: warnings.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ErrorCount: errors.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TargetCount: targetPlans.Count,
            RenameCount: streamRenames.Count));

        var plan = new MergePlan
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ResourcesRoot = Path.GetFullPath(resourcesRoot),
            TargetResource = targetResource,
            Settings = settings,
            SourceYmts = sources.Select(source => new SourceYmtSummary(
                source.ResourceName,
                source.YmtPath,
                source.PedBaseName,
                source.Gender,
                source.CollectionName,
                source.FullCollectionName,
                source.DlcName,
                source.Components.ToDictionary(component => component.ComponentId, component => component.Drawables.Count),
                source.Props.ToDictionary(prop => prop.AnchorId, prop => prop.Props.Count))).ToList(),
            TargetCollections = targetPlans,
            StandaloneResources = standaloneResources.ToList(),
            DrawableMappings = drawableMappings,
            PropMappings = propMappings,
            StreamRenames = streamRenames.ToList(),
            OldYmtBackups = mergeableSources.Select(source => new OldYmtBackupPlan(
                source.YmtPath,
                Path.Combine("_clothing_repacker_backups", "{runId}", source.ResourceName, Path.GetRelativePath(source.ResourceRoot, source.YmtPath)).Replace(Path.DirectorySeparatorChar, '/'))).ToList(),
            SourceManifestWarnings = manifestWarnings,
            SourceCreatureMetadata = creatureMetadata.Select(metadata => new SourceCreatureMetadataSummary(
                metadata.ResourceName,
                metadata.Path,
                metadata.ShaderVariableComponents.Count,
                metadata.ComponentExpressions.Count,
                metadata.PropExpressions.Count)).ToList(),
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };

        return new AnalyzeResult(plan, sources, streamFiles, creatureMetadata);
    }

    public async Task SavePlanAsync(MergePlan plan, string outputPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, plan, _jsonOptions, cancellationToken);
    }

    public async Task<MergePlan> LoadPlanAsync(string planPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(planPath);
        return (await JsonSerializer.DeserializeAsync<MergePlan>(stream, _jsonOptions, cancellationToken))
            ?? throw new InvalidDataException($"Could not read plan {planPath}.");
    }

    public async Task<BuildResult> BuildAsync(MergePlan plan, string outputRoot, BuildOptions? options = null, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new BuildOptions();
        var sources = await ReloadSourcesForPlanAsync(plan, progress, cancellationToken);
        var creatureMetadataByResource = await ReloadCreatureMetadataForPlanAsync(plan, cancellationToken);
        var writtenFiles = new List<string>();
        var fullOutputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(fullOutputRoot);

        progress?.Report(new OperationProgress(
            "build",
            "start",
            Total: plan.TargetCollections.Count,
            Message: $"Loaded {sources.Count} source YMTs for {plan.TargetCollections.Count} target collections.",
            SourceCount: sources.Count));

        for (var index = 0; index < plan.TargetCollections.Count; index++)
        {
            var targetPlan = plan.TargetCollections[index];
            var builder = new OutputCollectionBuilder(targetPlan.CollectionName, targetPlan.FullCollectionName, InferPedBaseName(targetPlan.FullCollectionName), targetPlan.Gender);
            foreach (var sourcePath in targetPlan.SourceYmts)
            {
                var source = sources[sourcePath];
                builder.AddComponents(source);
                builder.AddProps(source);
            }

            var xml = builder.BuildXml();
            var relativeYmtPath = targetPlan.OutputYmtPath.Replace('/', Path.DirectorySeparatorChar);
            var ymtOutputPath = Path.Combine(fullOutputRoot, relativeYmtPath);
            Directory.CreateDirectory(Path.GetDirectoryName(ymtOutputPath)!);
            await _codec.EncodeFromXmlAsync(xml, ymtOutputPath, cancellationToken);
            writtenFiles.Add(ymtOutputPath);

            if (options.IncludeYmtXml)
            {
                var previewXmlPath = ymtOutputPath + ".xml";
                xml.Save(previewXmlPath);
                writtenFiles.Add(previewXmlPath);
            }

            var creatureMetadataXml = BuildCreatureMetadataXml(plan, targetPlan, sources, creatureMetadataByResource);
            var creatureMetadataOutputPath = Path.Combine(fullOutputRoot, plan.TargetResource, "stream", $"MP_CreatureMetadata_{targetPlan.CollectionName}.ymt");
            Directory.CreateDirectory(Path.GetDirectoryName(creatureMetadataOutputPath)!);
            await _codec.EncodeFromXmlAsync(creatureMetadataXml, creatureMetadataOutputPath, cancellationToken);
            writtenFiles.Add(creatureMetadataOutputPath);

            if (options.IncludeYmtXml)
            {
                var previewXmlPath = creatureMetadataOutputPath + ".xml";
                creatureMetadataXml.Save(previewXmlPath);
                writtenFiles.Add(previewXmlPath);
            }

            var metaPath = Path.Combine(fullOutputRoot, plan.TargetResource, "data", $"{targetPlan.FullCollectionName}.meta");
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            await File.WriteAllTextAsync(metaPath, BuildMinimalShopMeta(targetPlan), cancellationToken);
            writtenFiles.Add(metaPath);

            progress?.Report(new OperationProgress(
                "build",
                "write-target",
                index + 1,
                plan.TargetCollections.Count,
                targetPlan.FullCollectionName,
                SourceCount: sources.Count,
                TargetCount: index + 1,
                WrittenFileCount: writtenFiles.Count));
        }

        var fxmanifestPath = Path.Combine(fullOutputRoot, plan.TargetResource, "fxmanifest.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(fxmanifestPath)!);
        await File.WriteAllTextAsync(fxmanifestPath, BuildFxManifest(plan, options), cancellationToken);
        writtenFiles.Add(fxmanifestPath);

        if (options.IncludeDebugClient)
        {
            var validationPath = Path.Combine(fullOutputRoot, plan.TargetResource, "client", "validate_collections.lua");
            Directory.CreateDirectory(Path.GetDirectoryName(validationPath)!);
            await File.WriteAllTextAsync(validationPath, BuildValidationLua(plan), cancellationToken);
            writtenFiles.Add(validationPath);
        }

        foreach (var standaloneResource in plan.StandaloneResources)
        {
            var resourceRoot = Path.Combine(fullOutputRoot, standaloneResource.OutputResource);
            foreach (var file in standaloneResource.Files)
            {
                var outputPath = Path.Combine(fullOutputRoot, file.OutputPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.Copy(file.SourcePath, outputPath, overwrite: true);
                writtenFiles.Add(outputPath);
            }

            var manifestPath = Path.Combine(resourceRoot, "fxmanifest.lua");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(manifestPath, BuildStandaloneFxManifest(standaloneResource), cancellationToken);
            writtenFiles.Add(manifestPath);
        }

        progress?.Report(new OperationProgress(
            "build",
            "complete",
            plan.TargetCollections.Count,
            plan.TargetCollections.Count,
            Message: "Build complete.",
            SourceCount: sources.Count,
            TargetCount: plan.TargetCollections.Count,
            WrittenFileCount: writtenFiles.Count));

        return new BuildResult(fullOutputRoot, writtenFiles);
    }

    public async Task<ExportXmlResult> ExportYmtsToXmlAsync(string folderPath, bool overwrite, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(fullRoot);
        }

        var writtenFiles = new List<string>();
        var skippedFiles = new List<string>();
        var ymtFiles = Directory.GetFiles(fullRoot, "*.ymt", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report(new OperationProgress(
            "export-xml",
            "start",
            Total: ymtFiles.Count,
            Message: $"Found {ymtFiles.Count} YMT files to export."));

        for (var index = 0; index < ymtFiles.Count; index++)
        {
            var ymtPath = ymtFiles[index];
            cancellationToken.ThrowIfCancellationRequested();

            var xmlPath = ymtPath + ".xml";
            if (!overwrite && File.Exists(xmlPath))
            {
                skippedFiles.Add(xmlPath);
                progress?.Report(new OperationProgress(
                    "export-xml",
                    "export-file",
                    index + 1,
                    ymtFiles.Count,
                    ymtPath,
                    WrittenFileCount: writtenFiles.Count,
                    SkippedCount: skippedFiles.Count));
                continue;
            }

            var xml = await _codec.DecodeToXmlAsync(ymtPath, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
            xml.Save(xmlPath);
            writtenFiles.Add(xmlPath);

            progress?.Report(new OperationProgress(
                "export-xml",
                "export-file",
                index + 1,
                ymtFiles.Count,
                ymtPath,
                WrittenFileCount: writtenFiles.Count,
                SkippedCount: skippedFiles.Count));
        }

        progress?.Report(new OperationProgress(
            "export-xml",
            "complete",
            ymtFiles.Count,
            ymtFiles.Count,
            Message: "XML export complete.",
            WrittenFileCount: writtenFiles.Count,
            SkippedCount: skippedFiles.Count));

        return new ExportXmlResult(fullRoot, writtenFiles, skippedFiles);
    }

    public async Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var mergedSourceYmtPaths = plan.TargetCollections
            .SelectMany(target => target.SourceYmts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new OperationProgress(
            "apply",
            "start",
            Total: plan.StreamRenames.Count + mergedSourceYmtPaths.Count,
            Message: $"Preparing to apply {plan.StreamRenames.Count} stream renames and {mergedSourceYmtPaths.Count} YMT backups."));

        var validationErrors = _planValidator.Validate(plan);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        }

        var runId = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHHmmssZ");
        var backupDir = Path.Combine(Path.GetFullPath(backupRoot), runId);
        Directory.CreateDirectory(backupDir);

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"clothing-repacker-{Guid.NewGuid():N}");
        progress?.Report(new OperationProgress(
            "apply",
            "build-staging",
            Message: "Building generated resource into a staging folder."));
        var buildResult = await BuildAsync(plan, stagingRoot, options: null, progress, cancellationToken);
        var entries = new List<BackupEntry>();

        for (var index = 0; index < plan.StreamRenames.Count; index++)
        {
            var rename = plan.StreamRenames[index];
            if (!File.Exists(rename.SourcePath))
            {
                throw new FileNotFoundException($"Source file missing at apply time: {rename.SourcePath}");
            }

            var beforeHash = ComputeSha256(rename.SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(rename.TargetPath)!);
            File.Move(rename.SourcePath, rename.TargetPath);
            entries.Add(new BackupEntry("stream-rename", rename.SourcePath, null, rename.TargetPath, beforeHash, ComputeSha256(rename.TargetPath), DateTimeOffset.UtcNow));

            progress?.Report(new OperationProgress(
                "apply",
                "rename-stream",
                index + 1,
                plan.StreamRenames.Count,
                rename.TargetPath,
                RenameCount: index + 1,
                BackupCount: entries.Count(entry => entry.Kind == "old-ymt")));
        }

        var mergedSources = plan.SourceYmts
            .Where(source => mergedSourceYmtPaths.Contains(source.Path))
            .ToList();

        for (var index = 0; index < mergedSources.Count; index++)
        {
            var source = mergedSources[index];
            if (!File.Exists(source.Path))
            {
                continue;
            }

            var backupPath = Path.Combine(backupDir, source.Resource, Path.GetFileName(source.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(source.Path, backupPath, overwrite: true);
            var beforeHash = ComputeSha256(source.Path);
            File.Delete(source.Path);
            entries.Add(new BackupEntry("old-ymt", source.Path, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow));

            progress?.Report(new OperationProgress(
                "apply",
                "backup-source-ymt",
                index + 1,
                mergedSources.Count,
                source.Path,
                RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
                BackupCount: entries.Count(entry => entry.Kind == "old-ymt")));
        }

        var generatedRoot = Path.Combine(Path.GetDirectoryName(plan.ResourcesRoot) ?? plan.ResourcesRoot, plan.TargetResource);
        if (Directory.Exists(generatedRoot))
        {
            Directory.Delete(generatedRoot, recursive: true);
        }

        CopyDirectory(Path.Combine(buildResult.OutputRoot, plan.TargetResource), generatedRoot);
        entries.Add(new BackupEntry("generated-resource", generatedRoot, null, generatedRoot, string.Empty, null, DateTimeOffset.UtcNow));

        foreach (var standaloneResource in plan.StandaloneResources)
        {
            var standaloneGeneratedRoot = Path.Combine(Path.GetDirectoryName(plan.ResourcesRoot) ?? plan.ResourcesRoot, standaloneResource.OutputResource);
            if (Directory.Exists(standaloneGeneratedRoot))
            {
                Directory.Delete(standaloneGeneratedRoot, recursive: true);
            }

            CopyDirectory(Path.Combine(buildResult.OutputRoot, standaloneResource.OutputResource), standaloneGeneratedRoot);
            entries.Add(new BackupEntry("generated-resource", standaloneGeneratedRoot, null, standaloneGeneratedRoot, string.Empty, null, DateTimeOffset.UtcNow));
        }

        progress?.Report(new OperationProgress(
            "apply",
            "copy-generated-resource",
            Message: $"Copied generated resource to {generatedRoot}.",
            RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
            BackupCount: entries.Count(entry => entry.Kind == "old-ymt"),
            WrittenFileCount: buildResult.WrittenFiles.Count));

        var manifestPath = Path.Combine(backupDir, "backup-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(entries, _jsonOptions), cancellationToken);

        progress?.Report(new OperationProgress(
            "apply",
            "complete",
            plan.StreamRenames.Count + mergedSources.Count,
            plan.StreamRenames.Count + mergedSources.Count,
            Message: $"Apply complete. Backup manifest written to {manifestPath}.",
            RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
            BackupCount: entries.Count(entry => entry.Kind == "old-ymt"),
            WrittenFileCount: buildResult.WrittenFiles.Count));

        return entries;
    }

    public async Task RestoreAsync(string backupManifestPath, CancellationToken cancellationToken = default)
    {

        var entries = JsonSerializer.Deserialize<List<BackupEntry>>(await File.ReadAllTextAsync(backupManifestPath, cancellationToken), _jsonOptions)
            ?? throw new InvalidDataException("Invalid backup manifest.");

        foreach (var entry in entries.Where(entry => entry.Kind == "generated-resource" && entry.AppliedPath is not null))
        {
            if (Directory.Exists(entry.AppliedPath))
            {
                Directory.Delete(entry.AppliedPath, recursive: true);
            }
        }

        foreach (var entry in entries.Where(entry => entry.Kind == "old-ymt"))
        {
            if (entry.BackupPath is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
            File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
        }

        foreach (var entry in entries.Where(entry => entry.Kind == "stream-rename"))
        {
            if (entry.AppliedPath is null || !File.Exists(entry.AppliedPath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
            File.Move(entry.AppliedPath, entry.OriginalPath, overwrite: true);
        }
    }

    public IReadOnlyList<string> ValidatePlan(MergePlan plan) => _planValidator.Validate(plan);

    private async Task<Dictionary<string, SourceYmt>> ReloadSourcesForPlanAsync(MergePlan plan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SourceYmt>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < plan.SourceYmts.Count; index++)
        {
            var source = plan.SourceYmts[index];
            var xml = await _codec.DecodeToXmlAsync(source.Path, cancellationToken);
            result[source.Path] = _reader.Read(xml, source.Path, source.Resource, Path.GetDirectoryName(source.Path) ?? source.Resource);

            progress?.Report(new OperationProgress(
                "build",
                "load-source",
                index + 1,
                plan.SourceYmts.Count,
                source.Path,
                SourceCount: index + 1));
        }

        return result;
    }

    private async Task<Dictionary<string, List<SourceCreatureMetadata>>> ReloadCreatureMetadataForPlanAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<SourceCreatureMetadata>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.SourceCreatureMetadata)
        {
            var xml = await _codec.DecodeToXmlAsync(source.Path, cancellationToken);
            var metadata = _creatureMetadataReader.Read(xml, source.Path, source.Resource, Path.GetDirectoryName(source.Path) ?? source.Resource);
            if (!result.TryGetValue(metadata.ResourceName, out var resourceMetadata))
            {
                resourceMetadata = [];
                result[metadata.ResourceName] = resourceMetadata;
            }

            resourceMetadata.Add(metadata);
        }

        return result;
    }

    private static XDocument BuildCreatureMetadataXml(
        MergePlan plan,
        TargetCollectionPlan targetPlan,
        Dictionary<string, SourceYmt> sources,
        Dictionary<string, List<SourceCreatureMetadata>> creatureMetadataByResource)
    {
        var builder = new CreatureMetadataBuilder();
        foreach (var sourcePath in targetPlan.SourceYmts)
        {
            var source = sources[sourcePath];
            var sourceDrawableMappings = plan.DrawableMappings
                .Where(mapping => mapping.SourceYmtPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
                                  && mapping.TargetFullCollection.Equals(targetPlan.FullCollectionName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var sourcePropMappings = plan.PropMappings
                .Where(mapping => mapping.SourceYmtPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
                                  && mapping.TargetFullCollection.Equals(targetPlan.FullCollectionName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (creatureMetadataByResource.TryGetValue(source.ResourceName, out var resourceCreatureMetadata))
            {
                foreach (var metadata in resourceCreatureMetadata)
                {
                    builder.Add(metadata, sourceDrawableMappings, sourcePropMappings);
                }
            }

            if (IsRepairCreatureMetadataMode(plan.Settings.CreatureMetadataMode))
            {
                builder.AddRepairHints(source, sourceDrawableMappings, sourcePropMappings);
            }
        }

        return builder.BuildXml();
    }

    private static bool IsRepairCreatureMetadataMode(string mode)
        => mode.Equals("repair", StringComparison.OrdinalIgnoreCase);

    private static bool IsCreatureMetadataMode(string mode)
        => mode.Equals("repair", StringComparison.OrdinalIgnoreCase)
           || mode.Equals("preserve", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyPedVariationXml(string path)
        => path.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static string InferPedBaseName(string fullCollectionName)
    {
        var match = Regex.Match(fullCollectionName, @"^(.*)_(merged_[fm]_\d+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : fullCollectionName;
    }

    private static bool IsMergeableFreemodeSource(SourceYmt source)
        => source.Gender is PedGender.Female or PedGender.Male
           && (source.PedBaseName.Equals("mp_f_freemode_01", StringComparison.OrdinalIgnoreCase)
               || source.PedBaseName.Equals("mp_m_freemode_01", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<StandaloneResourcePlan> BuildStandaloneResourcePlans(
        IReadOnlyList<SourceYmt> sources,
        IReadOnlyList<StreamFile> streamFiles,
        string targetResource,
        List<string> warnings)
    {
        var plans = new List<StandaloneResourcePlan>();
        foreach (var resourceGroup in sources.GroupBy(source => (source.ResourceName, source.ResourceRoot)))
        {
            var outputResource = $"{targetResource}_standalone_{SanitizeResourceName(resourceGroup.Key.ResourceName)}";
            var files = new Dictionary<string, SourceFileCopyPlan>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in resourceGroup)
            {
                warnings.Add($"Non-freemode YMT will be copied unchanged into standalone resource '{outputResource}': {source.YmtPath}");
                AddStandaloneFile(files, source.ResourceRoot, source.YmtPath, outputResource);

                foreach (var streamFile in streamFiles.Where(file =>
                             file.ResourceRoot.Equals(source.ResourceRoot, StringComparison.OrdinalIgnoreCase)
                             && IsRelatedStandaloneStreamFile(file, source)))
                {
                    AddStandaloneFile(files, source.ResourceRoot, streamFile.FullPath, outputResource);
                }
            }

            plans.Add(new StandaloneResourcePlan(
                resourceGroup.Key.ResourceName,
                outputResource,
                files.Values.OrderBy(file => file.OutputPath, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return plans;
    }

    private static void AddStandaloneFile(Dictionary<string, SourceFileCopyPlan> files, string sourceRoot, string sourcePath, string outputResource)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
        files[sourcePath] = new SourceFileCopyPlan(sourcePath, $"{outputResource}/{relativePath}");
    }

    private static bool IsRelatedStandaloneStreamFile(StreamFile file, SourceYmt source)
    {
        if (file.FullPath.Equals(source.YmtPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return file.FileName.StartsWith($"{source.FullCollectionName}^", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeResourceName(string resourceName)
        => Regex.Replace(resourceName, @"[^A-Za-z0-9_]+", "_");

    private static IEnumerable<SourceManifestWarning> ReadManifestWarnings(ResourceScanItem item)
    {
        if (item.ManifestPath is null)
        {
            yield break;
        }

        foreach (var line in File.ReadLines(item.ManifestPath))
        {
            if (line.Contains("SHOP_PED_APPAREL_META_FILE", StringComparison.OrdinalIgnoreCase))
            {
                yield return new SourceManifestWarning(
                    item.ResourceName,
                    item.ManifestPath,
                    "old-shop-meta-still-referenced",
                    line.Trim(),
                    "Review manually or rerun with future manifest-edit support after confirming the meta only references merged collections.");
            }
        }
    }

    private static string BuildMinimalShopMeta(TargetCollectionPlan plan)
        => $$"""
<?xml version="1.0" encoding="UTF-8"?>
<ShopPedApparel>
  <pedName>{{InferPedBaseName(plan.FullCollectionName)}}</pedName>
  <dlcName>{{plan.CollectionName}}</dlcName>
  <fullDlcName>{{plan.FullCollectionName}}</fullDlcName>
  <eCharacter>{{GetCharacterName(plan.Gender)}}</eCharacter>
  <creatureMetaData>MP_CreatureMetadata_{{plan.CollectionName}}</creatureMetaData>
  <pedOutfits>
  </pedOutfits>
  <pedComponents>
  </pedComponents>
  <pedProps>
  </pedProps>
</ShopPedApparel>
""";

    private static string GetCharacterName(PedGender gender)
        => gender switch
        {
            PedGender.Female => "SCR_CHAR_MULTIPLAYER_F",
            PedGender.Male => "SCR_CHAR_MULTIPLAYER",
            _ => "SCR_CHAR_MULTIPLAYER",
        };

    private static string BuildFxManifest(MergePlan plan, BuildOptions options)
    {
        var dependencies = plan.SourceYmts.Select(source => source.Resource).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
        var dataFiles = plan.TargetCollections
            .OrderBy(target => target.FullCollectionName)
            .Select(target => $"data_file 'SHOP_PED_APPAREL_META_FILE' 'data/{target.FullCollectionName}.meta'");

        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("author 'Red40 ClothingRepacker'");
        sb.AppendLine("description 'Repacked clothing collection for FiveM, generated by Red40 ClothingRepacker (https://red40.dev/)'");
        sb.AppendLine("version '1.0.0'");
        sb.AppendLine();
        sb.AppendLine("dependencies {");
        foreach (var dependency in dependencies)
        {
            sb.AppendLine($"  '{dependency}',");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("files {");
        sb.AppendLine("  'data/*.meta'");
        sb.AppendLine("}");
        sb.AppendLine();
        foreach (var dataFile in dataFiles)
        {
            sb.AppendLine(dataFile);
        }
        if (options.IncludeDebugClient)
        {
            sb.AppendLine();
            sb.AppendLine("client_script 'client/validate_collections.lua'");
        }

        return sb.ToString();
    }

    private static string BuildStandaloneFxManifest(StandaloneResourcePlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("author 'Red40 ClothingRepacker'");
        sb.AppendLine($"description 'Unmodified non-freemode clothing files copied from {plan.SourceResource}'");
        sb.AppendLine("version '1.0.0'");
        return sb.ToString();
    }

    private static string BuildValidationLua(MergePlan plan)
    {
        var collectionList = string.Join("," + Environment.NewLine, plan.TargetCollections.OrderBy(target => target.CollectionName).Select(target => $"        \"{target.CollectionName}\""));
        var expected = string.Join(Environment.NewLine, plan.TargetCollections.Select(target =>
        {
            var componentEntries = string.Join(", ", target.ComponentCounts.OrderBy(item => item.Key).Select(item => $"[{item.Key}] = {item.Value}"));
            var propEntries = string.Join(", ", target.PropCounts.OrderBy(item => item.Key).Select(item => $"[{item.Key}] = {item.Value}"));
            return $"    {target.CollectionName} = {{ components = {{ {componentEntries} }}, props = {{ {propEntries} }} }},";
        }));

        return $$"""
RegisterCommand("clothing_repacker_validate", function()
    local ped = PlayerPedId()

    local collections = {
{{collectionList}}
    }

    local expected = {
{{expected}}
    }

    for _, collection in ipairs(collections) do
        print(("Checking collection: %s"):format(collection))

        for comp = 0, 11 do
            local count = GetNumberOfPedCollectionDrawableVariations(ped, comp, collection)
            if count and count > 0 then
                print(("  component %d -> %d drawables"):format(comp, count))
            end
        end

        for anchor = 0, 12 do
            local count = GetNumberOfPedCollectionPropDrawableVariations(ped, anchor, collection)
            if count and count > 0 then
                print(("  prop anchor %d -> %d props"):format(anchor, count))
            end
        end
    end
end, false)
""";
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination, StringComparison.OrdinalIgnoreCase), overwrite: true);
        }
    }
}
