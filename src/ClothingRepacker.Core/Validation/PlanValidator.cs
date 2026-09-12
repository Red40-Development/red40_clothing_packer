using System.Text.RegularExpressions;
using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Validation;

public sealed class PlanValidator
{
    private const int SupportedSchemaVersion = 2;
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<string> Validate(MergePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<string>();
        errors.AddRange(plan.Errors);

        if (plan.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add($"Unsupported plan schema version {plan.SchemaVersion}; rerun Analyze with this version.");
        }

        var componentCapacity = Math.Clamp(plan.Settings.MaxDrawablesPerComponent, 1, ClothingConstants.MaximumDrawablesPerComponent);
        var propCapacity = Math.Clamp(plan.Settings.MaxDrawablesPerProp, 1, ClothingConstants.MaximumDrawablesPerProp);
        if (plan.Settings.MaxDrawablesPerComponent <= 0 || plan.Settings.MaxDrawablesPerComponent > ClothingConstants.MaximumDrawablesPerComponent)
        {
            errors.Add($"Configured component drawable capacity must be between 1 and {ClothingConstants.MaximumDrawablesPerComponent}.");
        }

        if (plan.Settings.MaxDrawablesPerProp <= 0 || plan.Settings.MaxDrawablesPerProp > ClothingConstants.MaximumDrawablesPerProp)
        {
            errors.Add($"Configured prop drawable capacity must be between 1 and {ClothingConstants.MaximumDrawablesPerProp}; 256 cannot be represented by the YMT numAvailProps field.");
        }

        var resourceRoots = GetFullPaths(plan.ResourceRoots, "resource root", errors);
        if (resourceRoots.Count == 0)
        {
            errors.Add("Plan contains no selected resource roots; rerun Analyze with the current version.");
        }

        if (string.IsNullOrWhiteSpace(plan.ResourcesRoot) || !Path.IsPathRooted(plan.ResourcesRoot))
        {
            errors.Add("Plan resources root must be an absolute path.");
        }

        if (string.IsNullOrWhiteSpace(plan.GeneratedResourcesRoot) || !Path.IsPathRooted(plan.GeneratedResourcesRoot))
        {
            errors.Add("Plan generated resources root must be an absolute path.");
        }

        if (!SafePath.IsSafeName(plan.TargetResource))
        {
            errors.Add($"Target resource name is not safe: {plan.TargetResource}");
        }

        var generatedRoot = TryFullPath(plan.GeneratedResourcesRoot);
        var generatedTarget = generatedRoot is null || !SafePath.IsSafeName(plan.TargetResource)
            ? null
            : SafePath.ResolveInsideRoot(generatedRoot, plan.TargetResource);
        if (generatedRoot is not null && resourceRoots.Count > 0)
        {
            foreach (var sourceRoot in resourceRoots)
            {
                if (SafePath.IsInside(generatedRoot, sourceRoot) || SafePath.PathsEqual(generatedRoot, sourceRoot))
                {
                    errors.Add($"Generated resources root must be outside selected source roots: {generatedRoot}");
                }
            }
        }

        if (generatedTarget is not null)
        {
            foreach (var sourceRoot in resourceRoots)
            {
                if (SafePath.IsInside(generatedTarget, sourceRoot)
                    || SafePath.IsInside(sourceRoot, generatedTarget)
                    || SafePath.PathsEqual(generatedTarget, sourceRoot))
                {
                    errors.Add($"Generated target must not overlap a selected source root: {generatedTarget}");
                }
            }
        }

        foreach (var overlap in OverlappingPaths(resourceRoots))
        {
            errors.Add($"Selected source roots overlap: {overlap.Left} and {overlap.Right}.");
        }

        var resourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in resourceRoots)
        {
            var name = Path.GetFileName(root);
            if (!SafePath.IsSafeName(name))
            {
                errors.Add($"Selected resource name is not safe: {name}");
            }

            if (!resourceNames.Add(name))
            {
                errors.Add($"Duplicate source resource name maps to the same copy destination: {name}");
            }
        }

