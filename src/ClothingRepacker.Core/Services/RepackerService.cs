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

    public async Task<AnalyzeResult> AnalyzeAsync(string resourcesRoot, string targetResource, MergePlanSettings settings, CancellationToken cancellationToken = default)
    {
        var scanItems = _scanner.ScanResources(resourcesRoot);
        var sources = new List<SourceYmt>();
        var streamFiles = new List<StreamFile>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var manifestWarnings = new List<SourceManifestWarning>();

        foreach (var item in scanItems)
        {
            streamFiles.AddRange(item.StreamFiles);
            if (item.ManifestPath is not null)
            {
                manifestWarnings.AddRange(ReadManifestWarnings(item));
            }

            foreach (var path in item.YmtFiles.Where(IsLikelyPedVariationXml))
            {
                XDocument xml;
                try
                {
                    xml = await _codec.DecodeToXmlAsync(path, cancellationToken);
                }
                catch (Exception ex)
                {
                    errors.Add($"{path}: could not decode YMT/XML ({ex.Message})");
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
        }

        var targets = _mergePlanner.Plan(sources, settings, warnings, errors);
        var drawableMappings = new List<DrawableMapping>();
        var propMappings = new List<PropMapping>();
        var targetPlans = new List<TargetCollectionPlan>();
        foreach (var target in targets)
        {
            var builder = new OutputCollectionBuilder(target.CollectionName, target.FullCollectionName, target.PedBaseName, target.Gender);
            foreach (var source in target.Sources)
            {
                drawableMappings.AddRange(builder.AddComponents(source));
                propMappings.AddRange(builder.AddProps(source));
            }

            var outputYmtPath = Path.Combine(targetResource, "stream", $"{target.FullCollectionName}.ymt.xml");
            targetPlans.Add(new TargetCollectionPlan(
                target.CollectionName,
                target.FullCollectionName,
                target.Gender,
                outputYmtPath.Replace(Path.DirectorySeparatorChar, '/'),
                target.Sources.Select(source => source.YmtPath).ToList(),
                builder.GetComponentCounts(),
                builder.GetPropCounts()));
        }

        var streamRenames = _streamRenamePlanner.BuildRenamePlan(drawableMappings, propMappings, streamFiles);
        errors.AddRange(_streamRenamePlanner.ValidateCollisions(streamRenames));

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
            DrawableMappings = drawableMappings,
            PropMappings = propMappings,
            StreamRenames = streamRenames.ToList(),
            OldYmtBackups = sources.Select(source => new OldYmtBackupPlan(
                source.YmtPath,
                Path.Combine("_clothing_repacker_backups", "{runId}", source.ResourceName, Path.GetRelativePath(source.ResourceRoot, source.YmtPath)).Replace(Path.DirectorySeparatorChar, '/'))).ToList(),
            SourceManifestWarnings = manifestWarnings,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };

        return new AnalyzeResult(plan, sources, streamFiles);
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

    public async Task<BuildResult> BuildAsync(MergePlan plan, string outputRoot, CancellationToken cancellationToken = default)
    {
        var sources = await ReloadSourcesForPlanAsync(plan, cancellationToken);
        var writtenFiles = new List<string>();
        var fullOutputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(fullOutputRoot);

        foreach (var targetPlan in plan.TargetCollections)
        {
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
            xml.Save(ymtOutputPath);
            writtenFiles.Add(ymtOutputPath);

            var metaPath = Path.Combine(fullOutputRoot, plan.TargetResource, "data", $"{targetPlan.FullCollectionName}.meta");
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            await File.WriteAllTextAsync(metaPath, BuildMinimalShopMeta(targetPlan), cancellationToken);
            writtenFiles.Add(metaPath);
        }

        var fxmanifestPath = Path.Combine(fullOutputRoot, plan.TargetResource, "fxmanifest.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(fxmanifestPath)!);
        await File.WriteAllTextAsync(fxmanifestPath, BuildFxManifest(plan), cancellationToken);
        writtenFiles.Add(fxmanifestPath);

        var validationPath = Path.Combine(fullOutputRoot, plan.TargetResource, "client", "validate_collections.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(validationPath)!);
        await File.WriteAllTextAsync(validationPath, BuildValidationLua(plan), cancellationToken);
        writtenFiles.Add(validationPath);

        return new BuildResult(fullOutputRoot, writtenFiles);
    }

    public async Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, bool yes, CancellationToken cancellationToken = default)
    {
        if (!yes)
        {
            throw new InvalidOperationException("apply requires --yes.");
        }

        var validationErrors = _planValidator.Validate(plan);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
        }

        var runId = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHHmmssZ");
        var backupDir = Path.Combine(Path.GetFullPath(backupRoot), runId);
        Directory.CreateDirectory(backupDir);

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"clothing-repacker-{Guid.NewGuid():N}");
        var buildResult = await BuildAsync(plan, stagingRoot, cancellationToken);
        var entries = new List<BackupEntry>();

        foreach (var rename in plan.StreamRenames)
        {
            if (!File.Exists(rename.SourcePath))
            {
                throw new FileNotFoundException($"Source file missing at apply time: {rename.SourcePath}");
            }

            var beforeHash = ComputeSha256(rename.SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(rename.TargetPath)!);
            File.Move(rename.SourcePath, rename.TargetPath);
            entries.Add(new BackupEntry("stream-rename", rename.SourcePath, null, rename.TargetPath, beforeHash, ComputeSha256(rename.TargetPath), DateTimeOffset.UtcNow));
        }

        foreach (var source in plan.SourceYmts)
        {
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
        }

        var generatedRoot = Path.Combine(Path.GetDirectoryName(plan.ResourcesRoot) ?? plan.ResourcesRoot, plan.TargetResource);
        if (Directory.Exists(generatedRoot))
        {
            Directory.Delete(generatedRoot, recursive: true);
        }

        CopyDirectory(Path.Combine(buildResult.OutputRoot, plan.TargetResource), generatedRoot);
        entries.Add(new BackupEntry("generated-resource", generatedRoot, null, generatedRoot, string.Empty, null, DateTimeOffset.UtcNow));

        var manifestPath = Path.Combine(backupDir, "backup-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(entries, _jsonOptions), cancellationToken);
        return entries;
    }

    public async Task RestoreAsync(string backupManifestPath, bool yes, CancellationToken cancellationToken = default)
    {
        if (!yes)
        {
            throw new InvalidOperationException("restore requires --yes.");
        }

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

    private async Task<Dictionary<string, SourceYmt>> ReloadSourcesForPlanAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SourceYmt>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.SourceYmts)
        {
            var xml = await _codec.DecodeToXmlAsync(source.Path, cancellationToken);
            result[source.Path] = _reader.Read(xml, source.Path, source.Resource, Path.GetDirectoryName(source.Path) ?? source.Resource);
        }

        return result;
    }

    private static bool IsLikelyPedVariationXml(string path)
        => path.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static string InferPedBaseName(string fullCollectionName)
    {
        var match = Regex.Match(fullCollectionName, @"^(.*)_(merged_[fm]_\d+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : fullCollectionName;
    }

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
<!-- Placeholder minimal shop meta. Replace with a fully valid SHOP_PED_APPAREL_META_FILE before in-game use. -->
<ShopPedApparel>
  <Collection name="{{plan.CollectionName}}" fullName="{{plan.FullCollectionName}}" gender="{{plan.Gender}}" />
</ShopPedApparel>
""";

    private static string BuildFxManifest(MergePlan plan)
    {
        var dependencies = plan.SourceYmts.Select(source => source.Resource).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
        var dataFiles = plan.TargetCollections
            .OrderBy(target => target.FullCollectionName)
            .Select(target => $"data_file 'SHOP_PED_APPAREL_META_FILE' 'data/{target.FullCollectionName}.meta'");

        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("author 'ClothingRepacker'");
        sb.AppendLine("description 'Generated merged clothing metadata'");
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
        sb.AppendLine();
        sb.AppendLine("client_script 'client/validate_collections.lua'");
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
