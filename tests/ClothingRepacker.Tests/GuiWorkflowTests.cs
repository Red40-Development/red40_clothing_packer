using ClothingRepacker.Core.Models;
using ClothingRepacker.Gui.Models;
using ClothingRepacker.Gui.Services;
using ClothingRepacker.Gui.ViewModels;

namespace ClothingRepacker.Tests;

public class GuiWorkflowTests
{
    [Fact]
    public void SelectingResourcesFolderSetsDefaultBackupRoot()
    {
        var root = CreateTempDirectory("gui-select");
        var vm = CreateViewModel();

        vm.SelectResourcesFolder(root);

        Assert.Equal(root, vm.ResourcesPath);
        Assert.Equal(Path.Combine(Directory.GetParent(root)!.FullName, "backups"), vm.BackupRoot);
        Assert.Equal(Path.Combine(root, "plan.json"), vm.PlanPath);
    }

    [Fact]
    public async Task AnalyzeSuccessPopulatesSummaryAndEnablesBuild()
    {
        var root = CreateTempDirectory("gui-analyze-success");
        var workflow = new FakeWorkflow
        {
            AnalyzeResult = BuildAnalyzeResult(errorCount: 0, warningCount: 1),
        };
        var vm = CreateViewModel(workflow);
        vm.SelectResourcesFolder(root);

        await vm.AnalyzeAsync();

        Assert.Equal(2, vm.Summary.SourceYmtCount);
        Assert.Equal(1, vm.Summary.TargetCollectionCount);
        Assert.Equal(1, vm.Summary.WarningCount);
        Assert.True(vm.CanBuildPreview());
        Assert.False(vm.CanApply());
    }

    [Fact]
    public async Task AnalyzeErrorsDisableBuildAndApply()
    {
        var root = CreateTempDirectory("gui-analyze-errors");
        var workflow = new FakeWorkflow
        {
            AnalyzeResult = BuildAnalyzeResult(errorCount: 1, warningCount: 0),
        };
        var vm = CreateViewModel(workflow);
        vm.SelectResourcesFolder(root);

        await vm.AnalyzeAsync();

        Assert.Single(vm.Errors);
        Assert.False(vm.CanBuildPreview());
        Assert.False(vm.CanApply());
    }

    [Fact]
    public async Task SuccessfulBuildEnablesApply()
    {
        var root = CreateTempDirectory("gui-build");
        var output = CreateTempDirectory("gui-build-output");
        var workflow = new FakeWorkflow
        {
            AnalyzeResult = BuildAnalyzeResult(errorCount: 0, warningCount: 0),
            BuildResult = new BuildResult(output, [Path.Combine(output, "zz_merged_clothing_meta", "fxmanifest.lua")]),
        };
        var vm = CreateViewModel(workflow);
        vm.SelectResourcesFolder(root);
        vm.OutputPath = output;

        await vm.AnalyzeAsync();
        await vm.BuildPreviewAsync();

        Assert.True(vm.CanApply());
        Assert.Single(vm.Files);
    }

    [Fact]
    public async Task CancellationResetsBusyStateWithoutEnablingUnsafeActions()
    {
        var root = CreateTempDirectory("gui-cancel");
        var workflow = new FakeWorkflow
        {
            BlockExportUntilCanceled = true,
        };
        var vm = CreateViewModel(workflow);
        vm.SelectResourcesFolder(root);

        var task = vm.ExportXmlAsync();
        await workflow.ExportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);

        vm.CancelOperation();
        await task;

        Assert.False(vm.IsBusy);
        Assert.False(vm.CanBuildPreview());
        Assert.False(vm.CanApply());
    }

    [Fact]
    public void RestoreRequiresSelectedManifestPath()
    {
        var root = CreateTempDirectory("gui-restore");
        var manifest = Path.Combine(root, "backup-manifest.json");
        File.WriteAllText(manifest, "[]");
        var vm = CreateViewModel();

        Assert.False(vm.CanRestore());

        vm.RestoreManifestPath = manifest;

        Assert.True(vm.CanRestore());
    }

    private static MainWindowViewModel CreateViewModel(FakeWorkflow? workflow = null)
        => new(workflow ?? new FakeWorkflow(), new FakeSettingsStore());

    private static AnalyzeResult BuildAnalyzeResult(int errorCount, int warningCount)
    {
        var plan = new MergePlan
        {
            ResourcesRoot = Path.GetTempPath(),
            TargetResource = "zz_merged_clothing_meta",
            SourceYmts =
            [
                new SourceYmtSummary("pack_a", "/tmp/a.ymt", "mp_f_freemode_01", PedGender.Female, "pack_a", "mp_f_freemode_01_pack_a", "hash_00000000", [], []),
                new SourceYmtSummary("pack_b", "/tmp/b.ymt", "mp_m_freemode_01", PedGender.Male, "pack_b", "mp_m_freemode_01_pack_b", "hash_00000000", [], []),
            ],
            TargetCollections =
            [
                new TargetCollectionPlan("merged_f_001", "mp_f_freemode_01_merged_f_001", PedGender.Female, "zz_merged_clothing_meta/stream/mp_f_freemode_01_merged_f_001.ymt", ["/tmp/a.ymt"], [], [], [], []),
            ],
            StreamRenames =
            [
                new StreamRename("/tmp/a.ydd", "/tmp/merged.ydd", "pack_a", "component", null, null, false),
            ],
            Warnings = Enumerable.Range(0, warningCount).Select(index => $"warning {index}").ToList(),
            Errors = Enumerable.Range(0, errorCount).Select(index => $"error {index}").ToList(),
        };

        return new AnalyzeResult(plan, [], [], []);
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeSettingsStore : IRecentSettingsStore
    {
        public RecentSettings LastSaved { get; private set; } = new();

        public Task<RecentSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RecentSettings());

        public Task SaveAsync(RecentSettings settings, CancellationToken cancellationToken = default)
        {
            LastSaved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkflow : IRepackerWorkflow
    {
        public TaskCompletionSource ExportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockExportUntilCanceled { get; init; }
        public AnalyzeResult AnalyzeResult { get; init; } = BuildAnalyzeResult(errorCount: 0, warningCount: 0);
        public BuildResult BuildResult { get; init; } = new(Path.GetTempPath(), []);

        public async Task<ExportXmlResult> ExportXmlAsync(string folderPath, bool overwrite, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            ExportStarted.TrySetResult();
            if (BlockExportUntilCanceled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ExportXmlResult(folderPath, [], []);
        }

        public Task<AnalyzeResult> AnalyzeAsync(string resourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
            => Task.FromResult(AnalyzeResult);

        public Task SavePlanAsync(MergePlan plan, string outputPath, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<BuildResult> BuildAsync(MergePlan plan, string outputRoot, BuildOptions options, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
            => Task.FromResult(BuildResult);

        public Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BackupEntry>>([]);

        public Task RestoreAsync(string backupManifestPath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