        var rootsByResource = resourceRoots
            .GroupBy(root => Path.GetFileName(root), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var sourceRootByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceSummaryByPath = new Dictionary<string, SourceYmtSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.SourceYmts)
        {
            ValidateSourcePath(source.Path, source.Resource, rootsByResource, sourceRootByPath, errors, "source YMT");
            if (TryFullPath(source.Path) is { } fullPath)
            {
                sourceSummaryByPath[fullPath] = source;
            }
        }

        foreach (var metadata in plan.SourceCreatureMetadata)
        {
            ValidateSourcePath(metadata.Path, metadata.Resource, rootsByResource, sourceRootByPath, errors, "creature metadata");
        }

        foreach (var metadata in plan.SourceAlternateMetadata)
        {
            ValidateSourcePath(metadata.Path, metadata.Resource, rootsByResource, sourceRootByPath, errors, "alternate metadata");
        }

        ValidateFingerprints(plan, rootsByResource, errors);
        ValidateTargets(plan, generatedRoot, generatedTarget, sourceSummaryByPath, componentCapacity, propCapacity, errors);
        ValidateMappings(plan, rootsByResource, sourceRootByPath, sourceSummaryByPath, errors);
        ValidateStreamRenames(plan, rootsByResource, errors);
        ValidateBackupDestinations(plan, errors);

        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ValidateTargets(
        MergePlan plan,
        string? generatedRoot,
        string? generatedTarget,
        IReadOnlyDictionary<string, SourceYmtSummary> sourceSummaryByPath,
        int componentCapacity,
        int propCapacity,
        List<string> errors)
    {
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetFullNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in plan.TargetCollections)
        {
            if (!SafePath.IsSafeName(target.CollectionName))
            {
                errors.Add($"Target collection name is not safe: {target.CollectionName}");
            }

            if (!SafePath.IsSafeName(target.FullCollectionName))
            {
                errors.Add($"Target full collection name is not safe: {target.FullCollectionName}");
            }

            if (!targetNames.Add(target.CollectionName))
            {
                errors.Add($"Duplicate target collection name: {target.CollectionName}");
            }

            if (!targetFullNames.Add(target.FullCollectionName))
            {
                errors.Add($"Duplicate target full collection name: {target.FullCollectionName}");
            }

            if (generatedRoot is not null && generatedTarget is not null)
            {
                try
                {
                    var output = SafePath.ResolveInsideRoot(generatedRoot, target.OutputYmtPath);
                    if (!SafePath.IsInside(output, generatedTarget))
                    {
                        errors.Add($"Target output path is outside the generated target: {target.OutputYmtPath}");
                    }

                    if (!targetPaths.Add(output))
                    {
                        errors.Add($"Duplicate target output path: {target.OutputYmtPath}");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add($"Unsafe target output path '{target.OutputYmtPath}': {ex.Message}");
                }
            }

            foreach (var component in target.ComponentCounts)
            {
                if (!IsComponentSlot(component.Key))
                {
                    errors.Add($"Target collection {target.FullCollectionName} has unsupported component slot {component.Key}.");
                }

                if (component.Value < 0 || component.Value > componentCapacity)
                {
                    errors.Add($"Target collection {target.FullCollectionName} component {component.Key} exceeds the safe drawable capacity of {componentCapacity}.");
                }
            }

            foreach (var prop in target.PropCounts)
            {
                if (!IsPropAnchor(prop.Key))
                {
                    errors.Add($"Target collection {target.FullCollectionName} has unsupported prop anchor {prop.Key}.");
                }

                if (prop.Value < 0)
                {
                    errors.Add($"Target collection {target.FullCollectionName} has a negative prop count at anchor {prop.Key}.");
                }
            }

            var propCount = target.PropCounts.Values.Sum();
            if (propCount > propCapacity)
            {
                errors.Add($"Target collection {target.FullCollectionName} contains {propCount} aggregate props, exceeding the safe capacity of {propCapacity}.");
            }

            ValidateRanges(target, sourceSummaryByPath, errors);

        }

        foreach (var output in plan.CreatureMetadataOutputs)
        {
            ValidateGeneratedRelativePath(output.OutputYmtPath, generatedRoot, generatedTarget, targetPaths, errors, "creature metadata output");
        }

        foreach (var output in plan.AlternateMetadataOutputs)
        {
            ValidateGeneratedRelativePath(output.OutputPath, generatedRoot, generatedTarget, targetPaths, errors, "alternate metadata output");
        }
    }

