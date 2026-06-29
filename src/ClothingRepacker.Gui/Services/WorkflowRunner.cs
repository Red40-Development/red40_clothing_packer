using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;

namespace ClothingRepacker.Gui.Services;

public sealed class WorkflowRunner : IRepackerWorkflow
{
    private readonly RepackerServiceFactory _factory;

    public WorkflowRunner(RepackerServiceFactory factory)
    {
        _factory = factory;
    }

    public Task<ExportXmlResult> ExportXmlAsync(string folderPath, bool overwrite, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => RunWorkflowAsync(service => service.ExportYmtsToXmlAsync(folderPath, overwrite, progress, cancellationToken), cancellationToken);

    public Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<string> resourceFolders, string generatedResourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => RunWorkflowAsync(service => service.AnalyzeAsync(resourceFolders, generatedResourcesRoot, targetResource, settings, progress, cancellationToken), cancellationToken);

    public Task SavePlanAsync(MergePlan plan, string outputPath, CancellationToken cancellationToken)
        => _factory.Create().SavePlanAsync(plan, outputPath, cancellationToken);

    public Task<BuildResult> BuildAsync(MergePlan plan, string outputRoot, BuildOptions options, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => RunWorkflowAsync(service => service.BuildAsync(plan, outputRoot, options, progress, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, ApplyOptions options, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => RunWorkflowAsync(service => service.ApplyAsync(plan, backupRoot, options, progress, cancellationToken), cancellationToken);

    public Task RestoreAsync(string backupManifestPath, CancellationToken cancellationToken)
        => RunWorkflowAsync(service => service.RestoreAsync(backupManifestPath, cancellationToken), cancellationToken);

    private Task<TResult> RunWorkflowAsync<TResult>(Func<RepackerService, Task<TResult>> workflow, CancellationToken cancellationToken)
        => Task.Run(() => workflow(_factory.Create()), cancellationToken);

    private Task RunWorkflowAsync(Func<RepackerService, Task> workflow, CancellationToken cancellationToken)
        => Task.Run(() => workflow(_factory.Create()), cancellationToken);
}
