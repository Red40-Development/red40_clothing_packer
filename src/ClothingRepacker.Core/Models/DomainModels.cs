using System.Xml.Linq;

namespace ClothingRepacker.Core.Models;

public sealed record SourceYmt(
    string YmtPath,
    string ResourceName,
    string ResourceRoot,
    string PedBaseName,
    PedGender Gender,
    string CollectionName,
    string FullCollectionName,
    string DlcName,
    XDocument Xml,
    IReadOnlyList<ComponentBlock> Components,
    IReadOnlyList<PropBlock> Props,
    IReadOnlyList<ValidationMessage> Messages);

public sealed record ComponentBlock(
    int ComponentId,
    IReadOnlyList<XElement> Drawables,
    IReadOnlyList<XElement> CompInfos);

public sealed record PropBlock(
    int AnchorId,
    IReadOnlyList<XElement> Props);

public sealed record DrawableMapping(
    string SourceResource,
    string SourceYmtPath,
    string SourceCollection,
    string SourceFullCollection,
    string TargetCollection,
    string TargetFullCollection,
    string PedBaseName,
    int ComponentId,
    int OldDrawableIndex,
    int NewDrawableIndex);

public sealed record PropMapping(
    string SourceResource,
    string SourceYmtPath,
    string SourceCollection,
    string SourceFullCollection,
    string TargetCollection,
    string TargetFullCollection,
    string PedBaseName,
    int AnchorId,
    int OldPropIndex,
    int NewPropIndex);

public sealed record StreamRename(
    string SourcePath,
    string TargetPath,
    string SourceResource,
    string Reason,
    string? Sha256Before,
    string? Sha256After,
    bool IsEscrowOpaque);

public sealed record StreamFile(
    string ResourceName,
    string ResourceRoot,
    string FullPath,
    string FileName,
    string Extension,
    bool IsUnderStreamFolder);

public sealed record BackupEntry(
    string Kind,
    string OriginalPath,
    string? BackupPath,
    string? AppliedPath,
    string Sha256Before,
    string? Sha256After,
    DateTimeOffset CreatedAtUtc);

public sealed record OldYmtBackupPlan(
    string SourcePath,
    string BackupPath);

public sealed record SourceManifestWarning(
    string Resource,
    string ManifestPath,
    string Kind,
    string Line,
    string Recommendation);

public sealed record SourceYmtSummary(
    string Resource,
    string Path,
    string PedBaseName,
    PedGender Gender,
    string CollectionName,
    string FullCollectionName,
    string DlcName,
    Dictionary<int, int> Components,
    Dictionary<int, int> Props);

public sealed record TargetCollectionPlan(
    string CollectionName,
    string FullCollectionName,
    PedGender Gender,
    string OutputYmtPath,
    List<string> SourceYmts,
    Dictionary<int, int> ComponentCounts,
    Dictionary<int, int> PropCounts);

public sealed class MergePlan
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ResourcesRoot { get; init; } = string.Empty;
    public string TargetResource { get; init; } = "zz_merged_clothing_meta";
    public MergePlanSettings Settings { get; init; } = new();
    public List<SourceYmtSummary> SourceYmts { get; init; } = [];
    public List<TargetCollectionPlan> TargetCollections { get; init; } = [];
    public List<DrawableMapping> DrawableMappings { get; init; } = [];
    public List<PropMapping> PropMappings { get; init; } = [];
    public List<StreamRename> StreamRenames { get; init; } = [];
    public List<OldYmtBackupPlan> OldYmtBackups { get; init; } = [];
    public List<SourceManifestWarning> SourceManifestWarnings { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public sealed class MergePlanSettings
{
    public string TargetPrefix { get; init; } = "merged";
    public int MaxDrawablesPerComponent { get; init; } = ClothingConstants.DefaultMaxDrawablesPerComponent;
    public int MaxDrawablesPerProp { get; init; } = ClothingConstants.DefaultMaxDrawablesPerProp;
    public string ShopMetaMode { get; init; } = "minimal";
    public bool RenameStreamsInPlace { get; init; } = true;
    public string FemalePrefix { get; init; } = "merged_f";
    public string MalePrefix { get; init; } = "merged_m";
}

public sealed record ValidationMessage(
    ValidationSeverity Severity,
    string Code,
    string Message);

public enum ValidationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public sealed record AnalyzeResult(
    MergePlan Plan,
    IReadOnlyList<SourceYmt> Sources,
    IReadOnlyList<StreamFile> StreamFiles);

public sealed record BuildResult(
    string OutputRoot,
    IReadOnlyList<string> WrittenFiles);

public sealed record ExportXmlResult(
    string RootFolder,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> SkippedFiles);