    private static void ValidateRanges(TargetCollectionPlan target, IReadOnlyDictionary<string, SourceYmtSummary> summaries, List<string> errors)
    {
        ValidateRanges(target.FullCollectionName, target.ComponentRanges, summaries, isProp: false, errors);
        ValidateRanges(target.FullCollectionName, target.PropRanges, summaries, isProp: true, errors);
    }

    private static void ValidateRanges(string targetName, IReadOnlyList<SourceIndexRange> ranges, IReadOnlyDictionary<string, SourceYmtSummary> summaries, bool isProp, List<string> errors)
    {
        foreach (var group in ranges.GroupBy(range => (Path: range.SourceYmtPath, range.SlotId), StringTupleComparer.Instance))
        {
            var ordered = group.OrderBy(range => range.StartIndex).ToList();
            var end = -1;
            foreach (var range in ordered)
            {
                if (range.StartIndex < 0 || range.Count <= 0)
                {
                    errors.Add($"Target {targetName} has a range with a non-negative start and positive count required.");
                    continue;
                }

                if (range.StartIndex <= end)
                {
                    errors.Add($"Target {targetName} has overlapping {(isProp ? "prop" : "component")} ranges for {range.SourceYmtPath} slot {range.SlotId}.");
                }

                end = Math.Max(end, range.StartIndex + range.Count - 1);
                if (!summaries.TryGetValue(TryFullPath(range.SourceYmtPath) ?? string.Empty, out var summary))
                {
                    errors.Add($"Range references unknown source YMT: {range.SourceYmtPath}");
                    continue;
                }

                var sourceCounts = isProp ? summary.Props : summary.Components;
                if (!sourceCounts.TryGetValue(range.SlotId, out var sourceCount) || range.StartIndex + range.Count > sourceCount)
                {
                    errors.Add($"Range for {range.SourceYmtPath} slot {range.SlotId} does not fit the analyzed source count.");
                }

                if (isProp ? !IsPropAnchor(range.SlotId) : !IsComponentSlot(range.SlotId))
                {
                    errors.Add($"Range references unsupported {(isProp ? "prop anchor" : "component slot")} {range.SlotId}.");
                }
            }
        }
    }

