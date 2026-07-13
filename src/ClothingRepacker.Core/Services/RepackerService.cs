using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Hashing;
using ClothingRepacker.Core.Localization;
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
        var warningDiagnostics = new List<LocalizedDiagnostic>();
        var errorDiagnostics = new List<LocalizedDiagnostic>();
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
            MessageKey: "progress.foundCandidates",
            MessageArguments: new Dictionary<string, object?> { ["resources"] = scanItems.Count, ["candidates"] = workItems.Count, ["files"] = streamFiles.Count }));

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
                        AddDiagnostic(warnings, warningDiagnostics, "diagnostic.metadataUnreferenced", new Dictionary<string, object?> { ["path"] = path },
                            $"{path}: Creature metadata has no corresponding ShopPedApparel creatureMetaData reference and will be backed up without being merged.");
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
                foreach (var message in source.Messages)
                {
                    if (message.Severity is not (ValidationSeverity.Warning or ValidationSeverity.Error))
                    {
                        continue;
                    }

                    var fallback = $"{path}: {message.Message}";
                    var arguments = new Dictionary<string, object?>(message.Arguments ?? new Dictionary<string, object?>())
                    {
                        ["path"] = path,
                    };
                    var diagnostics = message.Severity == ValidationSeverity.Warning ? warningDiagnostics : errorDiagnostics;
                    var messages = message.Severity == ValidationSeverity.Warning ? warnings : errors;
                    AddDiagnostic(messages, diagnostics, message.Code, arguments, fallback);
                }
                sources.Add(source);
            }
            catch (Exception ex)
            {
                AddDiagnostic(errors, errorDiagnostics, "legacy", new Dictionary<string, object?>(), $"{path}: {ex.Message}");
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
            AddDiagnostic(warnings, warningDiagnostics, "diagnostic.missingCreatureMetadata", new Dictionary<string, object?>
            {
                ["path"] = reference.ShopMetaPath,
                ["reference"] = reference.Reference,
                ["resource"] = reference.Resource,
            }, $"{reference.ShopMetaPath}: ShopPedApparel creatureMetaData references missing creature metadata '{reference.Reference}' and generated creature metadata will be omitted for merged targets from resource '{reference.Resource}'.");
        }

        progress?.Report(new OperationProgress(
            "analyze",
            "plan-targets",
            workItems.Count,
            workItems.Count,
            MessageKey: "progress.planningTargets",
            SourceCount: sources.Count,
            WarningCount: warnings.Count,
            ErrorCount: errors.Count));

        var mergeableSources = sources.Where(IsMergeableFreemodeSource).ToList();
        foreach (var source in sources.Except(mergeableSources))
        {
            AddDiagnostic(warnings, warningDiagnostics, "diagnostic.nonFreemodeSkipped", new Dictionary<string, object?> { ["path"] = source.YmtPath },
                $"{source.YmtPath}: Non-freemode YMT skipped. It will only be copied to the generated resources root when copy-before-rename apply mode is enabled.");
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
            MessageKey: "progress.finalizing",
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
        var sourceAlternateMetadataBackups = alternateMetadata.Select(metadata => new SourceAlternateMetadataBackupPlan(
            metadata.Path,
            Path.Combine(metadata.ResourceName, Path.GetRelativePath(metadata.ResourceRoot, metadata.Path)).Replace(Path.DirectorySeparatorChar, '/'))).ToList();

        progress?.Report(new OperationProgress(
            "analyze",
            "complete",
            workItems.Count,
            workItems.Count,
            MessageKey: "progress.analyzeComplete",
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
            SourceAlternateMetadataBackups = sourceAlternateMetadataBackups,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            WarningDiagnostics = CompleteDiagnostics(warningDiagnostics, warnings),
            ErrorDiagnostics = CompleteDiagnostics(errorDiagnostics, errors),
        };

        return new AnalyzeResult(plan, sources, streamFiles, creatureMetadata);
    }

    private static void AddDiagnostic(
        ICollection<string> legacy,
        ICollection<LocalizedDiagnostic> diagnostics,
        string code,
        IReadOnlyDictionary<string, object?> arguments,
        string fallback)
    {
        legacy.Add(fallback);
        diagnostics.Add(new LocalizedDiagnostic(code, arguments, fallback));
    }

    private static List<LocalizedDiagnostic> CompleteDiagnostics(
        IReadOnlyList<LocalizedDiagnostic> diagnostics,
        IReadOnlyList<string> legacy)
        => diagnostics
            .Concat(legacy
                .Where(text => !diagnostics.Any(diagnostic => diagnostic.FallbackText.Equals(text, StringComparison.OrdinalIgnoreCase)))
                .Select(LocalizedDiagnostic.Legacy))
            .ToList();

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
        var fullOutputRoot = Path.GetFullPath(outputRoot);
        ValidateGeneratedResourcesRoot(GetKnownResourceRoots(plan), fullOutputRoot, GeneratedResourcesRootUsage.GeneratedOnly);

        var sources = await ReloadSourcesForPlanAsync(plan, progress, cancellationToken);
        var creatureMetadataByPath = await ReloadCreatureMetadataForPlanAsync(plan, cancellationToken);
        var alternateMetadataByPath = ReloadAlternateMetadataForPlan(plan, cancellationToken);
        var sourceShopMetadata = LoadSourceShopMetadataIndex(plan, cancellationToken);
        var creatureMetadataOutputs = GetCreatureMetadataOutputPlans(plan);
        var creatureMetadataOutputByTarget = creatureMetadataOutputs
            .SelectMany(output => output.TargetCollections.Select(collection => new { collection, output }))
            .ToDictionary(item => item.collection, item => item.output, StringComparer.OrdinalIgnoreCase);
        var writtenFiles = new List<string>();
        Directory.CreateDirectory(fullOutputRoot);

        progress?.Report(new OperationProgress(
            "build",
            "start",
            Total: plan.TargetCollections.Count,
            MessageKey: "progress.loadedSources",
            MessageArguments: new Dictionary<string, object?> { ["sources"] = sources.Count, ["targets"] = plan.TargetCollections.Count },
            SourceCount: sources.Count));

        for (var index = 0; index < plan.TargetCollections.Count; index++)
        {
            var targetPlan = plan.TargetCollections[index];
            var relativeYmtPath = targetPlan.OutputYmtPath.Replace('/', Path.DirectorySeparatorChar);
            var ymtOutputPath = Path.Combine(fullOutputRoot, relativeYmtPath);
            progress?.Report(new OperationProgress(
                "build",
                "build-target",
                index + 1,
                plan.TargetCollections.Count,
                ymtOutputPath,
                MessageKey: "progress.buildingTargetNamed",
                MessageArguments: new Dictionary<string, object?> { ["name"] = targetPlan.FullCollectionName },
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
                    cancellationToken);
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
                BuildShopMeta(targetPlan, xml, sourceShopMetadata, plan.DrawableMappings, plan.PropMappings, creatureMetadataOutput?.Name).Save(metaPath);
                writtenFiles.Add(metaPath);
            }
            catch (Exception ex) when (IsContextWrappable(ex))
            {
                throw CreateContextException(
                    $"Failed while building target collection '{targetPlan.FullCollectionName}' for output '{ymtOutputPath}'",
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
            var creatureMetadataOutputPath = Path.Combine(fullOutputRoot, creatureMetadataOutput.OutputYmtPath.Replace('/', Path.DirectorySeparatorChar));
            progress?.Report(new OperationProgress(
                "build",
                "build-creature-metadata",
                index + 1,
                creatureMetadataOutputs.Count,
                creatureMetadataOutputPath,
                MessageKey: "progress.buildingCreatureMetadataNamed",
                MessageArguments: new Dictionary<string, object?> { ["name"] = creatureMetadataOutput.Name },
                SourceCount: sources.Count,
                TargetCount: plan.TargetCollections.Count,
                WrittenFileCount: writtenFiles.Count));

            try
            {
                var creatureMetadataXml = BuildCreatureMetadataXml(plan, creatureMetadataOutput, targetPlansByCollection, sources, creatureMetadataByPath);
                Directory.CreateDirectory(Path.GetDirectoryName(creatureMetadataOutputPath)!);
                await EncodeYmtWithDiagnosticsAsync(
                    creatureMetadataXml,
                    creatureMetadataOutputPath,
                    $"Failed to encode creature metadata '{creatureMetadataOutput.Name}'",
                    cancellationToken);
                writtenFiles.Add(creatureMetadataOutputPath);

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
            var outputPath = Path.Combine(fullOutputRoot, alternateMetadataOutput.OutputPath.Replace('/', Path.DirectorySeparatorChar));
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

        progress?.Report(new OperationProgress(
            "build",
            "complete",
            plan.TargetCollections.Count,
            plan.TargetCollections.Count,
            MessageKey: "progress.buildComplete",
            SourceCount: sources.Count,
            TargetCount: plan.TargetCollections.Count,
            WrittenFileCount: writtenFiles.Count));

        return new BuildResult(fullOutputRoot, writtenFiles);
    }

    private async Task EncodeYmtWithDiagnosticsAsync(XDocument xml, string outputYmtPath, string context, CancellationToken cancellationToken)
    {
        try
        {
            await _codec.EncodeFromXmlAsync(xml, outputYmtPath, cancellationToken);
        }
        catch (Exception ex) when (IsContextWrappable(ex))
        {
            var diagnosticXmlPath = TrySaveFailedXml(xml, outputYmtPath);
            var diagnosticMessage = diagnosticXmlPath is null
                ? string.Empty
                : $" Diagnostic XML was written to '{diagnosticXmlPath}'.";

            throw CreateContextException($"{context}. Output YMT: '{outputYmtPath}'.{diagnosticMessage}", ex);
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
            MessageKey: "progress.foundYmtFiles",
            MessageArguments: new Dictionary<string, object?> { ["count"] = ymtFiles.Count }));

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
            MessageKey: "progress.xmlExportComplete",
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
        var generatedResourcesRoot = GetGeneratedResourcesRoot(plan);
        var sourceAlternateMetadataBackups = GetSourceAlternateMetadataBackupPlans(plan);
        var sourceBackupPlanCount = mergedSourceYmtPaths.Count + plan.BrokenCreatureMetadataBackups.Count + sourceAlternateMetadataBackups.Count;
        ValidateGeneratedResourcesRoot(
            GetKnownResourceRoots(plan),
            generatedResourcesRoot,
            options.CopyResourcesToOutputBeforeRename
                ? GeneratedResourcesRootUsage.CopySourceResources
                : GeneratedResourcesRootUsage.GeneratedOnly);

        progress?.Report(new OperationProgress(
            "apply",
            "start",
            Total: resourceRootsToCopy.Count + plan.StreamRenames.Count + sourceBackupPlanCount,
            MessageKey: options.CopyResourcesToOutputBeforeRename
                ? "progress.preparingCopyApply"
                : "progress.preparingApply",
            MessageArguments: new Dictionary<string, object?>
            {
                ["resources"] = resourceRootsToCopy.Count,
                ["renames"] = plan.StreamRenames.Count,
                ["backups"] = sourceBackupPlanCount,
            }));

        var validationErrors = _planValidator.Validate(plan);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        }

        var runId = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHHmmssZ");
        var backupDir = Path.Combine(Path.GetFullPath(backupRoot), runId);
        Directory.CreateDirectory(backupDir);
        var manifestPath = Path.Combine(backupDir, "backup-manifest.json");
        var entries = new List<BackupEntry>();
        await WriteBackupManifestAsync(manifestPath, entries, cancellationToken);

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"clothing-repacker-{Guid.NewGuid():N}");
        progress?.Report(new OperationProgress(
            "apply",
            "build-staging",
            MessageKey: "progress.buildingStaging"));
        var buildResult = await BuildAsync(plan, stagingRoot, new BuildOptions
        {
            IncludeYmtXml = options.IncludeYmtXml,
            IncludeDebugClient = options.IncludeDebugClient,
        }, progress, cancellationToken);
        var renameMap = options.CopyResourcesToOutputBeforeRename
            ? plan.StreamRenames.ToDictionary(rename => rename.SourcePath, rename => rename.TargetPath, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathMap = options.CopyResourcesToOutputBeforeRename
            ? CopySourceResourcesToOutput(resourceRootsToCopy, generatedResourcesRoot, entries, progress, cancellationToken, renameMap)
            : new ResourcePathMap([]);

        if (options.CopyResourcesToOutputBeforeRename)
        {
            foreach (var sourceRoot in resourceRootsToCopy)
            {
                var copiedRoot = GetResourceCopyDestination(sourceRoot, generatedResourcesRoot);
                SanitizeResourceManifest(copiedRoot, entries: null, backupManifestRoot: null);
            }
        }
        else
        {
            foreach (var sourceRoot in GetKnownResourceRoots(plan))
            {
                SanitizeResourceManifest(sourceRoot, entries, backupDir);
                await WriteBackupManifestAsync(manifestPath, entries, cancellationToken);
            }
        }

        for (var index = 0; index < plan.StreamRenames.Count; index++)
        {
            var rename = plan.StreamRenames[index];
            var sourcePath = pathMap.Map(rename.SourcePath);
            var targetPath = pathMap.Map(rename.TargetPath);

            if (options.CopyResourcesToOutputBeforeRename && File.Exists(targetPath))
            {
                var copyBeforeHash = ComputeSha256(targetPath);
                entries.Add(new BackupEntry("stream-rename", sourcePath, null, targetPath, copyBeforeHash, ComputeSha256(targetPath), DateTimeOffset.UtcNow));

                progress?.Report(new OperationProgress(
                    "apply",
                    "rename-stream",
                    index + 1,
                    plan.StreamRenames.Count,
                    targetPath,
                    RenameCount: index + 1,
                    BackupCount: entries.Count(IsSourceBackupEntry)));
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Source file missing at apply time: {sourcePath}");
            }

            var beforeHash = ComputeSha256(sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var renameEntry = new BackupEntry("stream-rename", sourcePath, null, targetPath, beforeHash, null, DateTimeOffset.UtcNow);
            entries.Add(renameEntry);
            await WriteBackupManifestAsync(manifestPath, entries, cancellationToken);
            File.Move(sourcePath, targetPath);
            entries[^1] = renameEntry with { Sha256After = ComputeSha256(targetPath) };
            await WriteBackupManifestAsync(manifestPath, entries, cancellationToken);

            progress?.Report(new OperationProgress(
                "apply",
                "rename-stream",
                index + 1,
                plan.StreamRenames.Count,
                targetPath,
                RenameCount: index + 1,
                BackupCount: entries.Count(IsSourceBackupEntry)));
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

            if (pathMap.HasMappings)
            {
                File.Delete(sourcePath);
            }
            else
            {
                var backupPath = Path.Combine(backupDir, source.Resource, Path.GetFileName(source.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(sourcePath, backupPath, overwrite: true);
                var beforeHash = ComputeSha256(sourcePath);
                entries.Add(new BackupEntry("old-ymt", sourcePath, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow));
                await WriteBackupManifestAsync(manifestPath, entries, cancellationToken);
                File.Delete(sourcePath);
            }

            progress?.Report(new OperationProgress(
                "apply",
                pathMap.HasMappings ? "remove-source-ymt" : "backup-source-ymt",
                index + 1,
                mergedSources.Count,
                sourcePath,
                RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
                BackupCount: entries.Count(IsSourceBackupEntry),
                RemovedCount: pathMap.HasMappings ? index + 1 : 0));
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
            entries.Add(new BackupEntry("broken-creature-metadata", sourcePath, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow));
            await WriteBackupManifestAsync(manifestPath, entries, cancellationToken);
            File.Delete(sourcePath);

            progress?.Report(new OperationProgress(
                "apply",
                "backup-source-ymt",
                mergedSources.Count + index + 1,
                mergedSources.Count + plan.BrokenCreatureMetadataBackups.Count,
                sourcePath,
                RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
                BackupCount: entries.Count(IsSourceBackupEntry)));
        }

        for (var index = 0; index < sourceAlternateMetadataBackups.Count; index++)
        {
            var source = sourceAlternateMetadataBackups[index];
            var sourcePath = pathMap.Map(source.SourcePath);
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
                var backupPath = Path.Combine(backupDir, source.BackupPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(sourcePath, backupPath, overwrite: true);
                var beforeHash = ComputeSha256(sourcePath);
                entries.Add(new BackupEntry("source-alternate-metadata", sourcePath, backupPath, null, beforeHash, ComputeSha256(backupPath), DateTimeOffset.UtcNow));
                await WriteBackupManifestAsync(manifestPath, entries, cancellationToken);
                File.Delete(sourcePath);
            }

            var metadataIndex = mergedSources.Count + plan.BrokenCreatureMetadataBackups.Count + index + 1;
            progress?.Report(new OperationProgress(
                "apply",
                pathMap.HasMappings ? "remove-source-metadata" : "backup-source-metadata",
                metadataIndex,
                sourceBackupPlanCount,
                sourcePath,
                RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
                BackupCount: entries.Count(IsSourceBackupEntry),
                RemovedCount: pathMap.HasMappings ? metadataIndex : 0));
        }

        if (plan.TargetCollections.Count > 0)
        {
            var generatedRoot = Path.Combine(generatedResourcesRoot, plan.TargetResource);
            var generatedRootIsCopiedSourceResource = resourceRootsToCopy.Any(resourceRoot =>
                PathsEqual(GetResourceCopyDestination(resourceRoot, generatedResourcesRoot), generatedRoot));
            entries.Add(new BackupEntry("generated-resource", generatedRoot, null, generatedRoot, string.Empty, null, DateTimeOffset.UtcNow));
            await WriteBackupManifestAsync(manifestPath, entries, cancellationToken);
            if (Directory.Exists(generatedRoot))
            {
                if (generatedRootIsCopiedSourceResource)
                {
                    RemoveOverlayArtifacts(generatedRoot);
                }
                else
                {
                    Directory.Delete(generatedRoot, recursive: true);
                }
            }

            CopyDirectory(
                Path.Combine(buildResult.OutputRoot, plan.TargetResource),
                generatedRoot,
                progress,
                "apply",
                "copy-generated-file",
                cancellationToken);
        }

        progress?.Report(new OperationProgress(
            "apply",
            "copy-generated-resource",
            MessageKey: plan.TargetCollections.Count > 0
                ? "progress.copiedGeneratedResource"
                : "progress.noMergedResource",
            MessageArguments: new Dictionary<string, object?> { ["path"] = Path.Combine(generatedResourcesRoot, plan.TargetResource) },
            RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
            BackupCount: entries.Count(IsSourceBackupEntry),
            WrittenFileCount: buildResult.WrittenFiles.Count));

        progress?.Report(new OperationProgress(
            "apply",
            "complete",
            plan.StreamRenames.Count + sourceBackupPlanCount,
            plan.StreamRenames.Count + sourceBackupPlanCount,
            MessageKey: "progress.applyComplete",
            MessageArguments: new Dictionary<string, object?> { ["path"] = manifestPath },
            RenameCount: entries.Count(entry => entry.Kind == "stream-rename"),
            BackupCount: entries.Count(IsSourceBackupEntry),
            WrittenFileCount: buildResult.WrittenFiles.Count));

        return entries;
    }

    private async Task WriteBackupManifestAsync(
        string manifestPath,
        IReadOnlyList<BackupEntry> entries,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(entries, _jsonOptions),
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
        => !string.IsNullOrWhiteSpace(plan.GeneratedResourcesRoot)
            ? Path.GetFullPath(plan.GeneratedResourcesRoot)
            : Path.GetDirectoryName(plan.ResourcesRoot) ?? plan.ResourcesRoot;

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
    {
        if (plan.SourceAlternateMetadataBackups.Count > 0)
        {
            return plan.SourceAlternateMetadataBackups;
        }

        return plan.SourceAlternateMetadata
            .Select(source => new SourceAlternateMetadataBackupPlan(
                source.Path,
                Path.Combine(source.Resource, Path.GetFileName(source.Path)).Replace(Path.DirectorySeparatorChar, '/')))
            .DistinctBy(source => source.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSourceBackupEntry(BackupEntry entry)
        => entry.Kind is "old-ymt" or "broken-creature-metadata" or "source-alternate-metadata" or "resource-manifest";

    private static ResourcePathMap CopySourceResourcesToOutput(
        IReadOnlyList<string> resourceRoots,
        string generatedResourcesRoot,
        List<BackupEntry> entries,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? renameMap = null)
    {
        var mappings = new List<ResourceRootMapping>();
        for (var index = 0; index < resourceRoots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRoot = NormalizePath(resourceRoots[index]);
            var destinationRoot = GetResourceCopyDestination(sourceRoot, generatedResourcesRoot);
            ValidateResourceCopyDestination(sourceRoot, destinationRoot);

            progress?.Report(new OperationProgress(
                "apply",
                "copy-source-resource",
                index,
                resourceRoots.Count,
                sourceRoot,
                $"Copying source resource {index + 1}/{resourceRoots.Count}: {Path.GetFileName(NormalizePath(sourceRoot))}."));

            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, recursive: true);
            }

            CopyDirectory(sourceRoot, destinationRoot, progress, "apply", "copy-source-file", cancellationToken, renameMap);
            mappings.Add(new ResourceRootMapping(sourceRoot, destinationRoot));
            entries.Add(new BackupEntry("generated-resource", destinationRoot, null, destinationRoot, string.Empty, null, DateTimeOffset.UtcNow));

            progress?.Report(new OperationProgress(
                "apply",
                "copy-source-resource",
                index + 1,
                resourceRoots.Count,
                destinationRoot,
                BackupCount: entries.Count(IsSourceBackupEntry)));
        }

        return new ResourcePathMap(mappings);
    }

    private static void SanitizeResourceManifest(string resourceRoot, List<BackupEntry>? entries = null, string? backupManifestRoot = null)
    {
        if (string.IsNullOrWhiteSpace(resourceRoot) || !Directory.Exists(resourceRoot))
        {
            return;
        }

        var manifestPath = FindResourceManifestPath(resourceRoot);
        if (manifestPath is null)
        {
            return;
        }

        var originalText = File.ReadAllText(manifestPath);
        var updatedText = SanitizeManifestText(originalText);
        if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
        {
            return;
        }

        var backupPath = backupManifestRoot is null
            ? null
            : Path.Combine(backupManifestRoot, Path.GetFileName(resourceRoot), Path.GetFileName(manifestPath));
        if (backupPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(manifestPath, backupPath, overwrite: true);
        }

        File.WriteAllText(manifestPath, updatedText);
        if (entries is not null && backupPath is not null)
        {
            entries.Add(new BackupEntry(
                "resource-manifest",
                manifestPath,
                backupPath,
                manifestPath,
                ComputeSha256(manifestPath),
                ComputeSha256(manifestPath),
                DateTimeOffset.UtcNow));
        }
    }

    private static string? FindResourceManifestPath(string resourceRoot)
    {
        foreach (var manifestName in new[] { "fxmanifest.lua", "__resource.lua" })
        {
            var path = Path.Combine(resourceRoot, manifestName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string SanitizeManifestText(string text)
    {
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var sanitizedLines = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (IsManifestDataFileLine(line) || IsManifestFilesEntryLine(line))
            {
                continue;
            }

            sanitizedLines.Add(line);
        }

        return string.Join(Environment.NewLine, sanitizedLines);
    }

    private static bool IsManifestDataFileLine(string line)
        => Regex.IsMatch(line, @"^\s*data_file\s+'(?:SHOP_PED_APPAREL_META_FILE|ALTERNATE_VARIATIONS_FILE|PED_FIRST_PERSON_ALTERNATE_DATA)'", RegexOptions.IgnoreCase);

    private static bool IsManifestFilesEntryLine(string line)
    {
        var match = Regex.Match(line, @"^\s*'([^']+)'\s*,?\s*$");
        if (!match.Success)
        {
            return false;
        }

        var value = match.Groups[1].Value;
        return value.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            && !value.Contains('*')
            && !value.Contains('?');
    }

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
        => Path.Combine(Path.GetFullPath(generatedResourcesRoot), Path.GetFileName(NormalizePath(sourceRoot)));

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
            MessageKey: "progress.preparingRestore",
            MessageArguments: new Dictionary<string, object?> { ["count"] = actions.Count, ["path"] = preview.ManifestPath }));

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
                MessageKey: "progress.removedGeneratedResourceNamed",
                MessageArguments: new Dictionary<string, object?> { ["path"] = action.DestinationPath }));
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
                MessageKey: "progress.restoredFile",
                MessageArguments: new Dictionary<string, object?> { ["destination"] = action.DestinationPath, ["source"] = action.SourcePath }));
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
                MessageKey: "progress.movedFile",
                MessageArguments: new Dictionary<string, object?> { ["source"] = action.SourcePath, ["destination"] = action.DestinationPath }));
        }

        progress?.Report(new OperationProgress(
            "restore",
            "complete",
            actions.Count,
            actions.Count,
            MessageKey: "progress.restoreComplete",
            MessageArguments: new Dictionary<string, object?> { ["count"] = completed }));
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

        foreach (var entry in entries.Where(IsSourceBackupEntry))
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
            progress?.Report(new OperationProgress(
                "build",
                "load-source",
                index + 1,
                plan.SourceYmts.Count,
                source.Path,
                MessageKey: "progress.loadingSourceNamed",
                MessageArguments: new Dictionary<string, object?> { ["path"] = source.Path },
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

    private static IReadOnlyList<CreatureMetadataOutputPlan> GetCreatureMetadataOutputPlans(MergePlan plan)
    {
        if (plan.CreatureMetadataOutputs.Count > 0)
        {
            return plan.CreatureMetadataOutputs;
        }

        return plan.TargetCollections
            .Where(target => !TargetHasUnavailableCreatureMetadata(plan, target))
            .Where(target => HasCreatureMetadataContent(plan, target))
            .Select(target => new CreatureMetadataOutputPlan(
                $"MP_CreatureMetadata_{target.CollectionName}",
                Path.Combine(plan.TargetResource, "stream", $"MP_CreatureMetadata_{target.CollectionName}.ymt").Replace(Path.DirectorySeparatorChar, '/'),
                [target.CollectionName],
                BuildLegacyCreatureMetadataBindings(plan, target)))
            .ToList();
    }

    private static bool HasCreatureMetadataContent(MergePlan plan, TargetCollectionPlan targetPlan)
    {
        var sourcePaths = targetPlan.SourceYmts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.SourceCreatureMetadata.Any(metadata =>
                   metadata.SourceYmts.Any(sourcePath => sourcePaths.Contains(sourcePath)))
               || plan.SourceYmts.Any(source =>
                   sourcePaths.Contains(source.Path) && source.HasCreatureRepairHints);
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
        var sourceResources = plan.SourceYmts
            .Select(source => source.Resource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resourceRoots = GetKnownResourceRoots(plan)
            .Where(Directory.Exists)
            .ToList();
        if (resourceRoots.Count == 0)
        {
            return index;
        }

        foreach (var resource in new ResourceScanner().ScanResourceFolders(resourceRoots, cancellationToken: cancellationToken))
        {
            if (!sourceResources.Contains(resource.ResourceName))
            {
                continue;
            }

            foreach (var path in resource.ShopMetaFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index.Add(resource.ResourceName, path);
            }
        }

        return index;
    }

    private static XDocument BuildShopMeta(
        TargetCollectionPlan plan,
        XDocument pedVariationXml,
        SourceShopMetadataIndex sourceShopMetadata,
        IReadOnlyList<DrawableMapping> drawableMappings,
        IReadOnlyList<PropMapping> propMappings,
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
                new XElement("pedComponents", new XAttribute("itemType", "ShopPedComponent"), BuildShopComponentItems(plan, pedVariationXml, sourceShopMetadata, drawableMappings)),
                new XElement("pedProps", new XAttribute("itemType", "ShopPedProp"), BuildShopPropItems(plan, pedVariationXml, sourceShopMetadata, propMappings))));

    private static IEnumerable<XNode> BuildShopComponentItems(
        TargetCollectionPlan plan,
        XDocument pedVariationXml,
        SourceShopMetadataIndex sourceShopMetadata,
        IReadOnlyList<DrawableMapping> drawableMappings)
    {
        var root = pedVariationXml.Root;
        if (root is null)
        {
            yield break;
        }

        var mappingsByTargetDrawable = drawableMappings
            .Where(mapping => mapping.TargetFullCollection.Equals(plan.FullCollectionName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                mapping => (mapping.ComponentId, mapping.NewDrawableIndex),
                mapping => mapping);
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
                    if (!mappingsByTargetDrawable.TryGetValue((componentId, drawableIndex), out var mapping)
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
        IReadOnlyList<PropMapping> propMappings)
    {
        var root = pedVariationXml.Root;
        if (root is null)
        {
            yield break;
        }

        var mappingsByTargetProp = propMappings
            .Where(mapping => mapping.TargetFullCollection.Equals(plan.FullCollectionName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                mapping => (mapping.AnchorId, mapping.NewPropIndex),
                mapping => mapping);
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
                if (!mappingsByTargetProp.TryGetValue((prop.AnchorId, prop.PropId), out var mapping)
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
