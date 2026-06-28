using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Gui.Services;

public sealed class WorkflowRunner : IRepackerWorkflow
{
    private readonly RepackerServiceFactory _factory;

    public WorkflowRunner(RepackerServiceFactory factory)
    {
        _factory = factory;
    }

    public Task<ExportXmlResult> ExportXmlAsync(string folderPath, bool overwrite, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => _factory.Create().ExportYmtsToXmlAsync(folderPath, overwrite, progress, cancellationToken);

    public Task<AnalyzeResult> AnalyzeAsync(string resourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => _factory.Create().AnalyzeAsync(resourcesRoot, targetResource, settings, progress, cancellationToken);

    public Task SavePlanAsync(MergePlan plan, string outputPath, CancellationToken cancellationToken)
        => _factory.Create().SavePlanAsync(plan, outputPath, cancellationToken);

    public Task<BuildResult> BuildAsync(MergePlan plan, string outputRoot, BuildOptions options, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => _factory.Create().BuildAsync(plan, outputRoot, options, progress, cancellationToken);

    public Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => _factory.Create().ApplyAsync(plan, backupRoot, progress, cancellationToken);

    public Task RestoreAsync(string backupManifestPath, CancellationToken cancellationToken)
        => _factory.Create().RestoreAsync(backupManifestPath, cancellationToken);
}