    private static void ValidateMappings(
        MergePlan plan,
        IReadOnlyDictionary<string, string> rootsByResource,
        IReadOnlyDictionary<string, string> sourceRootByPath,
        IReadOnlyDictionary<string, SourceYmtSummary> summaries,
        List<string> errors)
    {
        var targets = plan.TargetCollections
            .GroupBy(target => target.FullCollectionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var componentSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var componentTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in plan.DrawableMappings)
        {
            ValidateMappingSource(mapping.SourceYmtPath, mapping.SourceResource, sourceRootByPath, rootsByResource, errors);
            if (!IsComponentSlot(mapping.ComponentId) || mapping.OldDrawableIndex < 0 || mapping.NewDrawableIndex < 0)
            {
                errors.Add($"Invalid component mapping slot or index for {mapping.SourceYmtPath}: slot {mapping.ComponentId}, old {mapping.OldDrawableIndex}, new {mapping.NewDrawableIndex}.");
            }

            var sourceKey = $"{mapping.SourceYmtPath}|{mapping.ComponentId}|{mapping.OldDrawableIndex}";
            if (!componentSourceKeys.Add(sourceKey))
            {
                errors.Add($"Component mapping source is not one-to-one: {sourceKey}");
            }

            var targetKey = $"{mapping.TargetFullCollection}|{mapping.ComponentId}|{mapping.NewDrawableIndex}";
            if (!componentTargetKeys.Add(targetKey))
            {
                errors.Add($"Component mapping target is not one-to-one: {targetKey}");
            }

            ValidateTargetMapping(mapping.TargetFullCollection, mapping.SourceYmtPath, mapping.ComponentId, mapping.OldDrawableIndex, mapping.NewDrawableIndex, targets, summaries, isProp: false, errors);
        }

        var propSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var propTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in plan.PropMappings)
        {
            ValidateMappingSource(mapping.SourceYmtPath, mapping.SourceResource, sourceRootByPath, rootsByResource, errors);
            if (!IsPropAnchor(mapping.AnchorId) || mapping.OldPropIndex < 0 || mapping.NewPropIndex < 0)
            {
                errors.Add($"Invalid prop mapping anchor or index for {mapping.SourceYmtPath}: anchor {mapping.AnchorId}, old {mapping.OldPropIndex}, new {mapping.NewPropIndex}.");
            }

            var sourceKey = $"{mapping.SourceYmtPath}|{mapping.AnchorId}|{mapping.OldPropIndex}";
            if (!propSourceKeys.Add(sourceKey))
            {
                errors.Add($"Prop mapping source is not one-to-one: {sourceKey}");
            }

            var targetKey = $"{mapping.TargetFullCollection}|{mapping.AnchorId}|{mapping.NewPropIndex}";
            if (!propTargetKeys.Add(targetKey))
            {
                errors.Add($"Prop mapping target is not one-to-one: {targetKey}");
            }

            ValidateTargetMapping(mapping.TargetFullCollection, mapping.SourceYmtPath, mapping.AnchorId, mapping.OldPropIndex, mapping.NewPropIndex, targets, summaries, isProp: true, errors);
        }

