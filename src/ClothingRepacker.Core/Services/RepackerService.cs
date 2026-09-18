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
        var scanItems = _scanner.ScanResourceFolders(resourceFolders, progress, cancellationToken);
        if (scanItems.Count == 0)
        {
            throw new InvalidOperationException("At least one non-backup resource folder is required.");
        }

        return await AnalyzeAsync(
            scanItems,
            FindCommonResourcesRoot(scanItems.Select(item => item.ResourceRoot)),
            fullGeneratedResourcesRoot,
            targetResource,
            settings,
            progress,
            cancellationToken);
    }

    private static string FindCommonResourcesRoot(IEnumerable<string> resourceRoots)
    {
        var roots = resourceRoots.Select(Path.GetFullPath).ToList();
        var commonRoot = Directory.GetParent(roots[0])?.FullName ?? roots[0];

        foreach (var root in roots.Skip(1))
        {
            while (!IsPathAtOrInside(root, commonRoot))
            {
                commonRoot = Directory.GetParent(commonRoot)?.FullName
                    ?? throw new InvalidOperationException("Selected resource folders do not share a common root.");
            }
        }

        if (ResourceFolderDiscovery.IsBracketFolder(commonRoot))
        {
            return Directory.GetParent(commonRoot)?.FullName ?? commonRoot;
        }

        return commonRoot;
    }

    private async Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<ResourceScanItem> scanItems, string resourcesRoot, string generatedResourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (settings.MaxDrawablesPerComponent <= 0 || settings.MaxDrawablesPerProp <= 0)
        {
            throw new InvalidOperationException("Drawable limits must be greater than zero.");
        }

        if (settings.MaxDrawablesPerComponent > ClothingConstants.MaximumDrawablesPerComponent)
        {
            throw new InvalidOperationException($"Component drawable limit cannot exceed {ClothingConstants.MaximumDrawablesPerComponent} (indices 0-{ClothingConstants.MaximumDrawablesPerComponent - 1}).");
        }

        if (settings.MaxDrawablesPerProp > ClothingConstants.MaximumDrawablesPerProp)
        {
            throw new InvalidOperationException($"Prop drawable limit cannot exceed {ClothingConstants.MaximumDrawablesPerProp}; the YMT numAvailProps field is an unsigned byte and 256 wraps to zero.");
        }

        if (!SafePath.IsSafeName(targetResource))
        {
            throw new InvalidOperationException($"Target resource name is unsafe: {targetResource}");
        }

        ValidateGeneratedResourcesRoot(
            scanItems.Select(item => item.ResourceRoot),
            generatedResourcesRoot,
            settings.RenameStreamsInPlace
                ? GeneratedResourcesRootUsage.GeneratedOnly
                : GeneratedResourcesRootUsage.CopySourceResources);

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
        var decodedDocuments = new Dictionary<string, Task<XDocument>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in scanItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            creatureMetadataReferencesByResource[item.ResourceName] = ReadShopCreatureMetadataReferences(item.ShopMetaFiles);
            alternateMetadata.AddRange(ReadAlternateMetadataFiles(item.ResourceName, item.ResourceRoot, item.ShopMetaFiles, cancellationToken));
            streamFiles.AddRange(item.StreamFiles);
            if (item.ManifestPath is not null)
            {
                manifestWarnings.AddRange(ReadManifestWarnings(item));
            }

            var ymtFiles = await FilterDuplicateXmlSidecarsAsync(item.YmtFiles, cancellationToken, decodedDocuments);
            foreach (var path in ymtFiles.Where(IsLikelyPedVariationXml))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            XDocument? xml = null;
            try
            {
                xml = await DecodeToXmlCachedAsync(path, cancellationToken, decodedDocuments);
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (IsExplicitYmtPath(path) || (xml is not null && IsSupportedClothingRoot(xml)))
                {
                    errors.Add($"{path}: {ex.Message}");
                }
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
            foreach (var source in target.Sources)
            {
                drawableMappings.AddRange(builder.AddComponents(source, GetComponentRanges(target.Contributions, source)));
                propMappings.AddRange(builder.AddProps(source, GetPropRanges(target.Contributions, source)));
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

        progress?.Report(new OperationProgress(
            "analyze",
            "finalize-plan",
            targetPlans.Count,
            targetPlans.Count,
            Message: "Finalizing merge plan.",
            SourceCount: sources.Count,
            WarningCount: warnings.Count,
            ErrorCount: errors.Count,
            TargetCount: targetPlans.Count));

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
            source.Props.ToDictionary(prop => prop.AnchorId, prop => prop.Props.Count),
            source.CreatureComponentRepairHints.Count > 0 || source.CreaturePropRepairHints.Count > 0)).ToList();
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
            sourceCreatureMetadataBindings,
            settings,
            targetResource);
        var sourceAlternateMetadataSummaries = alternateMetadata.Select(metadata => new SourceAlternateMetadataSummary(
            metadata.ResourceName,
            metadata.Path,
            metadata.Kind,
            CountAlternateMetadataItems(metadata))).ToList();
        var alternateMetadataOutputs = BuildAlternateMetadataOutputPlans(alternateMetadata, targetResource);
        var sourceAlternateMetadataBackups = alternateMetadata.Select(metadata => new SourceAlternateMetadataBackupPlan(
            metadata.Path,
            Path.Combine(metadata.ResourceName, Path.GetRelativePath(metadata.ResourceRoot, metadata.Path)).Replace(Path.DirectorySeparatorChar, '/'))).ToList();
        var sourceFiles = BuildSourceFingerprints(scanItems, sources, creatureMetadata, brokenCreatureMetadata, alternateMetadata, streamRenames, errors);

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
            SourceFiles = sourceFiles,
            SourceAlternateMetadataBackups = sourceAlternateMetadataBackups,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };

        return new AnalyzeResult(plan, sources, streamFiles, creatureMetadata);
    }

    public async Task SavePlanAsync(MergePlan plan, string outputPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("Plan output path is required.");
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await using var stream = File.Create(fullOutputPath);
        await JsonSerializer.SerializeAsync(stream, plan, _jsonOptions, cancellationToken);
    }

    public async Task<MergePlan> LoadPlanAsync(string planPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(planPath);
        var plan = await JsonSerializer.DeserializeAsync<MergePlan>(stream, _jsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Could not read plan {planPath}.");
        if (plan.SchemaVersion != 2)
        {
            throw new InvalidDataException($"Unsupported plan schema version {plan.SchemaVersion}; rerun Analyze to create a current plan.");
        }

        return plan;
    }
    public async Task<BuildResult> BuildAsync(MergePlan plan, string outputRoot, BuildOptions? options = null, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new BuildOptions();
        var validationErrors = _planValidator.Validate(plan);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        }

        var fullOutputRoot = Path.GetFullPath(outputRoot);
        ValidateGeneratedResourcesRoot(GetKnownResourceRoots(plan), fullOutputRoot, GeneratedResourcesRootUsage.GeneratedOnly);
        ValidateBuildOutputRoot(plan, fullOutputRoot);
        ValidateCurrentFingerprints(plan);

        var finalOutputRoot = fullOutputRoot;
        var stagingRoot = CreateStagingRoot(
            "build",
            Directory.GetParent(fullOutputRoot)?.FullName ?? Path.GetPathRoot(fullOutputRoot));
        fullOutputRoot = Path.Combine(stagingRoot, "content");
        try
        {
            var sources = await ReloadSourcesForPlanAsync(plan, progress, cancellationToken);
            var creatureMetadataByPath = await ReloadCreatureMetadataForPlanAsync(plan, cancellationToken);
            var alternateMetadataByPath = ReloadAlternateMetadataForPlan(plan, cancellationToken);
            var sourceShopMetadata = LoadSourceShopMetadataIndex(plan, cancellationToken);
            var mappingIndex = MappingIndex.Create(plan.DrawableMappings, plan.PropMappings);
            var creatureMetadataOutputs = plan.CreatureMetadataOutputs;
            var creatureMetadataOutputByTarget = creatureMetadataOutputs
                .SelectMany(output => output.TargetCollections.Select(collection => new { collection, output }))
                .ToDictionary(item => item.collection, item => item.output, StringComparer.OrdinalIgnoreCase);
            var writtenFiles = new List<string>();

            progress?.Report(new OperationProgress(
                "build",
                "start",
                Total: plan.TargetCollections.Count,
                Message: $"Loaded {sources.Count} source YMTs for {plan.TargetCollections.Count} target collections.",
                SourceCount: sources.Count));

            for (var index = 0; index < plan.TargetCollections.Count; index++)
            {
                var targetPlan = plan.TargetCollections[index];
                var ymtOutputPath = SafePath.ResolveInsideRoot(fullOutputRoot, targetPlan.OutputYmtPath);
                progress?.Report(new OperationProgress(
                    "build",
                    "build-target",
                    index + 1,
                    plan.TargetCollections.Count,
                    ymtOutputPath,
                    $"Building target collection {targetPlan.FullCollectionName}.",
                    SourceCount: sources.Count,
                    TargetCount: index));

                try
                {
                    var builder = new OutputCollectionBuilder(targetPlan.CollectionName, targetPlan.FullCollectionName, InferPedBaseName(targetPlan.FullCollectionName), targetPlan.Gender);
                    foreach (var sourcePath in targetPlan.SourceYmts)
                    {
                        var source = sources[sourcePath];
                        builder.AddComponents(source, GetComponentRanges(targetPlan, source));
                        builder.AddProps(source, GetPropRanges(targetPlan, source));
                    }

                    var xml = builder.BuildXml();
                    Directory.CreateDirectory(Path.GetDirectoryName(ymtOutputPath)!);
                    await EncodeYmtWithDiagnosticsAsync(
                        xml,
                        ymtOutputPath,
                        $"Failed to encode target collection '{targetPlan.FullCollectionName}'",
                        cancellationToken,
                        SafePath.ResolveInsideRoot(finalOutputRoot, targetPlan.OutputYmtPath));
                    writtenFiles.Add(ymtOutputPath);

                    if (options.IncludeYmtXml)
                    {
                        var previewXmlPath = ymtOutputPath + ".xml";
                        xml.Save(previewXmlPath);
                        writtenFiles.Add(previewXmlPath);
                    }

                    creatureMetadataOutputByTarget.TryGetValue(targetPlan.CollectionName, out var creatureMetadataOutput);
                    var metaPath = SafePath.ResolveInsideRoot(fullOutputRoot, $"{plan.TargetResource}/data/{targetPlan.FullCollectionName}.meta");
                    Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
                    BuildShopMeta(targetPlan, xml, sourceShopMetadata, mappingIndex, creatureMetadataOutput?.Name).Save(metaPath);
                    writtenFiles.Add(metaPath);
                }
                catch (Exception ex) when (IsContextWrappable(ex))
                {
                    throw CreateContextException(
                        $"Failed while building target collection '{targetPlan.FullCollectionName}' for output '{SafePath.ResolveInsideRoot(finalOutputRoot, targetPlan.OutputYmtPath)}'",
                        ex);
                }

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
                var creatureMetadataOutputPath = SafePath.ResolveInsideRoot(fullOutputRoot, creatureMetadataOutput.OutputYmtPath);
                progress?.Report(new OperationProgress(
                    "build",
                    "build-creature-metadata",
                    index + 1,
                    creatureMetadataOutputs.Count,
                    creatureMetadataOutputPath,
                    $"Building creature metadata {creatureMetadataOutput.Name}.",
                    SourceCount: sources.Count,
                    TargetCount: plan.TargetCollections.Count,
                    WrittenFileCount: writtenFiles.Count));

                try
                {
                    var creatureMetadataXml = BuildCreatureMetadataXml(creatureMetadataOutput, targetPlansByCollection, sources, creatureMetadataByPath, mappingIndex);
                    Directory.CreateDirectory(Path.GetDirectoryName(creatureMetadataOutputPath)!);
                    await EncodeYmtWithDiagnosticsAsync(
                        creatureMetadataXml,
                        creatureMetadataOutputPath,
                        $"Failed to encode creature metadata '{creatureMetadataOutput.Name}'",
                        cancellationToken,
                        SafePath.ResolveInsideRoot(finalOutputRoot, creatureMetadataOutput.OutputYmtPath));

                    if (options.IncludeYmtXml)
                    {
                        var previewXmlPath = creatureMetadataOutputPath + ".xml";
                        creatureMetadataXml.Save(previewXmlPath);
                        writtenFiles.Add(previewXmlPath);
                    }
                }
                catch (Exception ex) when (IsContextWrappable(ex))
                {
                    throw CreateContextException(
                        $"Failed while building creature metadata '{creatureMetadataOutput.Name}' for output '{creatureMetadataOutputPath}'",
                        ex);
                }
            }

            foreach (var alternateMetadataOutput in plan.AlternateMetadataOutputs)
            {
                var outputPath = SafePath.ResolveInsideRoot(fullOutputRoot, alternateMetadataOutput.OutputPath);
                try
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

                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    xml.Save(outputPath);
                    writtenFiles.Add(outputPath);
                }
                catch (Exception ex) when (IsContextWrappable(ex))
                {
                    throw CreateContextException(
                        $"Failed while building alternate metadata '{alternateMetadataOutput.Kind}' for output '{outputPath}'",
                        ex);
                }
            }

            if (plan.TargetCollections.Count > 0)
            {
                var fxmanifestPath = SafePath.ResolveInsideRoot(fullOutputRoot, $"{plan.TargetResource}/fxmanifest.lua");
                Directory.CreateDirectory(Path.GetDirectoryName(fxmanifestPath)!);
                await File.WriteAllTextAsync(fxmanifestPath, BuildFxManifest(plan, options), cancellationToken);
                writtenFiles.Add(fxmanifestPath);

                if (options.IncludeDebugClient)
                {
                    var validationPath = SafePath.ResolveInsideRoot(fullOutputRoot, $"{plan.TargetResource}/client/validate_collections.lua");
                    Directory.CreateDirectory(Path.GetDirectoryName(validationPath)!);
                    await File.WriteAllTextAsync(validationPath, BuildValidationLua(plan), cancellationToken);
                    writtenFiles.Add(validationPath);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            var stagedTargetRoot = SafePath.ResolveInsideRoot(fullOutputRoot, plan.TargetResource, allowRoot: true);
            var finalTargetRoot = SafePath.ResolveInsideRoot(finalOutputRoot, plan.TargetResource, allowRoot: true);
            if (Directory.Exists(stagedTargetRoot))
            {
                WriteOwnershipMarker(stagedTargetRoot);
                ReplaceOwnedDirectory(stagedTargetRoot, finalTargetRoot);
            }
            else if (Directory.Exists(finalTargetRoot))
            {
                EnsureOwnedDirectory(finalTargetRoot);
                Directory.Delete(finalTargetRoot, recursive: true);
            }

            var finalWrittenFiles = writtenFiles
                .Select(path => Path.Combine(finalOutputRoot, Path.GetRelativePath(fullOutputRoot, path)))
                .ToList();
            progress?.Report(new OperationProgress(
                "build",
                "complete",
                plan.TargetCollections.Count,
                plan.TargetCollections.Count,
                Message: "Build complete.",
                SourceCount: sources.Count,
                TargetCount: plan.TargetCollections.Count,
                WrittenFileCount: finalWrittenFiles.Count));

            return new BuildResult(finalOutputRoot, finalWrittenFiles);
        }
        finally
        {
            DeleteStagingRoot(stagingRoot);
        }
    }

    private async Task EncodeYmtWithDiagnosticsAsync(XDocument xml, string outputYmtPath, string context, CancellationToken cancellationToken, string? diagnosticOutputYmtPath = null)
    {
        try
        {
            await _codec.EncodeFromXmlAsync(xml, outputYmtPath, cancellationToken);
        }
        catch (Exception ex) when (IsContextWrappable(ex))
        {
            var displayPath = diagnosticOutputYmtPath ?? outputYmtPath;
            var diagnosticXmlPath = TrySaveFailedXml(xml, displayPath);
            var diagnosticMessage = diagnosticXmlPath is null
                ? string.Empty
                : $" Diagnostic XML was written to '{diagnosticXmlPath}'.";

            throw CreateContextException($"{context}. Output YMT: '{displayPath}'.{diagnosticMessage}", ex);
        }
    }

    private static string? TrySaveFailedXml(XDocument xml, string outputYmtPath)
    {
        try
        {
            var diagnosticXmlPath = outputYmtPath + ".failed.xml";
            Directory.CreateDirectory(Path.GetDirectoryName(diagnosticXmlPath)!);
            xml.Save(diagnosticXmlPath);
            return diagnosticXmlPath;
        }
        catch
        {
            return null;
        }
    }

    private static WorkflowContextException CreateContextException(string context, Exception innerException)
    {
        var message = innerException is OverflowException
            ? $"{context}: a numeric value could not fit in a signed 32-bit integer. Check the source XML or failed XML preview for values outside -2147483648..2147483647 in signed integer fields. Original error: {innerException.Message}"
            : $"{context}: {innerException.Message}";

        return new WorkflowContextException(message, innerException);
    }

    private static bool IsContextWrappable(Exception ex)
        => ex is not OperationCanceledException && ex is not WorkflowContextException;

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
        var validationErrors = _planValidator.Validate(plan);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var knownRoots = GetKnownResourceRoots(plan);
        var generatedResourcesRoot = GetGeneratedResourcesRoot(plan);
        ValidateGeneratedResourcesRoot(
            knownRoots,
            generatedResourcesRoot,
            options.CopyResourcesToOutputBeforeRename
                ? GeneratedResourcesRootUsage.CopySourceResources
                : GeneratedResourcesRootUsage.GeneratedOnly);
        ValidateApplyRoots(plan, Path.GetFullPath(backupRoot), generatedResourcesRoot);
        ValidateApplyDestinations(plan, generatedResourcesRoot, resourceRootsToCopy: options.CopyResourcesToOutputBeforeRename ? GetResourceRootsForCopy(plan) : []);
        ValidateCurrentFingerprints(plan);

        var mergedSourceYmtPaths = plan.TargetCollections
            .SelectMany(target => target.SourceYmts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resourceRootsToCopy = options.CopyResourcesToOutputBeforeRename
            ? GetResourceRootsForCopy(plan)
            : [];
        var sourceAlternateMetadataBackups = GetSourceAlternateMetadataBackupPlans(plan);
        var sourceBackupPlanCount = mergedSourceYmtPaths.Count + plan.BrokenCreatureMetadataBackups.Count + sourceAlternateMetadataBackups.Count;
        progress?.Report(new OperationProgress(
            "apply",
            "start",
            Total: resourceRootsToCopy.Count + plan.StreamRenames.Count + sourceBackupPlanCount,
            Message: options.CopyResourcesToOutputBeforeRename
                ? $"Preparing to copy {resourceRootsToCopy.Count} source resources, then apply {plan.StreamRenames.Count} stream renames to the output copy."
                : $"Preparing to apply {plan.StreamRenames.Count} stream renames and {sourceBackupPlanCount} source backups."));

        var (backupDir, runId) = CreateBackupRunDirectory(Path.GetFullPath(backupRoot));
        var manifestPath = Path.Combine(backupDir, "backup-manifest.json");
        var manifest = new BackupManifest
        {
            RunId = runId,
            BackupRoot = Path.GetFullPath(backupRoot),
            SourceRoots = knownRoots.ToList(),
            GeneratedResourcesRoot = generatedResourcesRoot,
        };
        var entries = manifest.Entries;
        var stagingRoot = CreateStagingRoot("apply");
        try
        {
            await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
            progress?.Report(new OperationProgress(
                "apply",
                "build-staging",
                Message: "Building generated resource into a staging folder."));
            var buildResult = await BuildAsync(plan, stagingRoot, new BuildOptions
            {
                IncludeYmtXml = options.IncludeYmtXml,
                IncludeDebugClient = options.IncludeDebugClient,
            }, progress, cancellationToken);

            var pathMap = new ResourcePathMap([]);
            if (options.CopyResourcesToOutputBeforeRename)
            {
                var mappings = new List<ResourceRootMapping>();
                foreach (var sourceRoot in resourceRootsToCopy)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destinationRoot = GetResourceCopyDestination(sourceRoot, generatedResourcesRoot);
                    ValidateResourceCopyDestination(sourceRoot, destinationRoot);
                    if (Directory.Exists(destinationRoot))
                    {
                        EnsureOwnedDirectory(destinationRoot);
                    }

                    var renameMap = plan.StreamRenames
                        .Where(rename => SafePath.IsInside(rename.SourcePath, sourceRoot))
                        .ToDictionary(
                            rename => Path.GetRelativePath(sourceRoot, rename.SourcePath),
                            rename => Path.GetRelativePath(sourceRoot, rename.TargetPath),
                            StringComparer.OrdinalIgnoreCase);
                    var stagedDestination = CreateStagingRoot("apply-copy", Path.GetDirectoryName(destinationRoot));
                    try
                    {
                        CopyDirectory(sourceRoot, stagedDestination, progress, "apply", "copy-source-file", cancellationToken, renameMap);
                        WriteOwnershipMarker(stagedDestination, runId);
                        SanitizeResourceManifest(stagedDestination, plan);

                        var ownershipHash = ComputeSha256(Path.Combine(stagedDestination, OwnershipMarkerFileName));
                        var copyEntry = new BackupEntry("generated-resource", destinationRoot, null, destinationRoot, string.Empty, ownershipHash, DateTimeOffset.UtcNow, "planned");
                        entries.Add(copyEntry);
                        await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                        ReplaceOwnedDirectory(stagedDestination, destinationRoot);
                        entries[^1] = copyEntry with { State = "applied" };
                        await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                        mappings.Add(new ResourceRootMapping(sourceRoot, destinationRoot));
                    }
                    finally
                    {
                        DeleteStagingRoot(stagedDestination);
                    }
                }

                pathMap = new ResourcePathMap(mappings);
            }
            else
            {
                foreach (var sourceRoot in knownRoots)
                {
                    await SanitizeResourceManifestAsync(sourceRoot, plan, backupDir, manifestPath, manifest, cancellationToken);
                }
            }

            for (var index = 0; index < plan.StreamRenames.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rename = plan.StreamRenames[index];
                var sourcePath = pathMap.Map(rename.SourcePath);
                var targetPath = pathMap.Map(rename.TargetPath);
                if (options.CopyResourcesToOutputBeforeRename && File.Exists(targetPath))
                {
                    var afterHash = ComputeSha256(targetPath);
                    entries.Add(new BackupEntry("stream-rename", sourcePath, null, targetPath, afterHash, afterHash, DateTimeOffset.UtcNow, "applied"));
                    await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                    continue;
                }

                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException($"Source file missing at apply time: {sourcePath}");
                }

                var beforeHash = ComputeSha256(sourcePath);
                var renameEntry = new BackupEntry("stream-rename", sourcePath, null, targetPath, beforeHash, null, DateTimeOffset.UtcNow, "planned");
                entries.Add(renameEntry);
                await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                if (File.Exists(targetPath))
                {
                    throw new IOException($"Refusing to overwrite existing stream destination: {targetPath}");
                }

                File.Move(sourcePath, targetPath);
                entries[^1] = renameEntry with { Sha256After = ComputeSha256(targetPath), State = "applied" };
                await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
            }

            var mergedSources = plan.SourceYmts
                .Where(source => mergedSourceYmtPaths.Contains(source.Path))
                .ToList();
            for (var index = 0; index < mergedSources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = mergedSources[index];
                var sourcePath = pathMap.Map(source.Path);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                if (pathMap.HasMappings)
                {
                    File.Delete(sourcePath);
                }
                else
                {
                    var sourceRoot = GetResourceRoot(plan, source.Resource);
                    var backupPath = GetBackupDestination(backupDir, sourceRoot, sourcePath);
                    var beforeHash = ComputeSha256(sourcePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(sourcePath, backupPath, overwrite: false);
                    var backupEntry = new BackupEntry("old-ymt", sourcePath, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow, "planned");
                    entries.Add(backupEntry);
                    await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                    File.Delete(sourcePath);
                    entries[^1] = backupEntry with { State = "applied" };
                    await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                }
            }

            foreach (var source in plan.BrokenCreatureMetadataBackups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = pathMap.Map(source.SourcePath);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                if (pathMap.HasMappings)
                {
                    File.Delete(sourcePath);
                    continue;
                }

                var sourceRoot = GetResourceRootForPath(plan, source.SourcePath);
                var backupPath = GetBackupDestination(backupDir, sourceRoot, sourcePath);
                var beforeHash = ComputeSha256(sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(sourcePath, backupPath, overwrite: false);
                var backupEntry = new BackupEntry("broken-creature-metadata", sourcePath, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow, "planned");
                entries.Add(backupEntry);
                await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                File.Delete(sourcePath);
                entries[^1] = backupEntry with { State = "applied" };
                await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
            }

            foreach (var source in sourceAlternateMetadataBackups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = pathMap.Map(source.SourcePath);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                if (pathMap.HasMappings)
                {
                    File.Delete(sourcePath);
                    continue;
                }

                var sourceRoot = GetResourceRootForPath(plan, source.SourcePath);
                var backupPath = GetBackupDestination(backupDir, sourceRoot, sourcePath);
                var beforeHash = ComputeSha256(sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(sourcePath, backupPath, overwrite: false);
                var backupEntry = new BackupEntry("source-alternate-metadata", sourcePath, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow, "planned");
                entries.Add(backupEntry);
                await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                File.Delete(sourcePath);
                entries[^1] = backupEntry with { State = "applied" };
                await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
            }

            if (plan.TargetCollections.Count > 0)
            {
                var generatedRoot = SafePath.ResolveInsideRoot(generatedResourcesRoot, plan.TargetResource);
                var generatedRootIsCopiedSourceResource = resourceRootsToCopy.Any(resourceRoot =>
                    SafePath.PathsEqual(GetResourceCopyDestination(resourceRoot, generatedResourcesRoot), generatedRoot));
                if (Directory.Exists(generatedRoot))
                {
                    EnsureOwnedDirectory(generatedRoot);
                }

                var stagedReplacement = CreateStagingRoot("apply-generated", Path.GetDirectoryName(generatedRoot));
                try
                {
                    if (generatedRootIsCopiedSourceResource && Directory.Exists(generatedRoot))
                    {
                        CopyDirectory(generatedRoot, stagedReplacement, cancellationToken: cancellationToken);
                        RemoveOverlayArtifacts(stagedReplacement);
                    }

                    var stagedGeneratedTarget = SafePath.ResolveInsideRoot(buildResult.OutputRoot, plan.TargetResource);
                    CopyDirectory(stagedGeneratedTarget, stagedReplacement, progress, "apply", "copy-generated-file", cancellationToken);
                    WriteOwnershipMarker(stagedReplacement, runId);

                    var ownershipHash = ComputeSha256(Path.Combine(stagedReplacement, OwnershipMarkerFileName));
                    var generatedEntry = new BackupEntry("generated-resource", generatedRoot, null, generatedRoot, string.Empty, ownershipHash, DateTimeOffset.UtcNow, "planned");
                    entries.Add(generatedEntry);
                    await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                    ReplaceOwnedDirectory(stagedReplacement, generatedRoot);
                    entries[^1] = generatedEntry with { State = "applied" };
                    await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
                }
                finally
                {
                    DeleteStagingRoot(stagedReplacement);
                }
            }

            RecordGeneratedFileEntries(manifest, cancellationToken);
            manifest.Completed = true;
            await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
            progress?.Report(new OperationProgress(
                "apply",
                "complete",
                plan.StreamRenames.Count + sourceBackupPlanCount,
                plan.StreamRenames.Count + sourceBackupPlanCount,
                Message: $"Apply complete. Backup manifest written to {manifestPath}.",
                RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
                BackupCount: entries.Count(IsSourceBackupEntry),
                WrittenFileCount: buildResult.WrittenFiles.Count));
            return entries.ToList();
        }
        finally
        {
            DeleteStagingRoot(stagingRoot);
        }
    }

    private async Task WriteBackupManifestAsync(string manifestPath, BackupManifest manifest, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(manifest, _jsonOptions),
                cancellationToken);
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetGeneratedResourcesRoot(MergePlan plan)
        => Path.GetFullPath(plan.GeneratedResourcesRoot);

    private static void ValidateApplyDestinations(MergePlan plan, string generatedResourcesRoot, IReadOnlyList<string> resourceRootsToCopy)
    {
        if (plan.TargetCollections.Count > 0)
        {
            var generatedTarget = SafePath.ResolveInsideRoot(generatedResourcesRoot, plan.TargetResource);
            if (Directory.Exists(generatedTarget))
            {
                EnsureOwnedDirectory(generatedTarget);
            }
        }

        foreach (var sourceRoot in resourceRootsToCopy)
        {
            var destinationRoot = GetResourceCopyDestination(sourceRoot, generatedResourcesRoot);
            if (Directory.Exists(destinationRoot))
            {
                EnsureOwnedDirectory(destinationRoot);
            }
        }

        foreach (var rename in plan.StreamRenames)
        {
            if (!resourceRootsToCopy.Any(root => SafePath.IsInside(rename.SourcePath, root))
                && File.Exists(rename.TargetPath)
                && !SafePath.PathsEqual(rename.SourcePath, rename.TargetPath))
            {
                throw new InvalidOperationException($"Refusing to overwrite existing stream destination: {rename.TargetPath}");
            }
        }
    }
    private static void ValidateBuildOutputRoot(MergePlan plan, string outputRoot)
    {
        var plannedGeneratedTarget = SafePath.ResolveInsideRoot(plan.GeneratedResourcesRoot, plan.TargetResource);
        if (SafePath.IsInside(outputRoot, plannedGeneratedTarget) || SafePath.PathsEqual(outputRoot, plannedGeneratedTarget))
        {
            throw new InvalidOperationException($"Build output root must be outside the generated target: {outputRoot}");
        }

        var outputTarget = SafePath.ResolveInsideRoot(outputRoot, plan.TargetResource);
        foreach (var sourceRoot in GetKnownResourceRoots(plan))
        {
            if (SafePath.IsInside(outputTarget, sourceRoot)
                || SafePath.IsInside(sourceRoot, outputTarget)
                || SafePath.PathsEqual(outputTarget, sourceRoot))
            {
                throw new InvalidOperationException($"Build target must not overlap a selected source root: {outputTarget}");
            }
        }
    }

    private static void ValidateApplyRoots(MergePlan plan, string backupRoot, string generatedResourcesRoot)
    {
        foreach (var sourceRoot in GetKnownResourceRoots(plan))
        {
            if (SafePath.IsInside(backupRoot, sourceRoot)
                || SafePath.IsInside(sourceRoot, backupRoot)
                || SafePath.PathsEqual(backupRoot, sourceRoot))
            {
                throw new InvalidOperationException($"Backup root must be outside selected source roots: {backupRoot}");
            }
        }

        var generatedTarget = SafePath.ResolveInsideRoot(generatedResourcesRoot, plan.TargetResource);
        if (SafePath.IsInside(backupRoot, generatedTarget)
            || SafePath.IsInside(generatedTarget, backupRoot)
            || SafePath.PathsEqual(backupRoot, generatedTarget))
        {
            throw new InvalidOperationException($"Backup root must be outside the generated target: {backupRoot}");
        }
    }

    private static (string Path, string RunId) CreateBackupRunDirectory(string backupRoot)
    {
        Directory.CreateDirectory(backupRoot);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var runId = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHHmmssZ}-{Guid.NewGuid():N}";
            var path = Path.Combine(backupRoot, runId);
            try
            {
                Directory.CreateDirectory(path);
                using (new FileStream(Path.Combine(path, ".run-lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                File.Delete(Path.Combine(path, ".run-lock"));
                return (path, runId);
            }
            catch (IOException) when (attempt < 19)
            {
                // A colliding candidate is never removed; try another random suffix.
            }
        }

        throw new IOException($"Could not create a unique backup run directory under {backupRoot}.");
    }

    private static string GetResourceRoot(MergePlan plan, string resourceName)
        => GetKnownResourceRoots(plan).FirstOrDefault(root =>
               Path.GetFileName(SafePath.Normalize(root)).Equals(resourceName, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"Resource root is not present in the plan: {resourceName}");

    private static string GetResourceRootForPath(MergePlan plan, string path)
        => GetKnownResourceRoots(plan).FirstOrDefault(root => SafePath.IsInside(path, root))
           ?? throw new InvalidOperationException($"Source path is not contained by a selected resource root: {path}");

    private static string GetBackupDestination(string backupDir, string resourceRoot, string sourcePath)
    {
        var relativePath = Path.GetRelativePath(resourceRoot, sourcePath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Source path is outside its resource root: {sourcePath}");
        }

        return SafePath.ResolveInsideRoot(backupDir, Path.Combine(Path.GetFileName(SafePath.Normalize(resourceRoot)), relativePath));
    }

    private static void RemoveOverlayArtifacts(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => IsOverlayArtifact(path))
                     .ToList())
        {
            File.Delete(file);
        }
    }
    private static bool IsOverlayArtifact(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ymt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".meta", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<SourceAlternateMetadataBackupPlan> GetSourceAlternateMetadataBackupPlans(MergePlan plan)
        => plan.SourceAlternateMetadataBackups;

    private static bool IsSourceBackupEntry(BackupEntry entry)
        => entry.Kind is "old-ymt" or "broken-creature-metadata" or "source-alternate-metadata" or "resource-manifest";


    private static void SanitizeResourceManifest(string resourceRoot, MergePlan plan)
    {
        var mutation = GetManifestSanitization(resourceRoot, plan);
        if (mutation is not null)
        {
            File.WriteAllText(mutation.Path, mutation.UpdatedText);
        }
    }

    private async Task SanitizeResourceManifestAsync(
        string resourceRoot,
        MergePlan plan,
        string backupDir,
        string manifestPath,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var mutation = GetManifestSanitization(resourceRoot, plan);
        if (mutation is null)
        {
            return;
        }

        var beforeHash = ComputeSha256(mutation.Path);
        var backupPath = GetBackupDestination(backupDir, resourceRoot, mutation.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(mutation.Path, backupPath, overwrite: false);
        if (!ComputeSha256(backupPath).Equals(beforeHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Manifest backup hash mismatch: {backupPath}");
        }

        var afterHash = ComputeSha256Utf8(mutation.UpdatedText);
        var entry = new BackupEntry(
            "resource-manifest",
            mutation.Path,
            backupPath,
            mutation.Path,
            beforeHash,
            afterHash,
            DateTimeOffset.UtcNow,
            "planned");
        manifest.Entries.Add(entry);
        await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
        File.WriteAllText(mutation.Path, mutation.UpdatedText);
        if (!ComputeSha256(mutation.Path).Equals(afterHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Manifest write hash mismatch: {mutation.Path}");
        }

        manifest.Entries[^1] = entry with { State = "applied" };
        await WriteBackupManifestAsync(manifestPath, manifest, cancellationToken);
    }

    private static ManifestSanitization? GetManifestSanitization(string resourceRoot, MergePlan plan)
    {
        if (string.IsNullOrWhiteSpace(resourceRoot) || !Directory.Exists(resourceRoot))
        {
            return null;
        }

        var manifestPath = ResourceManifestLocator.Find(resourceRoot);
        if (manifestPath is null)
        {
            return null;
        }

        var originalText = File.ReadAllText(manifestPath);
        var migratedPaths = plan.SourceAlternateMetadata
            .Select(metadata => metadata.Path)
            .Concat(plan.SourceFiles
                .Where(fingerprint => fingerprint.Kind.Equals("shop-metadata", StringComparison.OrdinalIgnoreCase))
                .Select(fingerprint => fingerprint.Path)
                .Where(IsShopPedApparelMetadataFile))
            .Where(path => SafePath.IsInside(path, resourceRoot))
            .Select(path => NormalizeManifestPath(Path.GetRelativePath(resourceRoot, path)))
            .Concat(plan.SourceYmts
                .Select(source => Path.GetFileName(source.Path))
                .Select(name =>
                {
                    while (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase))
                    {
                        name = Path.GetFileNameWithoutExtension(name);
                    }

                    return NormalizeManifestPath(name + ".meta");
                }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatedText = SanitizeManifestText(originalText, migratedPaths);
        return string.Equals(originalText, updatedText, StringComparison.Ordinal)
            ? null
            : new ManifestSanitization(manifestPath, originalText, updatedText);
    }

    private static bool IsShopPedApparelMetadataFile(string path)
    {
        try
        {
            return XDocument.Load(path).Root?.Name.LocalName == "ShopPedApparel";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }


    private static string SanitizeManifestText(string text, IReadOnlySet<string> migratedPaths)
    {
        var sanitized = new StringBuilder(text.Length);
        var start = 0;
        while (start < text.Length)
        {
            var lineEnd = start;
            while (lineEnd < text.Length && text[lineEnd] is not '\r' and not '\n')
            {
                lineEnd++;
            }

            var segmentEnd = lineEnd;
            if (segmentEnd < text.Length)
            {
                segmentEnd += text[segmentEnd] == '\r'
                              && segmentEnd + 1 < text.Length
                              && text[segmentEnd + 1] == '\n'
                    ? 2
                    : 1;
            }

            var line = text[start..lineEnd];
            if (!IsManifestMigratedDataFileLine(line, migratedPaths)
                && !IsManifestMigratedFilesEntryLine(line, migratedPaths))
            {
                sanitized.Append(text, start, segmentEnd - start);
            }

            start = segmentEnd;
        }

        return sanitized.ToString();
    }

    private static bool IsManifestMigratedDataFileLine(string line, IReadOnlySet<string> migratedPaths)
    {
        var values = Regex.Matches(line, @"(?:""([^""]*)""|'([^']*)')")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .ToList();
        if (values.Count < 2
            || !Regex.IsMatch(values[0], @"^(SHOP_PED_APPAREL_META_FILE|ALTERNATE_VARIATIONS_FILE|PED_FIRST_PERSON_ALTERNATE_DATA)$", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return migratedPaths.Contains(NormalizeManifestPath(values[1]));
    }

    private static bool IsManifestMigratedFilesEntryLine(string line, IReadOnlySet<string> migratedPaths)
    {
        var values = Regex.Matches(line, @"(?:""([^""]*)""|'([^']*)')")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .ToList();
        return values.Count == 1 && migratedPaths.Contains(NormalizeManifestPath(values[0]));
    }

    private static string NormalizeManifestPath(string path)
        => path.Trim().TrimStart('.', '/', '\\').Replace('\\', '/');

    private static IReadOnlyList<string> GetResourceRootsForCopy(MergePlan plan)
    {
        var roots = GetKnownResourceRoots(plan);
        if (roots.Count > 0)
        {
            return roots;
        }

        throw new InvalidOperationException("Copy-to-output apply mode requires resource roots in the plan. Re-run analyze with the current version and try again.");
    }

    private static IReadOnlyList<string> GetKnownResourceRoots(MergePlan plan)
        => plan.ResourceRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(SafePath.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();


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

    private static void ValidateGeneratedResourcesRoot(
        IEnumerable<string> resourceRoots,
        string generatedResourcesRoot,
        GeneratedResourcesRootUsage usage)
    {
        var fullGeneratedResourcesRoot = Path.GetFullPath(generatedResourcesRoot);
        if (ResourceFolderDiscovery.IsResourceFolder(fullGeneratedResourcesRoot))
        {
            throw new InvalidOperationException($"Output root must be a folder that contains resources, not a resource folder: {fullGeneratedResourcesRoot}");
        }

        var roots = resourceRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sourceRoot in roots)
        {
            if (IsPathAtOrInside(fullGeneratedResourcesRoot, sourceRoot))
            {
                throw new InvalidOperationException($"Output root must be outside selected resource folders. Choose a folder that will contain generated resources, not a source resource: {fullGeneratedResourcesRoot}");
            }
        }

        if (usage != GeneratedResourcesRootUsage.CopySourceResources)
        {
            return;
        }

        foreach (var sourceRoot in roots)
        {
            ValidateResourceCopyDestination(sourceRoot, GetResourceCopyDestination(sourceRoot, fullGeneratedResourcesRoot));
        }
    }

    private static string GetResourceCopyDestination(string sourceRoot, string generatedResourcesRoot)
        => SafePath.ResolveInsideRoot(generatedResourcesRoot, Path.GetFileName(SafePath.Normalize(sourceRoot)));

    private static bool PathsEqual(string left, string right)
        => SafePath.PathsEqual(left, right);

    private static bool IsPathInside(string path, string parent)
        => SafePath.IsInside(path, parent);

    private static bool IsPathAtOrInside(string path, string parent)
        => PathsEqual(path, parent) || IsPathInside(path, parent);

    private static bool IsUnderAnyRoot(string path, IEnumerable<string> roots)
        => roots.Any(root => IsPathAtOrInside(path, root));

    private static string NormalizePath(string path)
        => SafePath.Normalize(path);


    public async Task<RestoreManifestPreview> LoadRestoreManifestPreviewAsync(string backupManifestPath, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadBackupManifestAsync(backupManifestPath, cancellationToken);
        ValidateManifestPaths(loaded);
        ReconcilePlannedEntries(loaded);
        var (actions, skippedActions) = PlanRestoreActions(loaded);
        return new RestoreManifestPreview(Path.GetFullPath(backupManifestPath), loaded.Manifest.Entries, actions, skippedActions);
    }

    public async Task RestoreAsync(string backupManifestPath, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadBackupManifestAsync(backupManifestPath, cancellationToken);
        ValidateManifestPaths(loaded);
        ReconcilePlannedEntries(loaded);
        ValidateRestorePreflight(loaded);
        var (actions, _) = PlanRestoreActions(loaded);

        progress?.Report(new OperationProgress(
            "restore",
            "start",
            Total: actions.Count,
            Message: $"Preparing to restore {actions.Count} action(s) from {Path.GetFullPath(backupManifestPath)}."));

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

            if (File.Exists(action.DestinationPath)
                && action.Entry.Kind != "resource-manifest"
                && ComputeSha256(action.DestinationPath).Equals(action.Entry.Sha256Before, StringComparison.OrdinalIgnoreCase))
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
            if (!File.Exists(action.DestinationPath))
            {
                File.Move(action.SourcePath, action.DestinationPath);
            }

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

    private async Task<LoadedBackupManifest> LoadBackupManifestAsync(string backupManifestPath, CancellationToken cancellationToken)
    {
        var fullManifestPath = Path.GetFullPath(backupManifestPath);
        var json = await File.ReadAllTextAsync(fullManifestPath, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(json, _jsonOptions)
                ?? throw new InvalidDataException("Invalid backup manifest.");
            if (manifest.SchemaVersion != 2)
            {
                throw new InvalidDataException($"Unsupported backup manifest schema version {manifest.SchemaVersion}.");
            }

            return new LoadedBackupManifest(fullManifestPath, manifest, IsLegacy: false);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var entries = JsonSerializer.Deserialize<List<BackupEntry>>(json, _jsonOptions)
                ?? throw new InvalidDataException("Invalid legacy backup manifest.");
            return new LoadedBackupManifest(
                fullManifestPath,
                new BackupManifest
                {
                    SchemaVersion = 1,
                    BackupRoot = Path.GetDirectoryName(fullManifestPath) ?? string.Empty,
                    Entries = entries,
                },
                IsLegacy: true);
        }

        throw new InvalidDataException("Invalid backup manifest.");
    }

    private static (IReadOnlyList<RestoreAction> Actions, IReadOnlyList<RestoreAction> SkippedActions) PlanRestoreActions(LoadedBackupManifest loaded)
    {
        var entries = loaded.Manifest.Entries;
        var generatedRoots = loaded.IsLegacy
            ? []
            : entries
                .Where(entry => entry.Kind == "generated-resource" && entry.AppliedPath is not null)
                .Select(entry => entry.AppliedPath!)
                .ToList();
        var actions = new List<RestoreAction>();
        var skippedActions = new List<RestoreAction>();

        foreach (var entry in entries.Where(entry => entry.Kind == "generated-resource" && entry.AppliedPath is not null))
        {
            var action = new RestoreAction(
                "delete-generated-resource",
                $"Remove generated resource {entry.AppliedPath}",
                null,
                entry.AppliedPath,
                entry);
            if (loaded.IsLegacy)
            {
                skippedActions.Add(action with { Description = $"{action.Description} manually; legacy manifests do not contain trustworthy root provenance." });
            }
            else
            {
                actions.Add(action);
            }
        }

        foreach (var entry in entries.Where(IsSourceBackupEntry))
        {
            var action = new RestoreAction(
                "copy-backup-file",
                $"Restore {entry.OriginalPath} from {entry.BackupPath}",
                entry.BackupPath,
                entry.OriginalPath,
                entry);
            if (entry.BackupPath is null || (!loaded.IsLegacy && IsUnderAnyRoot(entry.OriginalPath, generatedRoots)))
            {
                skippedActions.Add(action);
            }
            else
            {
                actions.Add(action);
            }
        }

        foreach (var entry in entries.Where(entry => entry.Kind == "stream-rename"))
        {
            var action = new RestoreAction(
                "move-stream-file",
                $"Move {entry.AppliedPath} back to {entry.OriginalPath}",
                entry.AppliedPath,
                entry.OriginalPath,
                entry);
            if (entry.AppliedPath is null || (!loaded.IsLegacy && (IsUnderAnyRoot(entry.OriginalPath, generatedRoots) || IsUnderAnyRoot(entry.AppliedPath, generatedRoots))))
            {
                skippedActions.Add(action);
            }
            else
            {
                actions.Add(action);
            }
        }

        return (actions, skippedActions);
    }
    private static void ValidateManifestPaths(LoadedBackupManifest loaded)
    {
        var errors = new List<string>();
        var manifestDirectory = Path.GetDirectoryName(loaded.ManifestPath) ?? string.Empty;
        if (loaded.IsLegacy)
        {
            foreach (var entry in loaded.Manifest.Entries)
            {
                if (entry.BackupPath is not null)
                {
                    try
                    {
                        SafePath.RequireInside(entry.BackupPath, manifestDirectory, allowEqual: false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        errors.Add(ex.Message);
                    }
                }
            }
        }
        else
        {
            try
            {
                SafePath.RequireInside(loaded.ManifestPath, loaded.Manifest.BackupRoot, allowEqual: false);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"Manifest path is outside its recorded backup root: {ex.Message}");
            }

            var sourceRoots = loaded.Manifest.SourceRoots.Select(SafePath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var generatedRoot = SafePath.Normalize(loaded.Manifest.GeneratedResourcesRoot);
            foreach (var entry in loaded.Manifest.Entries)
            {
                foreach (var path in new[] { entry.BackupPath, entry.OriginalPath, entry.AppliedPath }.Where(path => path is not null))
                {
                    try
                    {
                        var allowed = entry.Kind == "generated-resource"
                            ? generatedRoot
                            : entry.BackupPath == path
                                ? loaded.Manifest.BackupRoot
                                : sourceRoots.FirstOrDefault(root => SafePath.IsInside(path!, root))
                                  ?? (SafePath.IsInside(path!, generatedRoot) ? generatedRoot : null);
                        if (allowed is null)
                        {
                            errors.Add($"Manifest entry path is outside recorded roots: {path}");
                        }
                        else
                        {
                            var allowEqual = entry.Kind == "generated-resource" && SafePath.PathsEqual(path!, generatedRoot);
                            SafePath.RequireInside(path!, allowed, allowEqual);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
                    {
                        errors.Add($"Unsafe manifest entry path '{path}': {ex.Message}");
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Distinct(StringComparer.OrdinalIgnoreCase)));
        }
    }

    private static void ValidateRestorePreflight(LoadedBackupManifest loaded)
    {
        var errors = new List<string>();
        var entries = loaded.Manifest.Entries;
        var generatedEntries = entries.Where(entry => entry.Kind == "generated-resource" && entry.AppliedPath is not null).ToList();
        foreach (var entry in generatedEntries)
        {
            if (loaded.IsLegacy)
            {
                continue;
            }

            if (Directory.Exists(entry.AppliedPath) && !File.Exists(Path.Combine(entry.AppliedPath!, OwnershipMarkerFileName)))
            {
                errors.Add($"Refusing to remove unowned generated resource during restore: {entry.AppliedPath}");
            }
        }

        ValidateGeneratedFileInventory(loaded, generatedEntries, errors);

        foreach (var entry in entries.Where(entry => IsSourceBackupEntry(entry)))
        {
            var backupHash = entry.Sha256Before;
            if (entry.BackupPath is null || !File.Exists(entry.BackupPath))
            {
                errors.Add($"Backup file is missing for {entry.OriginalPath}: {entry.BackupPath}");
                continue;
            }

            if (!ComputeSha256(entry.BackupPath).Equals(backupHash, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Backup hash mismatch for {entry.OriginalPath}: {entry.BackupPath}");
                continue;
            }

            if (entry.AppliedPath is not null && entry.Kind == "resource-manifest")
            {
                if (!File.Exists(entry.AppliedPath))
                {
                    errors.Add($"Applied manifest is missing: {entry.AppliedPath}");
                }
                else if (entry.Sha256After is null || !ComputeSha256(entry.AppliedPath).Equals(entry.Sha256After, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Modified post-Apply file blocks restore: {entry.AppliedPath}");
                }

                continue;
            }

            if (File.Exists(entry.OriginalPath))
            {
                var currentHash = ComputeSha256(entry.OriginalPath);
                if (!currentHash.Equals(entry.Sha256Before, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Destination conflict blocks restore for {entry.OriginalPath} (modified post-Apply).");
                }
            }
        }

        foreach (var entry in entries.Where(entry => entry.Kind == "stream-rename"))
        {
            if (string.IsNullOrWhiteSpace(entry.Sha256After))
            {
                errors.Add(loaded.IsLegacy
                    ? $"Legacy stream restore requires a recorded applied hash: {entry.AppliedPath}"
                    : $"Planned stream mutation could not be reconciled safely: {entry.AppliedPath}");
                continue;
            }
            if (entry.AppliedPath is null)
            {
                errors.Add($"Stream rename entry has no applied path: {entry.OriginalPath}");
                continue;
            }

            if (!File.Exists(entry.AppliedPath))
            {
                if (!File.Exists(entry.OriginalPath) || !ComputeSha256(entry.OriginalPath).Equals(entry.Sha256Before, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Applied stream file is missing: {entry.AppliedPath}");
                }

                continue;
            }

            var appliedHash = ComputeSha256(entry.AppliedPath);
            if (entry.Sha256After is not null && !appliedHash.Equals(entry.Sha256After, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Modified post-Apply file blocks restore: {entry.AppliedPath}");
            }


            if (File.Exists(entry.OriginalPath))
            {
                errors.Add($"Destination conflict blocks stream restore: {entry.OriginalPath}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Distinct(StringComparer.OrdinalIgnoreCase)));
        }
    }
    private static void ValidateGeneratedFileInventory(
        LoadedBackupManifest loaded,
        IReadOnlyList<BackupEntry> generatedEntries,
        List<string> errors)
    {
        var fileEntries = loaded.Manifest.Entries
            .Where(entry => entry.Kind == "generated-file")
            .ToList();
        foreach (var entry in fileEntries)
        {
            if (entry.AppliedPath is null || !File.Exists(entry.AppliedPath))
            {
                errors.Add($"Generated post-Apply file is missing: {entry.AppliedPath}");
            }
            else if (entry.Sha256After is null
                     || !ComputeSha256(entry.AppliedPath).Equals(entry.Sha256After, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Modified post-Apply file blocks restore: {entry.AppliedPath}");
            }
        }

        if (!loaded.Manifest.Completed)
        {
            return;
        }

        foreach (var root in generatedEntries
                     .Select(entry => entry.AppliedPath!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                errors.Add($"Generated resource is missing: {root}");
                continue;
            }

            var expected = fileEntries
                .Where(entry => SafePath.PathsEqual(entry.OriginalPath, root) && entry.AppliedPath is not null)
                .Select(entry => entry.AppliedPath!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!expected.Contains(SafePath.Normalize(path)))
                {
                    errors.Add($"Unexpected post-Apply file blocks restore: {path}");
                }
            }
        }
    }

    private static void RecordGeneratedFileEntries(BackupManifest manifest, CancellationToken cancellationToken)
    {
        manifest.Entries.RemoveAll(entry => entry.Kind == "generated-file");
        var generatedRoots = manifest.Entries
            .Where(entry => entry.Kind == "generated-resource" && entry.AppliedPath is not null)
            .Select(entry => SafePath.Normalize(entry.AppliedPath!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var root in generatedRoots)
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Generated resource disappeared before journaling: {root}");
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Select(SafePath.Normalize)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                manifest.Entries.Add(new BackupEntry(
                    "generated-file",
                    root,
                    null,
                    path,
                    string.Empty,
                    ComputeSha256(path),
                    DateTimeOffset.UtcNow,
                    "applied"));
            }
        }
    }

    private static void ReconcilePlannedEntries(LoadedBackupManifest loaded)
    {
        foreach (var entry in loaded.Manifest.Entries.ToList())
        {
            if (!entry.State.Equals("planned", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Kind == "generated-resource")
            {
                var index = loaded.Manifest.Entries.IndexOf(entry);
                var markerPath = entry.AppliedPath is null
                    ? null
                    : Path.Combine(entry.AppliedPath, OwnershipMarkerFileName);
                if (markerPath is not null
                    && File.Exists(markerPath)
                    && entry.Sha256After is not null
                    && ComputeSha256(markerPath).Equals(entry.Sha256After, StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Manifest.Entries[index] = entry with { State = "applied" };
                }
                else
                {
                    loaded.Manifest.Entries.RemoveAt(index);
                }

                continue;
            }

            if (entry.Kind == "resource-manifest" && entry.AppliedPath is not null && File.Exists(entry.AppliedPath))
            {
                var currentHash = ComputeSha256(entry.AppliedPath);
                var index = loaded.Manifest.Entries.IndexOf(entry);
                if (entry.Sha256After is not null
                    && currentHash.Equals(entry.Sha256After, StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Manifest.Entries[index] = entry with { State = "applied" };
                }
                else if (currentHash.Equals(entry.Sha256Before, StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Manifest.Entries.RemoveAt(index);
                }

                continue;
            }

            if (entry.Kind == "stream-rename")
            {
                var index = loaded.Manifest.Entries.IndexOf(entry);
                if (entry.AppliedPath is not null && !File.Exists(entry.OriginalPath) && File.Exists(entry.AppliedPath))
                {
                    var afterHash = ComputeSha256(entry.AppliedPath);
                    if (afterHash.Equals(entry.Sha256Before, StringComparison.OrdinalIgnoreCase))
                    {
                        loaded.Manifest.Entries[index] = entry with { State = "applied", Sha256After = afterHash };
                    }
                }
                else if (File.Exists(entry.OriginalPath)
                         && (entry.AppliedPath is null || !File.Exists(entry.AppliedPath))
                         && ComputeSha256(entry.OriginalPath).Equals(entry.Sha256Before, StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Manifest.Entries.RemoveAt(index);
                }

                continue;
            }

            if (IsSourceBackupEntry(entry) && entry.Kind != "resource-manifest")
            {
                var index = loaded.Manifest.Entries.IndexOf(entry);
                if (!File.Exists(entry.OriginalPath) && entry.BackupPath is not null && File.Exists(entry.BackupPath))
                {
                    loaded.Manifest.Entries[index] = entry with { State = "applied" };
                }
                else if (File.Exists(entry.OriginalPath)
                         && ComputeSha256(entry.OriginalPath).Equals(entry.Sha256Before, StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Manifest.Entries.RemoveAt(index);
                }
            }
        }
    }

    private sealed record LoadedBackupManifest(string ManifestPath, BackupManifest Manifest, bool IsLegacy);

    public IReadOnlyList<string> ValidatePlan(MergePlan plan) => _planValidator.Validate(plan);

    private async Task<Dictionary<string, SourceYmt>> ReloadSourcesForPlanAsync(MergePlan plan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SourceYmt>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < plan.SourceYmts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = plan.SourceYmts[index];
            progress?.Report(new OperationProgress(
                "build",
                "load-source",
                index + 1,
                plan.SourceYmts.Count,
                source.Path,
                $"Loading source YMT {source.Path}.",
                SourceCount: index));

            try
            {
                var xml = await _codec.DecodeToXmlAsync(source.Path, cancellationToken);
                result[source.Path] = _reader.Read(xml, source.Path, source.Resource, Path.GetDirectoryName(source.Path) ?? source.Resource);
            }
            catch (Exception ex) when (IsContextWrappable(ex))
            {
                throw CreateContextException($"Failed to load source YMT '{source.Path}' for build", ex);
            }
        }

        return result;
    }

    private async Task<Dictionary<string, SourceCreatureMetadata>> ReloadCreatureMetadataForPlanAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SourceCreatureMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.SourceCreatureMetadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var xml = await _codec.DecodeToXmlAsync(source.Path, cancellationToken);
                var metadata = _creatureMetadataReader.Read(xml, source.Path, source.Resource, Path.GetDirectoryName(source.Path) ?? source.Resource);
                result[metadata.Path] = metadata;
            }
            catch (Exception ex) when (IsContextWrappable(ex))
            {
                throw CreateContextException($"Failed to load creature metadata '{source.Path}' for build", ex);
            }
        }

        return result;
    }

    private static Dictionary<string, XDocument> ReloadAlternateMetadataForPlan(MergePlan plan, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.SourceAlternateMetadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result[source.Path] = XDocument.Load(source.Path, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex) when (IsContextWrappable(ex))
            {
                throw CreateContextException($"Failed to load alternate metadata '{source.Path}' for build", ex);
            }
        }

        return result;
    }

    private static XDocument BuildCreatureMetadataXml(
        CreatureMetadataOutputPlan outputPlan,
        IReadOnlyDictionary<string, TargetCollectionPlan> targetPlansByCollection,
        Dictionary<string, SourceYmt> sources,
        Dictionary<string, SourceCreatureMetadata> creatureMetadataByPath,
        MappingIndex mappingIndex)
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
                var sourceDrawableMappings = mappingIndex.GetDrawableMappings(targetPlan.FullCollectionName, sourcePath);
                var sourcePropMappings = mappingIndex.GetPropMappings(targetPlan.FullCollectionName, sourcePath);

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
            if (targetPlan.ComponentCounts.Count == 0)
            {
                return new Dictionary<int, SourceIndexRange>();
            }

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
            if (targetPlan.PropCounts.Count == 0)
            {
                return new Dictionary<int, SourceIndexRange>();
            }

            return source.Props.ToDictionary(
                prop => prop.AnchorId,
                prop => new SourceIndexRange(source.YmtPath, prop.AnchorId, 0, prop.Props.Count));
        }

        return targetPlan.PropRanges
            .Where(range => range.SourceYmtPath.Equals(source.YmtPath, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(range => range.SlotId, range => range);
    }

    private static IReadOnlyDictionary<int, SourceIndexRange> GetComponentRanges(IEnumerable<SourceYmtContribution> contributions, SourceYmt source)
        => contributions
            .Where(contribution => ReferenceEquals(contribution.Source, source))
            .SelectMany(contribution => contribution.ComponentRanges.Values)
            .ToDictionary(range => range.SlotId, range => range);

    private static IReadOnlyDictionary<int, SourceIndexRange> GetPropRanges(IEnumerable<SourceYmtContribution> contributions, SourceYmt source)
        => contributions
            .Where(contribution => ReferenceEquals(contribution.Source, source))
            .SelectMany(contribution => contribution.PropRanges.Values)
            .ToDictionary(range => range.SlotId, range => range);


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
            var targetBindings = targetPlan.SourceYmts
                .Where(sourceBindingsByYmt.ContainsKey)
                .SelectMany(sourcePath => sourceBindingsByYmt[sourcePath])
                .DistinctBy(binding => (binding.SourceYmtPath.ToUpperInvariant(), binding.SourceMetadataPath.ToUpperInvariant()))
                .ToList();
            var hasRepairHints = targetPlan.SourceYmts
                .Select(sourcePath => sourceYmts.FirstOrDefault(source => source.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
                .Any(source => source?.HasCreatureRepairHints == true);
            if (targetBindings.Count == 0 && !hasRepairHints)
            {
                continue;
            }

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

    private sealed class MappingIndex
    {
        private readonly Dictionary<string, Dictionary<(int Slot, int Index), DrawableMapping>> _drawablesByTarget = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<(int Slot, int Index), PropMapping>> _propsByTarget = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, List<DrawableMapping>>> _drawablesByTargetSource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, List<PropMapping>>> _propsByTargetSource = new(StringComparer.OrdinalIgnoreCase);

        public static MappingIndex Create(
            IReadOnlyList<DrawableMapping> drawableMappings,
            IReadOnlyList<PropMapping> propMappings)
        {
            var index = new MappingIndex();
            foreach (var mapping in drawableMappings)
            {
                if (!index._drawablesByTarget.TryGetValue(mapping.TargetFullCollection, out var targetMappings))
                {
                    targetMappings = [];
                    index._drawablesByTarget.Add(mapping.TargetFullCollection, targetMappings);
                }

                targetMappings.Add((mapping.ComponentId, mapping.NewDrawableIndex), mapping);
                AddSource(index._drawablesByTargetSource, mapping.TargetFullCollection, mapping.SourceYmtPath, mapping);
            }

            foreach (var mapping in propMappings)
            {
                if (!index._propsByTarget.TryGetValue(mapping.TargetFullCollection, out var targetMappings))
                {
                    targetMappings = [];
                    index._propsByTarget.Add(mapping.TargetFullCollection, targetMappings);
                }

                targetMappings.Add((mapping.AnchorId, mapping.NewPropIndex), mapping);
                AddSource(index._propsByTargetSource, mapping.TargetFullCollection, mapping.SourceYmtPath, mapping);
            }

            return index;
        }

        public IReadOnlyList<DrawableMapping> GetDrawableMappings(string target, string source)
            => GetSourceMappings(_drawablesByTargetSource, target, source);

        public IReadOnlyList<PropMapping> GetPropMappings(string target, string source)
            => GetSourceMappings(_propsByTargetSource, target, source);

        public bool TryGetDrawable(string target, int slot, int index, out DrawableMapping mapping)
        {
            if (_drawablesByTarget.TryGetValue(target, out var mappings)
                && mappings.TryGetValue((slot, index), out var found))
            {
                mapping = found;
                return true;
            }

            mapping = null!;
            return false;
        }

        public bool TryGetProp(string target, int anchor, int index, out PropMapping mapping)
        {
            if (_propsByTarget.TryGetValue(target, out var mappings)
                && mappings.TryGetValue((anchor, index), out var found))
            {
                mapping = found;
                return true;
            }

            mapping = null!;
            return false;
        }

        private static IReadOnlyList<TMapping> GetSourceMappings<TMapping>(
            Dictionary<string, Dictionary<string, List<TMapping>>> index,
            string target,
            string source)
            => index.TryGetValue(target, out var bySource)
               && bySource.TryGetValue(source, out var mappings)
                ? mappings
                : Array.Empty<TMapping>();

        private static void AddSource<TMapping>(
            Dictionary<string, Dictionary<string, List<TMapping>>> index,
            string target,
            string source,
            TMapping mapping)
        {
            if (!index.TryGetValue(target, out var bySource))
            {
                bySource = new Dictionary<string, List<TMapping>>(StringComparer.OrdinalIgnoreCase);
                index.Add(target, bySource);
            }

            if (!bySource.TryGetValue(source, out var mappings))
            {
                mappings = [];
                bySource.Add(source, mappings);
            }

            mappings.Add(mapping);
        }
    }

    private sealed record PendingCreatureMetadataOutput(string Key)
    {
        public List<string> TargetCollections { get; } = [];
        public List<CreatureMetadataSourceBinding> SourceBindings { get; } = [];
    }

    private static bool IsLikelyPedVariationXml(string path)
        => path.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitYmtPath(string path)
        => path.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedClothingRoot(XDocument xml)
        => xml.Root?.Name.LocalName is "CPedVariationInfo" or "CCreatureMetaData";

    private async Task<XDocument> DecodeToXmlCachedAsync(
        string path,
        CancellationToken cancellationToken,
        Dictionary<string, Task<XDocument>> decodedDocuments)
    {
        if (!decodedDocuments.TryGetValue(path, out var decodeTask))
        {
            try
            {
                decodeTask = _codec.DecodeToXmlAsync(path, cancellationToken);
            }
            catch (Exception ex)
            {
                decodeTask = Task.FromException<XDocument>(ex);
            }

            decodedDocuments[path] = decodeTask;
        }

        return await decodeTask;
    }

    private async Task<IReadOnlyList<string>> FilterDuplicateXmlSidecarsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken,
        Dictionary<string, Task<XDocument>> decodedDocuments)
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
                var xml = await DecodeToXmlCachedAsync(path, cancellationToken, decodedDocuments);
                var ymtXml = await DecodeToXmlCachedAsync(ymtPath, cancellationToken, decodedDocuments);
                if (XmlDocumentsMatch(xml, ymtXml))
                {
                    skippedXmlPaths.Add(path);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep both files in the work list; explicit YMT paths report decode errors while unrelated XML is ignored.
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
        IReadOnlyList<string> metaFiles,
        CancellationToken cancellationToken)
    {
        var result = new List<SourceAlternateMetadata>();
        foreach (var path in metaFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    private sealed record ManifestSanitization(string Path, string OriginalText, string UpdatedText);

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

    private sealed class SourceShopMetadataIndex
    {
        private readonly Dictionary<SourceShopKey, SourceShopEntry> _components = [];
        private readonly Dictionary<SourceShopKey, SourceShopEntry> _props = [];

        public void Add(string resourceName, string path)
        {
            try
            {
                var xml = XDocument.Load(path);
                if (xml.Root?.Name.LocalName != "ShopPedApparel")
                {
                    return;
                }

                var fullDlcName = xml.Root.Element("fullDlcName")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(fullDlcName))
                {
                    fullDlcName = xml.Root.Element("dlcName")?.Value.Trim();
                }

                if (string.IsNullOrWhiteSpace(fullDlcName))
                {
                    return;
                }

                foreach (var item in xml.Root.Element("pedComponents")?.Elements("Item") ?? [])
                {
                    if (!TryReadComponentShopKey(item, out var componentId, out var drawableIndex, out var textureIndex))
                    {
                        continue;
                    }

                    _components.TryAdd(
                        CreateSourceShopKey(resourceName, fullDlcName, componentId, drawableIndex, textureIndex),
                        new SourceShopEntry(item, FindLeadingComment(item)));
                }

                foreach (var item in xml.Root.Element("pedProps")?.Elements("Item") ?? [])
                {
                    if (!TryReadPropShopKey(item, out var anchorId, out var propIndex, out var textureIndex))
                    {
                        continue;
                    }

                    _props.TryAdd(
                        CreateSourceShopKey(resourceName, fullDlcName, anchorId, propIndex, textureIndex),
                        new SourceShopEntry(item, FindLeadingComment(item)));
                }
            }
            catch
            {
                // Source resources often contain loose meta files with non-XML content; ignore anything that does not parse as ShopPedApparel.
            }
        }

        public bool TryGetComponent(string resourceName, string fullDlcName, int componentId, int drawableIndex, int textureIndex, out SourceShopEntry entry)
            => _components.TryGetValue(CreateSourceShopKey(resourceName, fullDlcName, componentId, drawableIndex, textureIndex), out entry!);

        public bool TryGetProp(string resourceName, string fullDlcName, int anchorId, int propIndex, int textureIndex, out SourceShopEntry entry)
            => _props.TryGetValue(CreateSourceShopKey(resourceName, fullDlcName, anchorId, propIndex, textureIndex), out entry!);
    }

    private sealed record SourceShopKey(string ResourceName, string FullDlcName, int SlotId, int LocalIndex, int TextureIndex);

    private sealed record SourceShopEntry(XElement Item, string? Comment);

    private enum SourceShopItemKind
    {
        Component,
        Prop,
    }

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

    private static SourceShopMetadataIndex LoadSourceShopMetadataIndex(MergePlan plan, CancellationToken cancellationToken)
    {
        var index = new SourceShopMetadataIndex();
        foreach (var fingerprint in plan.SourceFiles
                     .Where(item => item.Kind.Equals("shop-metadata", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index.Add(Path.GetFileName(SafePath.Normalize(fingerprint.ResourceRoot)), fingerprint.Path);
        }

        return index;
    }

    private static XDocument BuildShopMeta(
        TargetCollectionPlan plan,
        XDocument pedVariationXml,
        SourceShopMetadataIndex sourceShopMetadata,
        MappingIndex mappingIndex,
        string? creatureMetadataName = null)
        => new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("ShopPedApparel",
                new XElement("pedName", InferPedBaseName(plan.FullCollectionName)),
                new XElement("dlcName", plan.CollectionName),
                new XElement("fullDlcName", plan.FullCollectionName),
                new XElement("eCharacter", GetCharacterName(plan.Gender)),
                string.IsNullOrWhiteSpace(creatureMetadataName) ? null : new XElement("creatureMetaData", creatureMetadataName),
                new XElement("pedOutfits", new XAttribute("itemType", "ShopPedOutfit")),
                new XElement("pedComponents", new XAttribute("itemType", "ShopPedComponent"), BuildShopComponentItems(plan, pedVariationXml, sourceShopMetadata, mappingIndex)),
                new XElement("pedProps", new XAttribute("itemType", "ShopPedProp"), BuildShopPropItems(plan, pedVariationXml, sourceShopMetadata, mappingIndex))));

    private static IEnumerable<XNode> BuildShopComponentItems(
        TargetCollectionPlan plan,
        XDocument pedVariationXml,
        SourceShopMetadataIndex sourceShopMetadata,
        MappingIndex mappingIndex)
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
                    if (!mappingIndex.TryGetDrawable(plan.FullCollectionName, componentId, drawableIndex, out var mapping)
                        || !sourceShopMetadata.TryGetComponent(mapping.SourceResource, mapping.SourceFullCollection, componentId, mapping.OldDrawableIndex, textureIndex, out var sourceEntry))
                    {
                        continue;
                    }

                    var prefix = ClothingConstants.ComponentPrefixes.GetValueOrDefault(componentId, $"comp_{componentId}");
                    var uniqueName = $"{plan.FullCollectionName}_{prefix}_{drawableIndex:000}_{textureIndex:00}";
                    foreach (var node in BuildShopItemNodes(sourceEntry, BuildBaseShopItem(uniqueName, sourceEntry, SourceShopItemKind.Component,
                        new XElement("drawableIndex", new XAttribute("value", 0)),
                        new XElement("localDrawableIndex", new XAttribute("value", drawableIndex)),
                        new XElement("eCompType", ClothingConstants.ComponentTypeNames.GetValueOrDefault(componentId, $"PV_COMP_{componentId}")),
                        new XElement("textureIndex", new XAttribute("value", textureIndex)),
                        new XElement("isInOutfit", new XAttribute("value", "false")))))
                    {
                        yield return node;
                    }
                }
            }
        }
    }

    private static IEnumerable<XNode> BuildShopPropItems(
        TargetCollectionPlan plan,
        XDocument pedVariationXml,
        SourceShopMetadataIndex sourceShopMetadata,
        MappingIndex mappingIndex)
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
            var textureCount = Math.Max(1, XmlHelpers.Items(prop.Item.Element("texData")).Count);
            for (var textureIndex = 0; textureIndex < textureCount; textureIndex++)
            {
                var prefix = ClothingConstants.PropPrefixes.GetValueOrDefault(prop.AnchorId, $"prop_{prop.AnchorId}");
                var uniqueName = $"{plan.FullCollectionName}_{prefix}_{prop.PropId:000}_{textureIndex:00}";
                if (!mappingIndex.TryGetProp(plan.FullCollectionName, prop.AnchorId, prop.PropId, out var mapping)
                    || !sourceShopMetadata.TryGetProp(mapping.SourceResource, mapping.SourceFullCollection, prop.AnchorId, mapping.OldPropIndex, textureIndex, out var sourceEntry))
                {
                    continue;
                }

                foreach (var node in BuildShopItemNodes(sourceEntry, BuildBaseShopItem(uniqueName, sourceEntry, SourceShopItemKind.Prop,
                    new XElement("propIndex", new XAttribute("value", 0)),
                    new XElement("localPropIndex", new XAttribute("value", prop.PropId)),
                    new XElement("eAnchorPoint", ClothingConstants.AnchorNames.GetValueOrDefault(prop.AnchorId, $"ANCHOR_{prop.AnchorId}")),
                    new XElement("textureIndex", new XAttribute("value", textureIndex)),
                    new XElement("isInOutfit", new XAttribute("value", "false")))))
                {
                    yield return node;
                }
            }
        }
    }

    private static IEnumerable<XNode> BuildShopItemNodes(SourceShopEntry sourceEntry, XElement item)
    {
        if (!string.IsNullOrWhiteSpace(sourceEntry.Comment))
        {
            yield return new XComment(sourceEntry.Comment);
        }

        yield return item;
    }

    private static XElement BuildBaseShopItem(string uniqueName, SourceShopEntry sourceEntry, SourceShopItemKind kind, params object[] fields)
        => new("Item",
            new XElement("lockHash"),
            CloneOrDefault(sourceEntry.Item, "cost", new XElement("cost", new XAttribute("value", 0))),
            new XElement("textLabel"),
            new XElement("uniqueNameHash", uniqueName),
            CloneOrDefault(sourceEntry.Item, "eShopEnum", new XElement("eShopEnum", "CLO_SHOP_NONE")),
            CloneOrDefault(sourceEntry.Item, "locate", new XElement("locate", new XAttribute("value", -99))),
            CloneOrDefault(sourceEntry.Item, "scriptSaveData", new XElement("scriptSaveData", new XAttribute("value", 0))),
            CloneOrDefault(sourceEntry.Item, "restrictionTags", new XElement("restrictionTags")),
            CloneOrDefault(sourceEntry.Item, "forcedComponents", new XElement("forcedComponents")),
            kind == SourceShopItemKind.Prop
                ? CloneOrDefault(sourceEntry.Item, "forcedProps", new XElement("forcedProps"))
                : null,
            CloneOrDefault(sourceEntry.Item, "variantComponents", new XElement("variantComponents")),
            kind == SourceShopItemKind.Prop
                ? CloneOrDefault(sourceEntry.Item, "variantProps", new XElement("variantProps"))
                : null,
            fields);

    private static XElement CloneOrDefault(XElement sourceItem, string name, XElement fallback)
        => sourceItem.Element(name) is { } element
            ? new XElement(element)
            : fallback;

    private static string? FindLeadingComment(XElement item)
    {
        for (var node = item.PreviousNode; node is not null; node = node.PreviousNode)
        {
            if (node is XComment comment)
            {
                return comment.Value;
            }

            if (node is XText text && string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }

            break;
        }

        return null;
    }

    private static SourceShopKey CreateSourceShopKey(string resourceName, string fullDlcName, int slotId, int localIndex, int textureIndex)
        => new(
            resourceName.ToUpperInvariant(),
            fullDlcName.ToUpperInvariant(),
            slotId,
            localIndex,
            textureIndex);

    private static bool TryReadComponentShopKey(XElement item, out int componentId, out int drawableIndex, out int textureIndex)
    {
        componentId = -1;
        drawableIndex = -1;
        textureIndex = -1;

        var compType = item.Element("eCompType")?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(compType))
        {
            componentId = ClothingConstants.ComponentTypeNames
                .Where(pair => pair.Value.Equals(compType, StringComparison.OrdinalIgnoreCase))
                .Select(pair => (int?)pair.Key)
                .FirstOrDefault() ?? -1;
        }

        TryGetShopInt(item, "localDrawableIndex", out drawableIndex);
        TryGetShopInt(item, "textureIndex", out textureIndex);

        return componentId >= 0 && drawableIndex >= 0 && textureIndex >= 0;
    }

    private static bool TryReadPropShopKey(XElement item, out int anchorId, out int propIndex, out int textureIndex)
    {
        anchorId = -1;
        propIndex = -1;
        textureIndex = -1;

        var anchorName = item.Element("eAnchorPoint")?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(anchorName))
        {
            anchorId = ClothingConstants.AnchorNames
                .Where(pair => pair.Value.Equals(anchorName, StringComparison.OrdinalIgnoreCase))
                .Select(pair => (int?)pair.Key)
                .FirstOrDefault() ?? -1;
        }

        TryGetShopInt(item, "localPropIndex", out propIndex);
        TryGetShopInt(item, "textureIndex", out textureIndex);

        return anchorId >= 0 && propIndex >= 0 && textureIndex >= 0;
    }

    private static bool TryGetShopInt(XElement item, string name, out int value)
    {
        value = 0;
        var element = item.Element(name);
        if (element is null)
        {
            return false;
        }

        var text = element.Attribute("value")?.Value ?? element.Value;
        return XmlHelpers.TryParseIntValue(text, out value);
    }

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
        var dependencies = plan.ResourceRoots.Count > 0
            ? plan.ResourceRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.GetFileName(NormalizePath(root)))
                .Where(resource => !string.IsNullOrWhiteSpace(resource))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .ToList()
            : plan.SourceYmts
                .Select(source => source.Resource)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .ToList();
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

    private static string BuildValidationLua(MergePlan plan)
    {
        var orderedTargets = plan.TargetCollections.OrderBy(target => target.CollectionName).ToList();
        var collectionList = string.Join("," + Environment.NewLine, orderedTargets.Select(target => $"        '{target.CollectionName}'"));
        var expected = string.Join(Environment.NewLine, orderedTargets.Select(target =>
        {
            var componentEntries = string.Join(", ", target.ComponentCounts.OrderBy(item => item.Key).Select(item => $"[{item.Key}] = {item.Value}"));
            var propEntries = string.Join(", ", target.PropCounts.OrderBy(item => item.Key).Select(item => $"[{item.Key}] = {item.Value}"));
            return $"    ['{target.CollectionName}'] = {{ components = {{ {componentEntries} }}, props = {{ {propEntries} }} }},";
        }));

        return $$"""
RegisterCommand('clothing_repacker_validate', function()
    local ped = PlayerPedId()
    local checked = 0
    local failures = 0

    local collections = {
{{collectionList}}
    }

    local expected = {
{{expected}}
    }

    local function verifyCount(kind, index, actual, expectedCount)
        actual = actual or 0
        expectedCount = expectedCount or 0
        checked = checked + 1

        if actual == expectedCount then
            if actual > 0 then
                print(('  PASS %s %d -> %d'):format(kind, index, actual))
            end
            return

        end

        failures = failures + 1
        print(('  FAIL %s %d -> expected %d, got %d'):format(kind, index, expectedCount, actual))
    end

    for _, collection in ipairs(collections) do
        local collectionExpected = expected[collection]
        print(('Checking collection: %s'):format(collection))

        for comp = 0, 11 do
            local count = GetNumberOfPedCollectionDrawableVariations(ped, comp, collection)
            verifyCount('component', comp, count, collectionExpected.components[comp])
        end

        for anchor = 0, 12 do
            local count = GetNumberOfPedCollectionPropDrawableVariations(ped, anchor, collection)
            verifyCount('prop anchor', anchor, count, collectionExpected.props[anchor])
        end
    end


    if failures == 0 then
        print(('Clothing collection validation PASSED: %d checks matched expected counts.'):format(checked))
        return
    end

    print(('Clothing collection validation FAILED: %d of %d checks did not match expected counts.'):format(failures, checked))
end, false)
""";
    }

    private static void ValidateCurrentFingerprints(MergePlan plan)
    {
        var failures = new List<string>();
        foreach (var fingerprint in plan.SourceFiles)
        {
            try
            {
                var path = SafePath.RequireInside(fingerprint.Path, fingerprint.ResourceRoot, allowEqual: false);
                if (!File.Exists(path))
                {
                    failures.Add($"{path} (missing)");
                    continue;
                }

                var actual = ComputeSha256(path);
                if (!actual.Equals(fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{path} (changed)");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                failures.Add($"{fingerprint.Path} ({ex.Message})");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Source fingerprint preflight failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }
    }

    private static string CreateStagingRoot(string operation, string? parent = null)
    {
        var stagingParent = parent ?? Path.GetTempPath();
        Directory.CreateDirectory(stagingParent);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = Path.Combine(stagingParent, $"clothing-repacker-{operation}-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (IOException) when (attempt < 9)
            {
            }
        }

        throw new IOException($"Could not create a unique {operation} staging directory.");
    }

    private static void DeleteStagingRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private const string OwnershipMarkerFileName = ".clothing-repacker-owned";
    private static void WriteOwnershipMarker(string resourceRoot, string markerValue = "clothing-repacker")
    {
        Directory.CreateDirectory(resourceRoot);
        File.WriteAllText(Path.Combine(resourceRoot, OwnershipMarkerFileName), markerValue);
    }

    private static void EnsureOwnedDirectory(string resourceRoot)
    {
        if (!File.Exists(Path.Combine(resourceRoot, OwnershipMarkerFileName)))
        {
            throw new InvalidOperationException($"Refusing to replace unowned destination: {resourceRoot}");
        }
    }

    private static void ReplaceOwnedDirectory(string stagedRoot, string finalRoot)
    {
        if (Directory.Exists(finalRoot))
        {
            EnsureOwnedDirectory(finalRoot);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalRoot)!);
        var displacedRoot = $"{finalRoot}.old-{Guid.NewGuid():N}";
        var displaced = false;
        try
        {
            if (Directory.Exists(finalRoot))
            {
                Directory.Move(finalRoot, displacedRoot);
                displaced = true;
            }

            Directory.Move(stagedRoot, finalRoot);
            if (displaced)
            {
                Directory.Delete(displacedRoot, recursive: true);
            }
        }
        catch
        {
            if (!Directory.Exists(finalRoot) && displaced && Directory.Exists(displacedRoot))
            {
                Directory.Move(displacedRoot, finalRoot);
            }

            throw;
        }
    }

    private static List<SourceFileFingerprint> BuildSourceFingerprints(
        IReadOnlyList<ResourceScanItem> scanItems,
        IReadOnlyList<SourceYmt> sources,
        IReadOnlyList<SourceCreatureMetadata> creatureMetadata,
        IReadOnlyList<SourceCreatureMetadata> brokenCreatureMetadata,
        IReadOnlyList<SourceAlternateMetadata> alternateMetadata,
        IReadOnlyList<StreamRename> streamRenames,
        List<string> errors)
    {
        var fingerprints = new Dictionary<string, SourceFileFingerprint>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string resourceRoot, string kind)
        {
            if (fingerprints.ContainsKey(path))
            {
                return;
            }

            try
            {
                fingerprints[path] = new SourceFileFingerprint(
                    SafePath.Normalize(path),
                    SafePath.Normalize(resourceRoot),
                    kind,
                    ComputeSha256(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{path}: could not fingerprint source file: {ex.Message}");
            }
        }

        foreach (var source in sources)
        {
            Add(source.YmtPath, source.ResourceRoot, "ymt");
        }

        foreach (var metadata in creatureMetadata.Concat(brokenCreatureMetadata))
        {
            Add(metadata.Path, metadata.ResourceRoot, "creature-metadata");
        }

        foreach (var metadata in alternateMetadata)
        {
            Add(metadata.Path, metadata.ResourceRoot, "alternate-metadata");
        }

        foreach (var item in scanItems)
        {
            foreach (var path in item.YmtFiles)
            {
                Add(path, item.ResourceRoot, "ymt");
            }
        }

        foreach (var item in scanItems)
        {
            foreach (var path in item.ShopMetaFiles)
            {
                Add(path, item.ResourceRoot, "shop-metadata");
            }

            if (item.ManifestPath is not null)
            {
                Add(item.ManifestPath, item.ResourceRoot, "resource-manifest");
            }
        }

        foreach (var rename in streamRenames)
        {
            if (scanItems.FirstOrDefault(item => item.ResourceName.Equals(rename.SourceResource, StringComparison.OrdinalIgnoreCase)) is { } item)
            {
                Add(rename.SourcePath, item.ResourceRoot, "stream");
            }
        }

        return fingerprints.Values
            .OrderBy(fingerprint => fingerprint.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static string ComputeSha256Utf8(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void CopyDirectory(
        string source,
        string destination,
        IProgress<OperationProgress>? progress = null,
        string operation = "copy",
        string stage = "copy-file",
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? renameMap = null)
    {
        var fullSource = NormalizePath(source);
        var fullDestination = NormalizePath(destination);
        Directory.CreateDirectory(fullDestination);
        foreach (var directory in Directory.GetDirectories(fullSource, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(fullDestination, Path.GetRelativePath(fullSource, directory)));
        }

        var files = Directory.GetFiles(fullSource, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var relativePath = Path.GetRelativePath(fullSource, file);
            var destinationFile = renameMap != null && renameMap.TryGetValue(relativePath, out var renamedRelative)
                ? Path.Combine(fullDestination, renamedRelative)
                : Path.Combine(fullDestination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
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

    private sealed class WorkflowContextException : InvalidOperationException
    {
        public WorkflowContextException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private enum GeneratedResourcesRootUsage
    {
        GeneratedOnly,
        CopySourceResources,
    }

    private sealed class ResourcePathMap
    {
        private readonly IReadOnlyList<ResourceRootMapping> _mappings;

        public ResourcePathMap(IReadOnlyList<ResourceRootMapping> mappings)
        {
            _mappings = mappings
                .OrderByDescending(mapping => NormalizePath(mapping.SourceRoot).Length)
                .ToList();
        }

        public bool HasMappings => _mappings.Count > 0;

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
