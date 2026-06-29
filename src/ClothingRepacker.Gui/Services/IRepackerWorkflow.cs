using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Gui.Services;

public interface IRepackerWorkflow
{
    Task<ExportXmlResult> ExportXmlAsync(string folderPath, bool overwrite, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<string> resourceFolders, string generatedResourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task SavePlanAsync(MergePlan plan, string outputPath, CancellationToken cancellationToken);
    Task<BuildResult> BuildAsync(MergePlan plan, string outputRoot, BuildOptions options, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, ApplyOptions options, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task RestoreAsync(string backupManifestPath, CancellationToken cancellationToken);
}