        foreach (var target in plan.TargetCollections)
        {
            foreach (var component in target.ComponentCounts.Where(component => component.Value != 0 && !target.ComponentRanges.Any(range => range.SlotId == component.Key)))
            {
                errors.Add($"Target component count for slot {component.Key} has no planned source ranges.");
            }

            foreach (var prop in target.PropCounts.Where(prop => prop.Value != 0 && !target.PropRanges.Any(range => range.SlotId == prop.Key)))
            {
                errors.Add($"Target prop count for anchor {prop.Key} has no planned source ranges.");
            }

            foreach (var range in target.ComponentRanges)
            {
                var mappings = plan.DrawableMappings.Where(mapping => mapping.TargetFullCollection.Equals(target.FullCollectionName, StringComparison.OrdinalIgnoreCase)
                    && mapping.SourceYmtPath.Equals(range.SourceYmtPath, StringComparison.OrdinalIgnoreCase)
                    && mapping.ComponentId == range.SlotId).ToList();
                ValidateRangeCoverage(range, mappings.Select(mapping => mapping.OldDrawableIndex), errors, "component");
            }

            foreach (var range in target.PropRanges)
            {
                var mappings = plan.PropMappings.Where(mapping => mapping.TargetFullCollection.Equals(target.FullCollectionName, StringComparison.OrdinalIgnoreCase)
                    && mapping.SourceYmtPath.Equals(range.SourceYmtPath, StringComparison.OrdinalIgnoreCase)
                    && mapping.AnchorId == range.SlotId).ToList();
                ValidateRangeCoverage(range, mappings.Select(mapping => mapping.OldPropIndex), errors, "prop");
            }

            foreach (var componentGroup in target.ComponentRanges.GroupBy(range => range.SlotId))
            {
                var expected = target.ComponentCounts.GetValueOrDefault(componentGroup.Key);
                if (expected != componentGroup.Sum(range => range.Count))
                {
                    errors.Add($"Target component count for slot {componentGroup.Key} does not agree with its planned range counts.");
                }

                var actualNew = plan.DrawableMappings
                    .Where(mapping => mapping.TargetFullCollection.Equals(target.FullCollectionName, StringComparison.OrdinalIgnoreCase) && mapping.ComponentId == componentGroup.Key)
                    .Select(mapping => mapping.NewDrawableIndex)
                    .ToHashSet();
                if (!Enumerable.Range(0, expected).ToHashSet().SetEquals(actualNew))
                {
                    errors.Add($"Target component mappings for slot {componentGroup.Key} do not cover the planned target count.");
                }
            }

            foreach (var propGroup in target.PropRanges.GroupBy(range => range.SlotId))
            {
                var expected = target.PropCounts.GetValueOrDefault(propGroup.Key);
                if (expected != propGroup.Sum(range => range.Count))
                {
                    errors.Add($"Target prop count for anchor {propGroup.Key} does not agree with its planned range counts.");
                }

                var actualNew = plan.PropMappings
                    .Where(mapping => mapping.TargetFullCollection.Equals(target.FullCollectionName, StringComparison.OrdinalIgnoreCase) && mapping.AnchorId == propGroup.Key)
                    .Select(mapping => mapping.NewPropIndex)
                    .ToHashSet();
                if (!Enumerable.Range(0, expected).ToHashSet().SetEquals(actualNew))
                {
                    errors.Add($"Target prop mappings for anchor {propGroup.Key} do not cover the planned target count.");
                }
            }
        }
    }
    private static void ValidateRangeCoverage(SourceIndexRange range, IEnumerable<int> oldIndexes, List<string> errors, string kind)
    {
        var indexes = oldIndexes.ToList();
        if (kind.Equals("prop", StringComparison.OrdinalIgnoreCase))
        {
            if (indexes.Distinct().Count() != range.Count)
            {
                errors.Add($"Prop range for {range.SourceYmtPath} anchor {range.SlotId} requires exactly {range.Count} distinct mappings.");
            }

            return;
        }

        var expected = Enumerable.Range(range.StartIndex, range.Count).ToHashSet();
        if (!expected.SetEquals(indexes))
        {
            errors.Add($"Component range for {range.SourceYmtPath} slot {range.SlotId} does not have exact old-index coverage.");
        }
    }
    private static void ValidateTargetMapping(
        string targetFullCollection,
        string sourcePath,
        int slot,
        int oldIndex,
        int newIndex,
        IReadOnlyDictionary<string, TargetCollectionPlan> targets,
        IReadOnlyDictionary<string, SourceYmtSummary> summaries,
        bool isProp,
        List<string> errors)
    {
        if (!targets.TryGetValue(targetFullCollection, out var target))
        {
            errors.Add($"Mapping references unknown target collection: {targetFullCollection}");
            return;
        }

        var ranges = isProp ? target.PropRanges : target.ComponentRanges;
        var range = ranges.FirstOrDefault(item => item.SourceYmtPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) && item.SlotId == slot);
        if (range is null)
        {
            errors.Add($"Mapping does not belong to a planned {(isProp ? "prop" : "component")} range: {sourcePath}, slot {slot}.");
            return;
        }

        var targetCount = isProp
            ? target.PropCounts.GetValueOrDefault(slot)
            : target.ComponentCounts.GetValueOrDefault(slot);
        var invalidIndex = isProp
            ? newIndex < 0 || newIndex >= targetCount
            : oldIndex < range.StartIndex || oldIndex >= range.StartIndex + range.Count || newIndex < 0 || newIndex >= targetCount;
        if (invalidIndex)
        {
            errors.Add($"Mapping index falls outside its planned {(isProp ? "prop" : "component")} range: {sourcePath}, slot {slot}.");
        }
    }
    private static void ValidateStreamRenames(MergePlan plan, IReadOnlyDictionary<string, string> rootsByResource, List<string> errors)
    {
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rename in plan.StreamRenames)
        {
            if (!sourcePaths.Add(rename.SourcePath))
            {
                errors.Add($"Stream rename source is not unique: {rename.SourcePath}");
            }

            if (!targetPaths.Add(rename.TargetPath))
            {
                errors.Add($"Planned target path collision: {rename.TargetPath}");
            }

            if (!rootsByResource.TryGetValue(rename.SourceResource, out var resourceRoot))
            {
                errors.Add($"Stream rename references unknown source resource: {rename.SourceResource}");
                continue;
            }

            try
            {
                SafePath.RequireInside(rename.SourcePath, resourceRoot, allowEqual: false);
                SafePath.RequireInside(rename.TargetPath, resourceRoot, allowEqual: false);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"Unsafe stream rename path: {ex.Message}");
            }
        }
    }

    private static void ValidateFingerprints(MergePlan plan, IReadOnlyDictionary<string, string> rootsByResource, List<string> errors)
    {
        var requiredPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Require(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                requiredPaths[TryFullPath(path) ?? path] = path;
            }
        }

        foreach (var source in plan.SourceYmts)
        {
            Require(source.Path);
        }

        foreach (var metadata in plan.SourceCreatureMetadata)
        {
            Require(metadata.Path);
        }

        foreach (var metadata in plan.SourceAlternateMetadata)
        {
            Require(metadata.Path);
        }

        foreach (var rename in plan.StreamRenames)
        {
            Require(rename.SourcePath);
        }

        foreach (var backup in plan.OldYmtBackups)
        {
            Require(backup.SourcePath);
        }

        foreach (var backup in plan.BrokenCreatureMetadataBackups)
        {
            Require(backup.SourcePath);
        }

        foreach (var backup in plan.SourceAlternateMetadataBackups)
        {
            Require(backup.SourcePath);
        }

        foreach (var warning in plan.SourceManifestWarnings)
        {
            Require(warning.ManifestPath);
        }

        foreach (var resourceRoot in rootsByResource.Values)
        {
            if (ResourceManifestLocator.Find(resourceRoot) is { } manifestPath)
            {
                Require(manifestPath);
            }
        }

        var fingerprintPaths = plan.SourceFiles
            .Select(fingerprint => TryFullPath(fingerprint.Path))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in requiredPaths)
        {
            if (!fingerprintPaths.Contains(required.Key))
            {
                errors.Add($"Missing Analyze-time source fingerprint for {required.Value}; rerun Analyze.");
            }
        }

        if (plan.SourceFiles.Count == 0)
        {
            errors.Add("Plan contains no Analyze-time source fingerprints; rerun Analyze with the current version.");
            return;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fingerprint in plan.SourceFiles)
        {
            var fullPath = TryFullPath(fingerprint.Path);
            if (fullPath is null)
            {
                errors.Add($"Invalid source fingerprint path: {fingerprint.Path}");
                continue;
            }

            if (!paths.Add(fullPath))
            {
                errors.Add($"Duplicate source fingerprint path: {fingerprint.Path}");
            }

            if (!rootsByResource.Values.Any(root => SafePath.IsInside(fullPath, root)))
            {
                errors.Add($"Source fingerprint path is outside selected resource roots: {fingerprint.Path}");
            }

            var fingerprintRoot = TryFullPath(fingerprint.ResourceRoot);
            if (fingerprintRoot is null)
            {
                errors.Add($"Invalid source fingerprint root: {fingerprint.ResourceRoot}");
            }
            else
            {
                if (!rootsByResource.Values.Any(root => SafePath.PathsEqual(root, fingerprintRoot)))
                {
                    errors.Add($"Source fingerprint root is not a selected resource root: {fingerprint.ResourceRoot}");
                }

                try
                {
                    SafePath.RequireInside(fullPath, fingerprintRoot, allowEqual: false);
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add($"Unsafe source fingerprint path '{fingerprint.Path}': {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(fingerprint.Kind) || !Sha256Pattern.IsMatch(fingerprint.Sha256))
            {
                errors.Add($"Invalid Analyze-time fingerprint for {fingerprint.Path}.");
            }
        }
    }

    private static void ValidateBackupDestinations(MergePlan plan, List<string> errors)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in plan.OldYmtBackups.Select(item => item.BackupPath)
                     .Concat(plan.BrokenCreatureMetadataBackups.Select(item => item.BackupPath))
                     .Concat(plan.SourceAlternateMetadataBackups.Select(item => item.BackupPath)))
        {
            if (!IsSafeBackupRelativePath(path))
            {
                errors.Add($"Unsafe backup destination: {path}");
            }

            var normalized = NormalizeBackupRelativePath(path);
            if (!paths.Add(normalized))
            {
                errors.Add($"Duplicate backup destination: {path}");
            }
        }
    }

    private static string NormalizeBackupRelativePath(string path)
        => string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static void ValidateSourcePath(string path, string resource, IReadOnlyDictionary<string, string> rootsByResource, Dictionary<string, string> sourceRootByPath, List<string> errors, string kind)
    {
        if (!rootsByResource.TryGetValue(resource, out var root))
        {
            errors.Add($"{kind} references unknown source resource '{resource}': {path}");
            return;
        }

        try
        {
            var fullPath = SafePath.RequireInside(path, root, allowEqual: false);
            if (!sourceRootByPath.TryAdd(fullPath, root))
            {
                errors.Add($"Duplicate source path: {path}");
            }
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"Unsafe {kind} path '{path}': {ex.Message}");
        }
    }

    private static void ValidateMappingSource(string path, string resource, IReadOnlyDictionary<string, string> sourceRootByPath, IReadOnlyDictionary<string, string> rootsByResource, List<string> errors)
    {
        if (!rootsByResource.TryGetValue(resource, out var root))
        {
            errors.Add($"Mapping references unknown source resource '{resource}': {path}");
            return;
        }

        try
        {
            SafePath.RequireInside(path, root, allowEqual: false);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"Unsafe mapping source path '{path}': {ex.Message}");
        }

        if (!sourceRootByPath.ContainsKey(TryFullPath(path) ?? string.Empty))
        {
            errors.Add($"Mapping references a source not present in the plan: {path}");
        }
    }

    private static void ValidateGeneratedRelativePath(string path, string? generatedRoot, string? generatedTarget, HashSet<string> paths, List<string> errors, string kind)
    {
        if (generatedRoot is null || generatedTarget is null)
        {
            return;
        }

        try
        {
            var fullPath = SafePath.ResolveInsideRoot(generatedRoot, path);
            if (!SafePath.IsInside(fullPath, generatedTarget))
            {
                errors.Add($"{kind} is outside the generated target: {path}");
            }

            if (!paths.Add(fullPath))
            {
                errors.Add($"Duplicate generated output path: {path}");
            }
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"Unsafe {kind} path '{path}': {ex.Message}");
        }
    }

    private static List<string> GetFullPaths(IEnumerable<string> paths, string kind, List<string> errors)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                var full = SafePath.Normalize(path);
                if (!seen.Add(full))
                {
                    errors.Add($"Duplicate {kind}: {path}");
                }
                else
                {
                    result.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                errors.Add($"Invalid {kind}: {path}");
            }
        }

        return result;
    }

    private static IEnumerable<(string Left, string Right)> OverlappingPaths(IReadOnlyList<string> paths)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            for (var j = i + 1; j < paths.Count; j++)
            {
                if (SafePath.IsInside(paths[i], paths[j]) || SafePath.IsInside(paths[j], paths[i]) || SafePath.PathsEqual(paths[i], paths[j]))
                {
                    yield return (paths[i], paths[j]);
                }
            }
        }
    }

    private static bool IsSafeBackupRelativePath(string path)
        => !string.IsNullOrWhiteSpace(path)
           && !Path.IsPathRooted(path)
           && !path.Contains(':')
           && !path.Replace('\\', '/').Split('/', StringSplitOptions.None).Any(segment => segment is "." or "..");

    private static bool IsComponentSlot(int value) => value is >= 0 and < ClothingConstants.ComponentSlotCount;

    private static bool IsPropAnchor(int value) => ClothingConstants.AnchorNames.ContainsKey(value);

    private static string? TryFullPath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : SafePath.Normalize(path);
        }
        catch
        {
            return null;
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Path, int SlotId)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Path, int SlotId) x, (string Path, int SlotId) y)
            => x.SlotId == y.SlotId && x.Path.Equals(y.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Path, int SlotId) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), obj.SlotId);
    }
}
