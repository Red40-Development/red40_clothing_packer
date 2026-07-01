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
    private const string AlternateVariationsKind = "alternate-variations";
    private const string FirstPersonAlternatesKind = "first-person-alternates";
    private const string AlternateVariationsFileName = "pedalternatevariations.meta";
    private const string FirstPersonAlternatesFileName = "first_person_alternates.meta";

    private readonly ResourceScanner _scanner = new();
    private readonly PedVariationReader _reader = new();
    private readonly CreatureMetadataReader _creatureMetadataReader = new();
    private readonly AlternateMetadataBuilder _alternateMetadataBuilder = new();
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
        var fullResourcesRoot = Path.GetFullPath(resourcesRoot);
        var generatedResourcesRoot = Path.GetDirectoryName(fullResourcesRoot) ?? fullResourcesRoot;
        return await AnalyzeAsync(
            _scanner.ScanResources(fullResourcesRoot, progress, cancellationToken),
            fullResourcesRoot,
            generatedResourcesRoot,
            targetResource,
            settings,
            progress,
            cancellationToken);
    }

    public async Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<string> resourceFolders, string generatedResourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (resourceFolders.Count == 0)
        {
            throw new InvalidOperationException("At least one resource folder is required.");
        }

        if (string.IsNullOrWhiteSpace(generatedResourcesRoot))
        {
            throw new InvalidOperationException("Generated resources root is required when analyzing explicit resource folders.");
        }

        var fullGeneratedResourcesRoot = Path.GetFullPath(generatedResourcesRoot);
        return await AnalyzeAsync(
            _scanner.ScanResourceFolders(resourceFolders, progress, cancellationToken),
            fullGeneratedResourcesRoot,
            fullGeneratedResourcesRoot,
            targetResource,
            settings,
            progress,
            cancellationToken);
    }

    private async Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<ResourceScanItem> scanItems, string resourcesRoot, string generatedResourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (settings.MaxDrawablesPerComponent <= 0 || settings.MaxDrawablesPerProp <= 0)
        {
            throw new InvalidOperationException("Drawable limits must be greater than zero.");
        }

        var sources = new List<SourceYmt>();
        var creatureMetadata = new List<SourceCreatureMetadata>();
        var brokenCreatureMetadata = new List<SourceCreatureMetadata>();
        var alternateMetadata = new List<SourceAlternateMetadata>();
        var streamFiles = new List<StreamFile>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var manifestWarnings = new List<SourceManifestWarning>();
        var workItems = new List<(ResourceScanItem Item, string Path)>();
        var creatureMetadataReferencesByResource = new Dictionary<string, IReadOnlyList<ShopCreatureMetadataReference>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in scanItems)
        {
            creatureMetadataReferencesByResource[item.ResourceName] = ReadShopCreatureMetadataReferences(item.ShopMetaFiles);
            alternateMetadata.AddRange(ReadAlternateMetadataFiles(item.ResourceName, item.ResourceRoot, item.ShopMetaFiles));
            streamFiles.AddRange(item.StreamFiles);
            if (item.ManifestPath is not null)
            {
                manifestWarnings.AddRange(ReadManifestWarnings(item));
            }

            var ymtFiles = await FilterDuplicateXmlSidecarsAsync(item.YmtFiles, cancellationToken);
            foreach (var path in ymtFiles.Where(IsLikelyPedVariationXml))
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
                    var metadata = _creatureMetadataReader.Read(xml, path, item.ResourceName, item.ResourceRoot);
                    if (!HasCorrespondingShopMetadata(metadata, creatureMetadataReferencesByResource[item.ResourceName]))
                    {
                        brokenCreatureMetadata.Add(metadata);
                        warnings.Add($"{path}: Creature metadata has no corresponding ShopPedApparel creatureMetaData reference and will be backed up without being merged.");
                        continue;
                    }

                    creatureMetadata.Add(metadata);
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

        var missingCreatureMetadataReferences = FindMissingCreatureMetadataReferences(creatureMetadataReferencesByResource, creatureMetadata, brokenCreatureMetadata);
        foreach (var reference in missingCreatureMetadataReferences)
        {
            warnings.Add($"{reference.ShopMetaPath}: ShopPedApparel creatureMetaData references missing creature metadata '{reference.Reference}' and generated creature metadata will be omitted for merged targets from resource '{reference.Resource}'.");
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
        foreach (var source in sources.Except(mergeableSources))
        {
            warnings.Add($"{source.YmtPath}: Non-freemode YMT skipped. It will only be copied to the generated resources root when copy-before-rename apply mode is enabled.");
        }

        var targets = _mergePlanner.Plan(mergeableSources, settings, warnings, errors);
        var drawableMappings = new List<DrawableMapping>();
        var propMappings = new List<PropMapping>();
        var targetPlans = new List<TargetCollectionPlan>();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var builder = new OutputCollectionBuilder(target.CollectionName, target.FullCollectionName, target.PedBaseName, target.Gender);
            foreach (var contribution in target.Contributions)
            {
                drawableMappings.AddRange(builder.AddComponents(contribution.Source, contribution.ComponentRanges));
                propMappings.AddRange(builder.AddProps(contribution.Source, contribution.PropRanges));
            }

            var outputYmtPath = Path.Combine(targetResource, "stream", $"{target.FullCollectionName}.ymt");
            targetPlans.Add(new TargetCollectionPlan(
                target.CollectionName,
                target.FullCollectionName,
                target.Gender,
                outputYmtPath.Replace(Path.DirectorySeparatorChar, '/'),
                target.Sources.Select(source => source.YmtPath).ToList(),
                target.Contributions
                    .SelectMany(contribution => contribution.ComponentRanges.Values)
                    .OrderBy(range => range.SourceYmtPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(range => range.SlotId)
                    .ThenBy(range => range.StartIndex)
                    .ToList(),
                target.Contributions
                    .SelectMany(contribution => contribution.PropRanges.Values)
                    .OrderBy(range => range.SourceYmtPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(range => range.SlotId)
                    .ThenBy(range => range.StartIndex)
                    .ToList(),
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
        var sourceCreatureMetadataBindings = BuildSourceCreatureMetadataBindings(sources, creatureMetadata, creatureMetadataReferencesByResource);
        var sourceYmtSummaries = sources.Select(source => new SourceYmtSummary(
            source.ResourceName,
            source.YmtPath,
            source.PedBaseName,
            source.Gender,
            source.CollectionName,
            source.FullCollectionName,
            source.DlcName,
            source.Components.ToDictionary(component => component.ComponentId, component => component.Drawables.Count),
            source.Props.ToDictionary(prop => prop.AnchorId, prop => prop.Props.Count))).ToList();
        var brokenCreatureMetadataBackups = brokenCreatureMetadata.Select(metadata => new BrokenCreatureMetadataBackupPlan(
            metadata.Path,
            Path.Combine(metadata.ResourceName, Path.GetRelativePath(metadata.ResourceRoot, metadata.Path)).Replace(Path.DirectorySeparatorChar, '/'))).ToList();
        var sourceCreatureMetadataSummaries = creatureMetadata.Select(metadata => new SourceCreatureMetadataSummary(
            metadata.ResourceName,
            metadata.Path,
            metadata.ShaderVariableComponents.Count,
            metadata.ComponentExpressions.Count,
            metadata.PropExpressions.Count,
            sourceCreatureMetadataBindings
                .Where(binding => binding.SourceMetadataPath.Equals(metadata.Path, StringComparison.OrdinalIgnoreCase))
                .Select(binding => binding.SourceYmtPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList())).ToList();
        var creatureMetadataOutputs = BuildCreatureMetadataOutputPlans(
            targetPlans,
            sourceYmtSummaries,
            brokenCreatureMetadataBackups,
            missingCreatureMetadataReferences,
            sourceCreatureMetadataBindings,
            settings,
            targetResource);
        var sourceAlternateMetadataSummaries = alternateMetadata.Select(metadata => new SourceAlternateMetadataSummary(
            metadata.ResourceName,
            metadata.Path,
            metadata.Kind,
            CountAlternateMetadataItems(metadata))).ToList();
        var alternateMetadataOutputs = BuildAlternateMetadataOutputPlans(alternateMetadata, targetResource);

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
            ResourceRoots = scanItems.Select(item => item.ResourceRoot).ToList(),
            GeneratedResourcesRoot = Path.GetFullPath(generatedResourcesRoot),
            TargetResource = targetResource,
            Settings = settings,
            SourceYmts = sourceYmtSummaries,
            TargetCollections = targetPlans,
            StandaloneResources = [],
            DrawableMappings = drawableMappings,
            PropMappings = propMappings,
            StreamRenames = streamRenames.ToList(),
            OldYmtBackups = mergeableSources.Select(source => new OldYmtBackupPlan(
                source.YmtPath,
                Path.Combine("_clothing_repacker_backups", "{runId}", source.ResourceName, Path.GetRelativePath(source.ResourceRoot, source.YmtPath)).Replace(Path.DirectorySeparatorChar, '/'))).ToList(),
            BrokenCreatureMetadataBackups = brokenCreatureMetadataBackups,
            MissingCreatureMetadataReferences = missingCreatureMetadataReferences,
            SourceManifestWarnings = manifestWarnings,
            SourceCreatureMetadata = sourceCreatureMetadataSummaries,
            CreatureMetadataOutputs = creatureMetadataOutputs,
            SourceAlternateMetadata = sourceAlternateMetadataSummaries,
            AlternateMetadataOutputs = alternateMetadataOutputs,
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
        var creatureMetadataByPath = await ReloadCreatureMetadataForPlanAsync(plan, cancellationToken);
        var alternateMetadataByPath = await ReloadAlternateMetadataForPlanAsync(plan, cancellationToken);
        var creatureMetadataOutputs = GetCreatureMetadataOutputPlans(plan);
        var creatureMetadataOutputByTarget = creatureMetadataOutputs
            .SelectMany(output => output.TargetCollections.Select(collection => new { collection, output }))
            .ToDictionary(item => item.collection, item => item.output, StringComparer.OrdinalIgnoreCase);
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
                builder.AddComponents(source, GetComponentRanges(targetPlan, source));
                builder.AddProps(source, GetPropRanges(targetPlan, source));
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

            creatureMetadataOutputByTarget.TryGetValue(targetPlan.CollectionName, out var creatureMetadataOutput);

            var metaPath = Path.Combine(fullOutputRoot, plan.TargetResource, "data", $"{targetPlan.FullCollectionName}.meta");
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            BuildShopMeta(targetPlan, xml, creatureMetadataOutput?.Name).Save(metaPath);
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

        var targetPlansByCollection = plan.TargetCollections.ToDictionary(target => target.CollectionName, target => target, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < creatureMetadataOutputs.Count; index++)
        {
            var creatureMetadataOutput = creatureMetadataOutputs[index];
            var creatureMetadataXml = BuildCreatureMetadataXml(plan, creatureMetadataOutput, targetPlansByCollection, sources, creatureMetadataByPath);
            var creatureMetadataOutputPath = Path.Combine(fullOutputRoot, creatureMetadataOutput.OutputYmtPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(creatureMetadataOutputPath)!);
            await _codec.EncodeFromXmlAsync(creatureMetadataXml, creatureMetadataOutputPath, cancellationToken);
            writtenFiles.Add(creatureMetadataOutputPath);

            if (options.IncludeYmtXml)
            {
                var previewXmlPath = creatureMetadataOutputPath + ".xml";
                creatureMetadataXml.Save(previewXmlPath);
                writtenFiles.Add(previewXmlPath);
            }
        }

        foreach (var alternateMetadataOutput in plan.AlternateMetadataOutputs)
        {
            var alternateXmls = alternateMetadataOutput.SourcePaths
                .Where(alternateMetadataByPath.ContainsKey)
                .Select(path => alternateMetadataByPath[path])
                .ToList();
            var xml = alternateMetadataOutput.Kind switch
            {
                AlternateVariationsKind => _alternateMetadataBuilder.BuildAlternateVariationsXml(alternateXmls, plan.DrawableMappings),
                FirstPersonAlternatesKind => _alternateMetadataBuilder.BuildFirstPersonAlternatesXml(alternateXmls, plan.DrawableMappings),
                _ => null,
            };
            if (xml is null)
            {
                continue;
            }

            var outputPath = Path.Combine(fullOutputRoot, alternateMetadataOutput.OutputPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            xml.Save(outputPath);
            writtenFiles.Add(outputPath);
        }

        if (plan.TargetCollections.Count > 0)
        {
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

    public Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
        => ApplyAsync(plan, backupRoot, new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = !plan.Settings.RenameStreamsInPlace,
        }, progress, cancellationToken);

    public async Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, ApplyOptions options, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        var mergedSourceYmtPaths = plan.TargetCollections
            .SelectMany(target => target.SourceYmts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resourceRootsToCopy = options.CopyResourcesToOutputBeforeRename
            ? GetResourceRootsForCopy(plan)
            : [];

        progress?.Report(new OperationProgress(
            "apply",
            "start",
            Total: resourceRootsToCopy.Count + plan.StreamRenames.Count + mergedSourceYmtPaths.Count + plan.BrokenCreatureMetadataBackups.Count,
            Message: options.CopyResourcesToOutputBeforeRename
                ? $"Preparing to copy {resourceRootsToCopy.Count} source resources, then apply {plan.StreamRenames.Count} stream renames to the output copy."
                : $"Preparing to apply {plan.StreamRenames.Count} stream renames, {mergedSourceYmtPaths.Count} YMT backups, and {plan.BrokenCreatureMetadataBackups.Count} broken creature metadata backups."));

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
        var buildResult = await BuildAsync(plan, stagingRoot, new BuildOptions
        {
            IncludeYmtXml = options.IncludeYmtXml,
            IncludeDebugClient = options.IncludeDebugClient,
        }, progress, cancellationToken);
        var entries = new List<BackupEntry>();
        var generatedResourcesRoot = GetGeneratedResourcesRoot(plan);
        var pathMap = options.CopyResourcesToOutputBeforeRename
            ? CopySourceResourcesToOutput(resourceRootsToCopy, generatedResourcesRoot, entries, progress, cancellationToken)
            : new ResourcePathMap([]);

        for (var index = 0; index < plan.StreamRenames.Count; index++)
        {
            var rename = plan.StreamRenames[index];
            var sourcePath = pathMap.Map(rename.SourcePath);
            var targetPath = pathMap.Map(rename.TargetPath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Source file missing at apply time: {sourcePath}");
            }

            var beforeHash = ComputeSha256(sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Move(sourcePath, targetPath);
            entries.Add(new BackupEntry("stream-rename", sourcePath, null, targetPath, beforeHash, ComputeSha256(targetPath), DateTimeOffset.UtcNow));

            progress?.Report(new OperationProgress(
                "apply",
                "rename-stream",
                index + 1,
                plan.StreamRenames.Count,
                targetPath,
                RenameCount: index + 1,
                BackupCount: entries.Count(entry => entry.Kind is "old-ymt" or "broken-creature-metadata")));
        }

        var mergedSources = plan.SourceYmts
            .Where(source => mergedSourceYmtPaths.Contains(source.Path))
            .ToList();

        for (var index = 0; index < mergedSources.Count; index++)
        {
            var source = mergedSources[index];
            var sourcePath = pathMap.Map(source.Path);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var backupPath = Path.Combine(backupDir, source.Resource, Path.GetFileName(source.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(sourcePath, backupPath, overwrite: true);
            var beforeHash = ComputeSha256(sourcePath);
            File.Delete(sourcePath);
            entries.Add(new BackupEntry("old-ymt", sourcePath, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow));

            progress?.Report(new OperationProgress(
                "apply",
                "backup-source-ymt",
                index + 1,
                mergedSources.Count,
                sourcePath,
                RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
                BackupCount: entries.Count(entry => entry.Kind is "old-ymt" or "broken-creature-metadata")));
        }

        for (var index = 0; index < plan.BrokenCreatureMetadataBackups.Count; index++)
        {
            var source = plan.BrokenCreatureMetadataBackups[index];
            var sourcePath = pathMap.Map(source.SourcePath);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var backupPath = Path.Combine(backupDir, source.BackupPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(sourcePath, backupPath, overwrite: true);
            var beforeHash = ComputeSha256(sourcePath);
            File.Delete(sourcePath);
            entries.Add(new BackupEntry("broken-creature-metadata", sourcePath, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow));

            progress?.Report(new OperationProgress(
                "apply",
                "backup-source-ymt",
                mergedSources.Count + index + 1,
                mergedSources.Count + plan.BrokenCreatureMetadataBackups.Count,
                sourcePath,
                RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
                BackupCount: entries.Count(entry => entry.Kind is "old-ymt" or "broken-creature-metadata")));
        }

        if (plan.TargetCollections.Count > 0)
        {
            var generatedRoot = Path.Combine(generatedResourcesRoot, plan.TargetResource);
            if (Directory.Exists(generatedRoot))
            {
                Directory.Delete(generatedRoot, recursive: true);
            }

            CopyDirectory(
                Path.Combine(buildResult.OutputRoot, plan.TargetResource),
                generatedRoot,
                progress,
                "apply",
                "copy-generated-file",
                cancellationToken);
            entries.Add(new BackupEntry("generated-resource", generatedRoot, null, generatedRoot, string.Empty, null, DateTimeOffset.UtcNow));
        }

        foreach (var standaloneResource in plan.StandaloneResources)
        {
            var standaloneGeneratedRoot = Path.Combine(generatedResourcesRoot, standaloneResource.OutputResource);
            if (Directory.Exists(standaloneGeneratedRoot))
            {
                Directory.Delete(standaloneGeneratedRoot, recursive: true);
            }

            CopyDirectory(
                Path.Combine(buildResult.OutputRoot, standaloneResource.OutputResource),
                standaloneGeneratedRoot,
                progress,
                "apply",
                "copy-generated-file",
                cancellationToken);
            entries.Add(new BackupEntry("generated-resource", standaloneGeneratedRoot, null, standaloneGeneratedRoot, string.Empty, null, DateTimeOffset.UtcNow));
        }

        progress?.Report(new OperationProgress(
            "apply",
            "copy-generated-resource",
            Message: plan.TargetCollections.Count > 0
                ? $"Copied generated resource to {Path.Combine(generatedResourcesRoot, plan.TargetResource)}."
                : "No merged freemode resource was generated.",
            RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
            BackupCount: entries.Count(entry => entry.Kind is "old-ymt" or "broken-creature-metadata"),
            WrittenFileCount: buildResult.WrittenFiles.Count));

        var manifestPath = Path.Combine(backupDir, "backup-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(entries, _jsonOptions), cancellationToken);

        progress?.Report(new OperationProgress(
            "apply",
            "complete",
            plan.StreamRenames.Count + mergedSources.Count + plan.BrokenCreatureMetadataBackups.Count,
            plan.StreamRenames.Count + mergedSources.Count + plan.BrokenCreatureMetadataBackups.Count,
            Message: $"Apply complete. Backup manifest written to {manifestPath}.",
            RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
            BackupCount: entries.Count(entry => entry.Kind is "old-ymt" or "broken-creature-metadata"),
            WrittenFileCount: buildResult.WrittenFiles.Count));

        return entries;
    }

    private static string GetGeneratedResourcesRoot(MergePlan plan)
        => !string.IsNullOrWhiteSpace(plan.GeneratedResourcesRoot)
            ? Path.GetFullPath(plan.GeneratedResourcesRoot)
            : Path.GetDirectoryName(plan.ResourcesRoot) ?? plan.ResourcesRoot;

    private static ResourcePathMap CopySourceResourcesToOutput(
        IReadOnlyList<string> resourceRoots,
        string generatedResourcesRoot,
        List<BackupEntry> entries,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var mappings = new List<ResourceRootMapping>();
        for (var index = 0; index < resourceRoots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRoot = Path.GetFullPath(resourceRoots[index]);
            var destinationRoot = Path.Combine(generatedResourcesRoot, Path.GetFileName(sourceRoot));
            ValidateResourceCopyDestination(sourceRoot, destinationRoot);

            progress?.Report(new OperationProgress(
                "apply",
                "copy-source-resource",
                index,
                resourceRoots.Count,
                sourceRoot,
                $"Copying source resource {index + 1}/{resourceRoots.Count}: {Path.GetFileName(sourceRoot)}."));

            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, recursive: true);
            }

            CopyDirectory(sourceRoot, destinationRoot, progress, "apply", "copy-source-file", cancellationToken);
            mappings.Add(new ResourceRootMapping(sourceRoot, destinationRoot));
            entries.Add(new BackupEntry("generated-resource", destinationRoot, null, destinationRoot, string.Empty, null, DateTimeOffset.UtcNow));

            progress?.Report(new OperationProgress(
                "apply",
                "copy-source-resource",
                index + 1,
                resourceRoots.Count,
                destinationRoot,
                BackupCount: entries.Count(entry => entry.Kind is "old-ymt" or "broken-creature-metadata")));
        }

        return new ResourcePathMap(mappings);
    }

    private static IReadOnlyList<string> GetResourceRootsForCopy(MergePlan plan)
    {
        var roots = plan.ResourceRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count > 0)
        {
            return roots;
        }

        roots = plan.SourceYmts
            .Select(source => TryInferResourceRoot(source.Path, source.Resource))
            .OfType<string>()
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            throw new InvalidOperationException("Copy-to-output apply mode requires resource roots in the plan. Re-run analyze with the current version and try again.");
        }

        return roots;
    }

    private static string? TryInferResourceRoot(string path, string resourceName)
    {
        var directory = Directory.Exists(path) ? new DirectoryInfo(path) : Directory.GetParent(path);
        while (directory is not null)
        {
            if (directory.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void ValidateResourceCopyDestination(string sourceRoot, string destinationRoot)
    {
        if (PathsEqual(sourceRoot, destinationRoot))
        {
            throw new InvalidOperationException($"Copy-to-output apply mode requires an output root separate from the source resource: {sourceRoot}");
        }

        if (IsPathInside(destinationRoot, sourceRoot))
        {
            throw new InvalidOperationException($"Copy-to-output apply mode cannot copy a resource inside itself: {destinationRoot}");
        }
    }

    private static bool PathsEqual(string left, string right)
        => NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInside(string path, string parent)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedParent = NormalizePath(parent);
        return normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathAtOrInside(string path, string parent)
        => PathsEqual(path, parent) || IsPathInside(path, parent);

    private static bool IsUnderAnyRoot(string path, IEnumerable<string> roots)
        => roots.Any(root => IsPathAtOrInside(path, root));

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public async Task<RestoreManifestPreview> LoadRestoreManifestPreviewAsync(string backupManifestPath, CancellationToken cancellationToken = default)
    {
        var entries = await LoadBackupEntriesAsync(backupManifestPath, cancellationToken);
        var (actions, skippedActions) = PlanRestoreActions(entries);
        return new RestoreManifestPreview(Path.GetFullPath(backupManifestPath), entries, actions, skippedActions);
    }

    public async Task RestoreAsync(string backupManifestPath, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var preview = await LoadRestoreManifestPreviewAsync(backupManifestPath, cancellationToken);
        var actions = preview.Actions;

        progress?.Report(new OperationProgress(
            "restore",
            "start",
            Total: actions.Count,
            Message: $"Preparing to restore {actions.Count} action(s) from {preview.ManifestPath}."));

        var completed = 0;
        foreach (var action in actions.Where(action => action.Kind == "delete-generated-resource"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.DestinationPath is not null && Directory.Exists(action.DestinationPath))
            {
                Directory.Delete(action.DestinationPath, recursive: true);
            }

            completed++;
            progress?.Report(new OperationProgress(
                "restore",
                "delete-generated-resource",
                completed,
                actions.Count,
                action.DestinationPath,
                $"Removed generated resource {action.DestinationPath}."));
        }

        foreach (var action in actions.Where(action => action.Kind == "copy-backup-file"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.SourcePath is null || action.DestinationPath is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
            File.Copy(action.SourcePath, action.DestinationPath, overwrite: true);

            completed++;
            progress?.Report(new OperationProgress(
                "restore",
                "copy-backup-file",
                completed,
                actions.Count,
                action.DestinationPath,
                $"Restored {action.DestinationPath} from {action.SourcePath}."));
        }

        foreach (var action in actions.Where(action => action.Kind == "move-stream-file"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.SourcePath is null || action.DestinationPath is null || !File.Exists(action.SourcePath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(action.DestinationPath)!);
            File.Move(action.SourcePath, action.DestinationPath, overwrite: true);

            completed++;
            progress?.Report(new OperationProgress(
                "restore",
                "move-stream-file",
                completed,
                actions.Count,
                action.DestinationPath,
                $"Moved {action.SourcePath} back to {action.DestinationPath}."));
        }

        progress?.Report(new OperationProgress(
            "restore",
            "complete",
            actions.Count,
            actions.Count,
            Message: $"Restore complete. Applied {completed} action(s)."));
    }

    private async Task<List<BackupEntry>> LoadBackupEntriesAsync(string backupManifestPath, CancellationToken cancellationToken)
        => JsonSerializer.Deserialize<List<BackupEntry>>(await File.ReadAllTextAsync(backupManifestPath, cancellationToken), _jsonOptions)
           ?? throw new InvalidDataException("Invalid backup manifest.");

    private static (IReadOnlyList<RestoreAction> Actions, IReadOnlyList<RestoreAction> SkippedActions) PlanRestoreActions(IReadOnlyList<BackupEntry> entries)
    {
        var generatedRoots = entries
            .Where(entry => entry.Kind == "generated-resource" && entry.AppliedPath is not null)
            .Select(entry => entry.AppliedPath!)
            .ToList();

        var actions = new List<RestoreAction>();
        var skippedActions = new List<RestoreAction>();

        foreach (var entry in entries.Where(entry => entry.Kind == "generated-resource" && entry.AppliedPath is not null))
        {
            actions.Add(new RestoreAction(
                "delete-generated-resource",
                $"Remove generated resource {entry.AppliedPath}",
                null,
                entry.AppliedPath,
                entry));
        }

        foreach (var entry in entries.Where(entry => entry.Kind is "old-ymt" or "broken-creature-metadata"))
        {
            var action = new RestoreAction(
                "copy-backup-file",
                $"Restore {entry.OriginalPath} from {entry.BackupPath}",
                entry.BackupPath,
                entry.OriginalPath,
                entry);

            if (entry.BackupPath is null || IsUnderAnyRoot(entry.OriginalPath, generatedRoots))
            {
                skippedActions.Add(action);
                continue;
            }

            actions.Add(action);
        }

        foreach (var entry in entries.Where(entry => entry.Kind == "stream-rename"))
        {
            var action = new RestoreAction(
                "move-stream-file",
                $"Move {entry.AppliedPath} back to {entry.OriginalPath}",
                entry.AppliedPath,
                entry.OriginalPath,
                entry);

            if (entry.AppliedPath is null
                || IsUnderAnyRoot(entry.OriginalPath, generatedRoots)
                || IsUnderAnyRoot(entry.AppliedPath, generatedRoots))
            {
                skippedActions.Add(action);
                continue;
            }

            actions.Add(action);
        }

        return (actions, skippedActions);
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

    private async Task<Dictionary<string, SourceCreatureMetadata>> ReloadCreatureMetadataForPlanAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SourceCreatureMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.SourceCreatureMetadata)
        {
            var xml = await _codec.DecodeToXmlAsync(source.Path, cancellationToken);
            var metadata = _creatureMetadataReader.Read(xml, source.Path, source.Resource, Path.GetDirectoryName(source.Path) ?? source.Resource);
            result[metadata.Path] = metadata;
        }

        return result;
    }

    private static async Task<Dictionary<string, XDocument>> ReloadAlternateMetadataForPlanAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.SourceAlternateMetadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[source.Path] = XDocument.Load(source.Path, LoadOptions.PreserveWhitespace);
        }

        await Task.CompletedTask;
        return result;
    }

    private static XDocument BuildCreatureMetadataXml(
        MergePlan plan,
        CreatureMetadataOutputPlan outputPlan,
        IReadOnlyDictionary<string, TargetCollectionPlan> targetPlansByCollection,
        Dictionary<string, SourceYmt> sources,
        Dictionary<string, SourceCreatureMetadata> creatureMetadataByPath)
    {
        var builder = new CreatureMetadataBuilder();
        foreach (var targetCollection in outputPlan.TargetCollections)
        {
            if (!targetPlansByCollection.TryGetValue(targetCollection, out var targetPlan))
            {
                continue;
            }

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

                foreach (var binding in outputPlan.SourceBindings.Where(binding => binding.SourceYmtPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
                {
                    if (creatureMetadataByPath.TryGetValue(binding.SourceMetadataPath, out var metadata))
                    {
                        builder.Add(metadata, sourceDrawableMappings, sourcePropMappings);
                    }
                }

                builder.AddRepairHints(source, sourceDrawableMappings, sourcePropMappings);
            }
        }

        return builder.BuildXml();
    }

    private static IReadOnlyDictionary<int, SourceIndexRange> GetComponentRanges(TargetCollectionPlan targetPlan, SourceYmt source)
    {
        if (targetPlan.ComponentRanges.Count == 0)
        {
            return source.Components.ToDictionary(
                component => component.ComponentId,
                component => new SourceIndexRange(source.YmtPath, component.ComponentId, 0, component.Drawables.Count));
        }

        return targetPlan.ComponentRanges
            .Where(range => range.SourceYmtPath.Equals(source.YmtPath, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(range => range.SlotId, range => range);
    }

    private static IReadOnlyDictionary<int, SourceIndexRange> GetPropRanges(TargetCollectionPlan targetPlan, SourceYmt source)
    {
        if (targetPlan.PropRanges.Count == 0)
        {
            return source.Props.ToDictionary(
                prop => prop.AnchorId,
                prop => new SourceIndexRange(source.YmtPath, prop.AnchorId, 0, prop.Props.Count));
        }

        return targetPlan.PropRanges
            .Where(range => range.SourceYmtPath.Equals(source.YmtPath, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(range => range.SlotId, range => range);
    }

    private static bool TargetHasUnavailableCreatureMetadata(MergePlan plan, TargetCollectionPlan targetPlan)
        => TargetHasUnavailableCreatureMetadata(
            targetPlan,
            plan.SourceYmts,
            plan.BrokenCreatureMetadataBackups,
            plan.MissingCreatureMetadataReferences);

    private static bool TargetHasUnavailableCreatureMetadata(
        TargetCollectionPlan targetPlan,
        IReadOnlyList<SourceYmtSummary> sourceYmts,
        IReadOnlyList<BrokenCreatureMetadataBackupPlan> brokenCreatureMetadataBackups,
        IReadOnlyList<MissingCreatureMetadataReference> missingCreatureMetadataReferences)
    {
        if (brokenCreatureMetadataBackups.Count == 0
            && missingCreatureMetadataReferences.Count == 0)
        {
            return false;
        }

        var targetResources = sourceYmts
            .Where(source => targetPlan.SourceYmts.Contains(source.Path, StringComparer.OrdinalIgnoreCase))
            .Select(source => source.Resource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (brokenCreatureMetadataBackups
            .Select(backup => backup.BackupPath.Split('/')[0])
            .Any(resource => targetResources.Contains(resource)))
        {
            return true;
        }

        return missingCreatureMetadataReferences
            .Select(reference => reference.Resource)
            .Any(resource => targetResources.Contains(resource));
    }

    private static IReadOnlyList<CreatureMetadataSourceBinding> BuildSourceCreatureMetadataBindings(
        IReadOnlyList<SourceYmt> sources,
        IReadOnlyList<SourceCreatureMetadata> creatureMetadata,
        Dictionary<string, IReadOnlyList<ShopCreatureMetadataReference>> creatureMetadataReferencesByResource)
    {
        var metadataByResourceAndName = creatureMetadata
            .GroupBy(metadata => metadata.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(metadata => NormalizeCreatureMetadataName(metadata.Path), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(nameGroup => nameGroup.Key, nameGroup => nameGroup.ToList(), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var bindings = new List<CreatureMetadataSourceBinding>();

        foreach (var source in sources)
        {
            if (!creatureMetadataReferencesByResource.TryGetValue(source.ResourceName, out var references)
                || !metadataByResourceAndName.TryGetValue(source.ResourceName, out var resourceMetadata))
            {
                continue;
            }

            foreach (var reference in references.Where(reference => ReferenceMatchesSource(reference, source)))
            {
                if (!resourceMetadata.TryGetValue(reference.NormalizedName, out var matchingMetadata))
                {
                    continue;
                }

                bindings.AddRange(matchingMetadata.Select(metadata => new CreatureMetadataSourceBinding(source.YmtPath, metadata.Path)));
            }
        }

        return bindings
            .DistinctBy(binding => (binding.SourceYmtPath.ToUpperInvariant(), binding.SourceMetadataPath.ToUpperInvariant()))
            .ToList();
    }

    private static bool ReferenceMatchesSource(ShopCreatureMetadataReference reference, SourceYmt source)
    {
        if (!string.IsNullOrWhiteSpace(reference.FullDlcName)
            && reference.FullDlcName.Equals(source.FullCollectionName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(reference.DlcName)
            && reference.DlcName.Equals(source.CollectionName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(reference.PedName)
                || reference.PedName.Equals(source.PedBaseName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static List<CreatureMetadataOutputPlan> BuildCreatureMetadataOutputPlans(
        IReadOnlyList<TargetCollectionPlan> targetPlans,
        IReadOnlyList<SourceYmtSummary> sourceYmts,
        IReadOnlyList<BrokenCreatureMetadataBackupPlan> brokenCreatureMetadataBackups,
        IReadOnlyList<MissingCreatureMetadataReference> missingCreatureMetadataReferences,
        IReadOnlyList<CreatureMetadataSourceBinding> sourceBindings,
        MergePlanSettings settings,
        string targetResource)
    {
        var sourceBindingsByYmt = sourceBindings
            .GroupBy(binding => binding.SourceYmtPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, PendingCreatureMetadataOutput>(StringComparer.OrdinalIgnoreCase);

        foreach (var targetPlan in targetPlans)
        {
            if (TargetHasUnavailableCreatureMetadata(
                    targetPlan,
                    sourceYmts,
                    brokenCreatureMetadataBackups,
                    missingCreatureMetadataReferences))
            {
                continue;
            }

            var targetBindings = targetPlan.SourceYmts
                .Where(sourceBindingsByYmt.ContainsKey)
                .SelectMany(sourcePath => sourceBindingsByYmt[sourcePath])
                .DistinctBy(binding => (binding.SourceYmtPath.ToUpperInvariant(), binding.SourceMetadataPath.ToUpperInvariant()))
                .ToList();
            var metadataKeyParts = targetBindings
                .Select(binding => binding.SourceMetadataPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var key = metadataKeyParts.Count == 0
                ? $"target:{targetPlan.CollectionName}"
                : string.Join("|", metadataKeyParts);

            if (!groups.TryGetValue(key, out var group))
            {
                group = new PendingCreatureMetadataOutput(key);
                groups[key] = group;
            }

            group.TargetCollections.Add(targetPlan.CollectionName);
            group.SourceBindings.AddRange(targetBindings);
        }

        var sharedIndex = 1;
        var outputs = new List<CreatureMetadataOutputPlan>();
        foreach (var group in groups.Values.OrderBy(group => group.TargetCollections.Min(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            var name = group.TargetCollections.Count == 1
                ? $"MP_CreatureMetadata_{group.TargetCollections[0]}"
                : $"MP_CreatureMetadata_{SanitizeMetadataName(settings.TargetPrefix)}_{sharedIndex++:000}";
            outputs.Add(new CreatureMetadataOutputPlan(
                name,
                Path.Combine(targetResource, "stream", $"{name}.ymt").Replace(Path.DirectorySeparatorChar, '/'),
                group.TargetCollections.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(collection => collection, StringComparer.OrdinalIgnoreCase).ToList(),
                group.SourceBindings
                    .DistinctBy(binding => (binding.SourceYmtPath.ToUpperInvariant(), binding.SourceMetadataPath.ToUpperInvariant()))
                    .OrderBy(binding => binding.SourceYmtPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(binding => binding.SourceMetadataPath, StringComparer.OrdinalIgnoreCase)
                    .ToList()));
        }

        return outputs;
    }

    private static IReadOnlyList<CreatureMetadataOutputPlan> GetCreatureMetadataOutputPlans(MergePlan plan)
    {
        if (plan.CreatureMetadataOutputs.Count > 0)
        {
            return plan.CreatureMetadataOutputs;
        }

        return plan.TargetCollections
            .Where(target => !TargetHasUnavailableCreatureMetadata(plan, target))
            .Select(target => new CreatureMetadataOutputPlan(
                $"MP_CreatureMetadata_{target.CollectionName}",
                Path.Combine(plan.TargetResource, "stream", $"MP_CreatureMetadata_{target.CollectionName}.ymt").Replace(Path.DirectorySeparatorChar, '/'),
                [target.CollectionName],
                BuildLegacyCreatureMetadataBindings(plan, target)))
            .ToList();
    }

    private static List<CreatureMetadataSourceBinding> BuildLegacyCreatureMetadataBindings(MergePlan plan, TargetCollectionPlan targetPlan)
    {
        var sourceResources = plan.SourceYmts
            .Where(source => targetPlan.SourceYmts.Contains(source.Path, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(source => source.Path, source => source.Resource, StringComparer.OrdinalIgnoreCase);
        var bindings = new List<CreatureMetadataSourceBinding>();
        foreach (var (sourcePath, resource) in sourceResources)
        {
            bindings.AddRange(plan.SourceCreatureMetadata
                .Where(metadata => metadata.Resource.Equals(resource, StringComparison.OrdinalIgnoreCase))
                .Select(metadata => new CreatureMetadataSourceBinding(sourcePath, metadata.Path)));
        }

        return bindings;
    }

    private static List<AlternateMetadataOutputPlan> BuildAlternateMetadataOutputPlans(
        IReadOnlyList<SourceAlternateMetadata> alternateMetadata,
        string targetResource)
    {
        var outputs = new List<AlternateMetadataOutputPlan>();
        AddAlternateMetadataOutput(outputs, alternateMetadata, AlternateVariationsKind, targetResource, AlternateVariationsFileName);
        AddAlternateMetadataOutput(outputs, alternateMetadata, FirstPersonAlternatesKind, targetResource, FirstPersonAlternatesFileName);
        return outputs;
    }

    private static void AddAlternateMetadataOutput(
        List<AlternateMetadataOutputPlan> outputs,
        IReadOnlyList<SourceAlternateMetadata> alternateMetadata,
        string kind,
        string targetResource,
        string fileName)
    {
        var sourcePaths = alternateMetadata
            .Where(metadata => metadata.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Select(metadata => metadata.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourcePaths.Count == 0)
        {
            return;
        }

        outputs.Add(new AlternateMetadataOutputPlan(
            kind,
            Path.Combine(targetResource, "data", fileName).Replace(Path.DirectorySeparatorChar, '/'),
            sourcePaths));
    }

    private static string SanitizeMetadataName(string name)
        => Regex.Replace(string.IsNullOrWhiteSpace(name) ? "merged" : name, @"[^A-Za-z0-9_]+", "_");

    private sealed record PendingCreatureMetadataOutput(string Key)
    {
        public List<string> TargetCollections { get; } = [];
        public List<CreatureMetadataSourceBinding> SourceBindings { get; } = [];
    }

    private static bool IsLikelyPedVariationXml(string path)
        => path.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<string>> FilterDuplicateXmlSidecarsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skippedXmlPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths.Where(path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            var ymtPath = GetMatchingYmtPath(path);
            if (ymtPath is null || !pathSet.Contains(ymtPath) || !File.Exists(ymtPath))
            {
                continue;
            }

            try
            {
                var xml = await _codec.DecodeToXmlAsync(path, cancellationToken);
                var ymtXml = await _codec.DecodeToXmlAsync(ymtPath, cancellationToken);
                if (XmlDocumentsMatch(xml, ymtXml))
                {
                    skippedXmlPaths.Add(path);
                }
            }
            catch
            {
                // Keep both files in the work list so normal analysis can report the real decode/parse error.
            }
        }

        return paths.Where(path => !skippedXmlPaths.Contains(path)).ToList();
    }

    private static string? GetMatchingYmtPath(string xmlPath)
    {
        if (xmlPath.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase))
        {
            return xmlPath[..^".xml".Length];
        }

        if (!xmlPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(
            Path.GetDirectoryName(xmlPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(xmlPath)}.ymt");
    }

    private static bool XmlDocumentsMatch(XDocument left, XDocument right)
    {
        if (left.Root is null || right.Root is null)
        {
            return left.Root is null && right.Root is null;
        }

        return XNode.DeepEquals(NormalizeXml(left.Root), NormalizeXml(right.Root));
    }

    private static XElement NormalizeXml(XElement element)
        => new(
            element.Name,
            element.Attributes()
                .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
                .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .Select(attribute => new XAttribute(attribute.Name, attribute.Value)),
            element.Nodes().Select(NormalizeXmlNode).Where(node => node is not null)!);

    private static XNode? NormalizeXmlNode(XNode node)
        => node switch
        {
            XElement element => NormalizeXml(element),
            XCData cdata => new XCData(cdata.Value.Trim()),
            XText text when string.IsNullOrWhiteSpace(text.Value) => null,
            XText text => new XText(text.Value.Trim()),
            _ => null,
        };

    private static IReadOnlyList<ShopCreatureMetadataReference> ReadShopCreatureMetadataReferences(IReadOnlyList<string> shopMetaFiles)
    {
        var result = new List<ShopCreatureMetadataReference>();
        foreach (var path in shopMetaFiles)
        {
            try
            {
                var xml = XDocument.Load(path);
                if (xml.Root?.Name.LocalName != "ShopPedApparel")
                {
                    continue;
                }

                var reference = xml.Root.Element("creatureMetaData")?.Value.Trim();
                if (!string.IsNullOrWhiteSpace(reference))
                {
                    result.Add(new ShopCreatureMetadataReference(
                        path,
                        reference,
                        NormalizeCreatureMetadataName(reference),
                        xml.Root.Element("pedName")?.Value.Trim() ?? string.Empty,
                        xml.Root.Element("dlcName")?.Value.Trim() ?? string.Empty,
                        xml.Root.Element("fullDlcName")?.Value.Trim() ?? string.Empty));
                }
            }
            catch
            {
                // Non-XML files can share these extensions in source resources; ignore them unless they decode as ShopPedApparel.
            }
        }

        return result;
    }

    private static IReadOnlyList<SourceAlternateMetadata> ReadAlternateMetadataFiles(
        string resourceName,
        string resourceRoot,
        IReadOnlyList<string> metaFiles)
    {
        var result = new List<SourceAlternateMetadata>();
        foreach (var path in metaFiles)
        {
            try
            {
                var xml = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                var kind = xml.Root?.Name.LocalName switch
                {
                    "CAlternateVariations" => AlternateVariationsKind,
                    "FirstPersonAlternateData" => FirstPersonAlternatesKind,
                    _ => null,
                };
                if (kind is null)
                {
                    continue;
                }

                result.Add(new SourceAlternateMetadata(path, resourceName, resourceRoot, kind, xml));
            }
            catch
            {
                // Source resources often contain loose meta files with non-XML content; ignore anything that does not parse as a supported alternate metadata file.
            }
        }

        return result;
    }

    private static int CountAlternateMetadataItems(SourceAlternateMetadata metadata)
        => metadata.Kind switch
        {
            AlternateVariationsKind => metadata.Xml.Root?.Element("peds")?.Elements("Item")
                .SelectMany(ped => ped.Element("switches")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                .Count() ?? 0,
            FirstPersonAlternatesKind => metadata.Xml.Root?.Element("alternates")?.Elements("Item").Count() ?? 0,
            _ => 0,
        };

    private static bool HasCorrespondingShopMetadata(SourceCreatureMetadata metadata, IReadOnlyList<ShopCreatureMetadataReference> creatureMetadataReferences)
        => creatureMetadataReferences.Any(reference => reference.NormalizedName.Equals(NormalizeCreatureMetadataName(metadata.Path), StringComparison.OrdinalIgnoreCase));

    private static List<MissingCreatureMetadataReference> FindMissingCreatureMetadataReferences(
        Dictionary<string, IReadOnlyList<ShopCreatureMetadataReference>> creatureMetadataReferencesByResource,
        IReadOnlyList<SourceCreatureMetadata> creatureMetadata,
        IReadOnlyList<SourceCreatureMetadata> brokenCreatureMetadata)
    {
        var availableNamesByResource = creatureMetadata
            .Concat(brokenCreatureMetadata)
            .GroupBy(metadata => metadata.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(metadata => NormalizeCreatureMetadataName(metadata.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var missing = new List<MissingCreatureMetadataReference>();
        foreach (var (resource, references) in creatureMetadataReferencesByResource)
        {
            availableNamesByResource.TryGetValue(resource, out var availableNames);
            foreach (var reference in references)
            {
                if (availableNames is not null && availableNames.Contains(reference.NormalizedName))
                {
                    continue;
                }

                missing.Add(new MissingCreatureMetadataReference(resource, reference.ShopMetaPath, reference.Reference));
            }
        }

        return missing
            .DistinctBy(reference => (reference.Resource.ToUpperInvariant(), reference.ShopMetaPath.ToUpperInvariant(), reference.Reference.ToUpperInvariant()))
            .ToList();
    }

    private sealed record ShopCreatureMetadataReference(
        string ShopMetaPath,
        string Reference,
        string NormalizedName,
        string PedName,
        string DlcName,
        string FullDlcName);

    private sealed record SourceAlternateMetadata(
        string Path,
        string ResourceName,
        string ResourceRoot,
        string Kind,
        XDocument Xml);

    private static string NormalizeCreatureMetadataName(string value)
    {
        var name = Path.GetFileName(value.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        while (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        return name;
    }

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
            else if (line.Contains("ALTERNATE_VARIATIONS_FILE", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("PED_FIRST_PERSON_ALTERNATE_DATA", StringComparison.OrdinalIgnoreCase))
            {
                yield return new SourceManifestWarning(
                    item.ResourceName,
                    item.ManifestPath,
                    "old-alternate-meta-still-referenced",
                    line.Trim(),
                    "Review manually after apply because alternate metadata entries for merged clothing are regenerated in the target resource.");
            }
        }
    }

    private static XDocument BuildShopMeta(TargetCollectionPlan plan, XDocument pedVariationXml, string? creatureMetadataName = null)
        => new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("ShopPedApparel",
                new XElement("pedName", InferPedBaseName(plan.FullCollectionName)),
                new XElement("dlcName", plan.CollectionName),
                new XElement("fullDlcName", plan.FullCollectionName),
                new XElement("eCharacter", GetCharacterName(plan.Gender)),
                string.IsNullOrWhiteSpace(creatureMetadataName) ? null : new XElement("creatureMetaData", creatureMetadataName),
                new XElement("pedOutfits", new XAttribute("itemType", "ShopPedOutfit")),
                new XElement("pedComponents", new XAttribute("itemType", "ShopPedComponent"), BuildShopComponentItems(plan, pedVariationXml)),
                new XElement("pedProps", new XAttribute("itemType", "ShopPedProp"), BuildShopPropItems(plan, pedVariationXml))));

    private static IEnumerable<XElement> BuildShopComponentItems(TargetCollectionPlan plan, XDocument pedVariationXml)
    {
        var root = pedVariationXml.Root;
        if (root is null)
        {
            yield break;
        }

        var availComp = XmlHelpers.ParseIntList(root.Element("availComp")?.Value ?? string.Empty);
        var componentData = XmlHelpers.Items(root.Element("aComponentData3"));
        for (var componentId = 0; componentId < Math.Min(availComp.Length, ClothingConstants.ComponentSlotCount); componentId++)
        {
            var componentDataIndex = availComp[componentId];
            if (componentDataIndex == ClothingConstants.MissingComponent
                || componentDataIndex < 0
                || componentDataIndex >= componentData.Count)
            {
                continue;
            }

            var drawables = XmlHelpers.Items(componentData[componentDataIndex].Element("aDrawblData3"));
            for (var drawableIndex = 0; drawableIndex < drawables.Count; drawableIndex++)
            {
                var textureCount = Math.Max(1, XmlHelpers.Items(drawables[drawableIndex].Element("aTexData")).Count);
                for (var textureIndex = 0; textureIndex < textureCount; textureIndex++)
                {
                    var prefix = ClothingConstants.ComponentPrefixes.GetValueOrDefault(componentId, $"comp_{componentId}");
                    var uniqueName = $"{plan.FullCollectionName}_{prefix}_{drawableIndex:000}_{textureIndex:00}";
                    yield return BuildBaseShopItem(uniqueName, "CLO_SHOP_NONE",
                        new XElement("componentId", new XAttribute("value", componentId)),
                        new XElement("drawableId", new XAttribute("value", drawableIndex)),
                        new XElement("textureId", new XAttribute("value", textureIndex)),
                        new XElement("compDrawableId", new XAttribute("value", drawableIndex)),
                        new XElement("compTexId", new XAttribute("value", textureIndex)),
                        new XElement("isInOutfit", new XAttribute("value", "false")));
                }
            }
        }
    }

    private static IEnumerable<XElement> BuildShopPropItems(TargetCollectionPlan plan, XDocument pedVariationXml)
    {
        var root = pedVariationXml.Root;
        if (root is null)
        {
            yield break;
        }

        var propMetadata = XmlHelpers.Items(root.Element("propInfo")?.Element("aPropMetaData"))
            .Select(item => new
            {
                Item = item,
                AnchorId = TryGetElementValue(item, "anchorId", out var anchorId) ? anchorId : -1,
                PropId = TryGetElementValue(item, "propId", out var propId) ? propId : -1,
            })
            .Where(item => item.AnchorId >= 0 && item.PropId >= 0)
            .OrderBy(item => item.AnchorId)
            .ThenBy(item => item.PropId);

        foreach (var prop in propMetadata)
        {
            var textureCount = Math.Max(1, XmlHelpers.Items(prop.Item.Element("aTexData")).Count);
            for (var textureIndex = 0; textureIndex < textureCount; textureIndex++)
            {
                var prefix = ClothingConstants.PropPrefixes.GetValueOrDefault(prop.AnchorId, $"prop_{prop.AnchorId}");
                var uniqueName = $"{plan.FullCollectionName}_{prefix}_{prop.PropId:000}_{textureIndex:00}";
                yield return BuildBaseShopItem(uniqueName, "CLO_SHOP_NONE",
                    new XElement("anchorId", new XAttribute("value", prop.AnchorId)),
                    new XElement("propId", new XAttribute("value", prop.PropId)),
                    new XElement("textureId", new XAttribute("value", textureIndex)),
                    new XElement("propAnchorId", new XAttribute("value", prop.AnchorId)),
                    new XElement("propDrawableId", new XAttribute("value", prop.PropId)),
                    new XElement("propTexId", new XAttribute("value", textureIndex)),
                    new XElement("isInOutfit", new XAttribute("value", "false")));
            }
        }
    }

    private static XElement BuildBaseShopItem(string uniqueName, string shop, params object[] fields)
        => new("Item",
            new XElement("lockHash", 0),
            new XElement("cost", new XAttribute("value", 0)),
            new XElement("textLabel", uniqueName),
            new XElement("uniqueNameHash", uniqueName),
            new XElement("eShopEnum", shop),
            fields);

    private static bool TryGetElementValue(XElement item, string name, out int value)
    {
        value = 0;
        var attribute = item.Element(name)?.Attribute("value");
        return attribute is not null && int.TryParse(attribute.Value, out value);
    }

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
        var hasAlternateVariations = plan.AlternateMetadataOutputs.Any(output => output.Kind.Equals(AlternateVariationsKind, StringComparison.OrdinalIgnoreCase));
        var hasFirstPersonAlternates = plan.AlternateMetadataOutputs.Any(output => output.Kind.Equals(FirstPersonAlternatesKind, StringComparison.OrdinalIgnoreCase));

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
        if (hasAlternateVariations)
        {
            sb.AppendLine($"data_file 'ALTERNATE_VARIATIONS_FILE' 'data/{AlternateVariationsFileName}'");
        }
        if (hasFirstPersonAlternates)
        {
            sb.AppendLine($"data_file 'PED_FIRST_PERSON_ALTERNATE_DATA' 'data/{FirstPersonAlternatesFileName}'");
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

    private static void CopyDirectory(
        string source,
        string destination,
        IProgress<OperationProgress>? progress = null,
        string operation = "copy",
        string stage = "copy-file",
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var destinationFile = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            File.Copy(file, destinationFile, overwrite: true);
            progress?.Report(new OperationProgress(
                operation,
                stage,
                index + 1,
                files.Count,
                destinationFile));
        }
    }

    private sealed record ResourceRootMapping(string SourceRoot, string DestinationRoot);

    private sealed class ResourcePathMap
    {
        private readonly IReadOnlyList<ResourceRootMapping> _mappings;

        public ResourcePathMap(IReadOnlyList<ResourceRootMapping> mappings)
        {
            _mappings = mappings
                .OrderByDescending(mapping => NormalizePath(mapping.SourceRoot).Length)
                .ToList();
        }

        public string Map(string path)
        {
            if (_mappings.Count == 0)
            {
                return path;
            }

            var fullPath = Path.GetFullPath(path);
            foreach (var mapping in _mappings)
            {
                if (PathsEqual(fullPath, mapping.SourceRoot))
                {
                    return mapping.DestinationRoot;
                }

                if (!IsPathInside(fullPath, mapping.SourceRoot))
                {
                    continue;
                }

                return Path.Combine(mapping.DestinationRoot, Path.GetRelativePath(mapping.SourceRoot, fullPath));
            }

            return path;
        }
    }
}
