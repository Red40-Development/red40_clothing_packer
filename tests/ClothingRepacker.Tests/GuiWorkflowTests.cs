using ClothingRepacker.Core;
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

        var parent = Directory.GetParent(root)!.FullName;
        var defaultRoot = Directory.GetParent(parent)!.FullName;
        var outputRoot = Path.Combine(defaultRoot, "red40_output");

        Assert.Equal(root, vm.ResourcesPath);
        Assert.Equal([root], vm.ResourcePaths.ToArray());
        Assert.Equal(outputRoot, vm.OutputPath);
        Assert.Equal(outputRoot, vm.GeneratedResourcesRoot);
        Assert.Equal(Path.Combine(defaultRoot, "backups"), vm.BackupRoot);
        Assert.Equal(Path.Combine(parent, "plan.json"), vm.PlanPath);
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
        var parent = Directory.GetParent(root)!.FullName;
        var outputRoot = Path.Combine(Directory.GetParent(parent)!.FullName, "red40_output");

        await vm.AnalyzeAsync();

        Assert.Equal([root], workflow.AnalyzeResourceFolders);
        Assert.Equal(outputRoot, workflow.AnalyzeGeneratedResourcesRoot);
        Assert.False(workflow.AnalyzeSettings.RenameStreamsInPlace);
        Assert.False(workflow.AnalyzeSettings.OptimizeYmtUsage);
        Assert.Equal(2, vm.SelectedTabIndex);
        Assert.Equal(2, vm.Summary.SourceYmtCount);
        Assert.Equal(1, vm.Summary.TargetCollectionCount);
        Assert.Equal(1, vm.Summary.WarningCount);
        Assert.True(vm.HasRepackReport);
        Assert.Contains(vm.RepackReportLines, line => line.Contains("pack_a", StringComparison.OrdinalIgnoreCase));
        Assert.True(vm.CanBuildPreview());
        Assert.False(vm.CanApply());
    }

    [Fact]
    public async Task ChangingPlanInputsClearsRepackReport()
    {
        var root = CreateTempDirectory("gui-repack-report-reset");
        var workflow = new FakeWorkflow
        {
            AnalyzeResult = BuildAnalyzeResult(errorCount: 0, warningCount: 0),
        };
        var vm = CreateViewModel(workflow);
        vm.SelectResourcesFolder(root);

        await vm.AnalyzeAsync();

        Assert.True(vm.HasRepackReport);

        vm.TargetResource = "zz_other_clothing_meta";

        Assert.False(vm.HasRepackReport);
        Assert.Null(vm.RepackReport);
        Assert.Empty(vm.RepackReportLines);
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
        Assert.False(workflow.BuildOptions.IncludeYmtXml);
        Assert.False(workflow.BuildOptions.IncludeDebugClient);
    }

    [Fact]
    public async Task BuildFailureLogsActivePathAndFullExceptionDetails()
    {
        var root = CreateTempDirectory("gui-build-failure");
        var workflow = new FakeWorkflow
        {
            AnalyzeResult = BuildAnalyzeResult(errorCount: 0, warningCount: 0),
            BuildException = new InvalidOperationException("Context around build failure", new OverflowException("Value was either too large or too small for an Int32.")),
        };
        var vm = CreateViewModel(workflow);
        vm.SelectResourcesFolder(root);

        await vm.AnalyzeAsync();
        await vm.BuildPreviewAsync();

        Assert.Contains("Context around build failure", vm.Status);
        Assert.Contains(vm.LogLines, line => line.Contains("Active path when the error occurred", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.LogLines, line => line.Contains("System.InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(vm.LogLines, line => line.Contains("System.OverflowException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyBeforeRenameOptionFlowsThroughAnalyzeAndApply()
    {
        var root = CreateTempDirectory("gui-copy-before-rename");
        var output = CreateTempDirectory("gui-copy-before-rename-output");
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
        await vm.ApplyAsync();

        Assert.False(workflow.AnalyzeSettings.RenameStreamsInPlace);
        Assert.True(workflow.ApplyOptions.CopyResourcesToOutputBeforeRename);
        Assert.False(workflow.ApplyOptions.IncludeYmtXml);
        Assert.False(workflow.ApplyOptions.IncludeDebugClient);
    }

    [Fact]
    public async Task OptimizeYmtUsageOptionFlowsThroughAnalyzeAndProjectSettings()
    {
        var root = CreateTempDirectory("gui-optimize-ymt");
        var settingsStore = new FakeSettingsStore(projectPath: "/tmp/project.json");
        var workflow = new FakeWorkflow
        {
            AnalyzeResult = BuildAnalyzeResult(errorCount: 0, warningCount: 0),
        };
        var vm = new MainWindowViewModel(workflow, settingsStore);
        await WaitForAsync(() => vm.HasCurrentProject);
        vm.SelectResourcesFolder(root);
        vm.OptimizeYmtUsage = true;

        await vm.AnalyzeAsync();

        Assert.True(workflow.AnalyzeSettings.OptimizeYmtUsage);
        Assert.True(settingsStore.LastSavedProject.OptimizeYmtUsage);
    }

    [Fact]
    public async Task SingleResourceCopyModeApplyCopiesResourceFolderInsideOutputRoot()
    {
        var root = CreateTempDirectory("gui-single-resource-copy-apply");
        var resourceRoot = Path.Combine(root, "gang_flags");
        var outputRoot = Path.Combine(root, "output");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), resourceRoot);
        Directory.CreateDirectory(outputRoot);
        await File.WriteAllTextAsync(
            Path.Combine(resourceRoot, "stream", "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd"),
            "drawable");

        var vm = new MainWindowViewModel(new WorkflowRunner(new RepackerServiceFactory()), new FakeSettingsStore());
        vm.AddResourceFolders([resourceRoot]);
        vm.OutputPath = outputRoot;
        vm.BackupRoot = Path.Combine(root, "backups");

        await vm.AnalyzeAsync();
        await vm.BuildPreviewAsync();
        await vm.ApplyAsync();

        Assert.Empty(vm.Errors);
        Assert.True(File.Exists(Path.Combine(resourceRoot, "fxmanifest.lua")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "gang_flags", "fxmanifest.lua")));
        Assert.True(Directory.Exists(Path.Combine(outputRoot, "zz_merged_clothing_meta")));
        Assert.False(File.Exists(Path.Combine(outputRoot, "fxmanifest.lua")));
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

    [Fact]
    public async Task LoadingRestoreManifestPopulatesRestorePreview()
    {
        var root = CreateTempDirectory("gui-restore-preview");
        var manifest = Path.Combine(root, "backup-manifest.json");
        File.WriteAllText(manifest, "[]");
        var backup = Path.Combine(root, "backups", "source.ymt");
        var original = Path.Combine(root, "resources", "source.ymt");
        var workflow = new FakeWorkflow
        {
            RestorePreview = new RestoreManifestPreview(
                manifest,
                [new BackupEntry("old-ymt", original, backup, null, "before", "after", DateTimeOffset.UtcNow)],
                [new RestoreAction("copy-backup-file", "Restore source", backup, original, new BackupEntry("old-ymt", original, backup, null, "before", "after", DateTimeOffset.UtcNow))],
                []),
        };
        var vm = CreateViewModel(workflow);

        vm.RestoreManifestPath = manifest;
        await WaitForAsync(() => vm.HasRestoreManifestPreview);

        Assert.Equal(5, vm.SelectedTabIndex);
        Assert.Contains(vm.RestoreSummaryLines, line => line == "Actions: 1");
        Assert.Contains(vm.RestoreActionLines, line => line.Contains($"{backup} -> {original}", StringComparison.Ordinal));
        Assert.Contains("Actions: 1", vm.RestoreSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRemoveAndClearResourceFoldersUpdatesAvailability()
    {
        var first = CreateTempDirectory("gui-resource-first");
        var second = CreateTempDirectory("gui-resource-second");
        File.WriteAllText(Path.Combine(first, "fxmanifest.lua"), "fx_version 'cerulean'");
        File.WriteAllText(Path.Combine(second, "fxmanifest.lua"), "fx_version 'cerulean'");
        var vm = CreateViewModel();

        vm.AddResourceFolders([first, second]);

        Assert.True(vm.CanAnalyzeResources);
        Assert.Equal([first, second], vm.ResourcePaths.ToArray());

        vm.SelectedResourcePath = first;
        vm.RemoveSelectedResourceFolder();

        Assert.Equal([second], vm.ResourcePaths.ToArray());

        vm.ClearResourceFolders();

        Assert.Empty(vm.ResourcePaths);
        Assert.False(vm.CanAnalyzeResources);
    }

    [Fact]
    public async Task MovingResourceFoldersUpdatesAnalyzeOrder()
    {
        var first = CreateTempDirectory("gui-resource-order-first");
        var second = CreateTempDirectory("gui-resource-order-second");
        var third = CreateTempDirectory("gui-resource-order-third");
        File.WriteAllText(Path.Combine(first, "fxmanifest.lua"), "fx_version 'cerulean'");
        File.WriteAllText(Path.Combine(second, "fxmanifest.lua"), "fx_version 'cerulean'");
        File.WriteAllText(Path.Combine(third, "fxmanifest.lua"), "fx_version 'cerulean'");
        var workflow = new FakeWorkflow();
        var vm = CreateViewModel(workflow);

        vm.AddResourceFolders([first, second, third]);
        vm.SelectedResourcePath = third;
        vm.MoveSelectedResourceFolderUp();
        vm.MoveSelectedResourceFolderUp();

        Assert.Equal([third, first, second], vm.ResourcePaths.ToArray());
        Assert.Equal(third, vm.ResourcesPath);
        Assert.False(vm.CanMoveResourceUp);
        Assert.True(vm.CanMoveResourceDown);

        await vm.AnalyzeAsync();

        Assert.Equal([third, first, second], workflow.AnalyzeResourceFolders);
    }

    [Fact]
    public void AddingResourcesRootExpandsImmediateChildrenWithManifests()
    {
        var root = CreateTempDirectory("gui-resource-root");
        var first = Path.Combine(root, "first_pack");
        var second = Path.Combine(root, "second_pack");
        var ignored = Path.Combine(root, "not_a_resource");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(ignored);
        File.WriteAllText(Path.Combine(first, "fxmanifest.lua"), "fx_version 'cerulean'");
        File.WriteAllText(Path.Combine(second, "__resource.lua"), "resource_manifest_version '44febabe-d386-4d18-afbe-5e627f4af937'");
        var vm = CreateViewModel();

        vm.AddResourceFolders([root]);

        Assert.Equal([first, second], vm.ResourcePaths.ToArray());
        Assert.True(vm.CanAnalyzeResources);
    }

    [Fact]
    public void AddingResourcesRootExpandsBracketCategoryFolders()
    {
        var root = CreateTempDirectory("gui-bracket-resource-root");
        var category = Path.Combine(root, "[clothing]");
        var first = Path.Combine(category, "first_pack");
        var second = Path.Combine(category, "second_pack");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "fxmanifest.lua"), "fx_version 'cerulean'");
        File.WriteAllText(Path.Combine(second, "__resource.lua"), "resource_manifest_version '44febabe-d386-4d18-afbe-5e627f4af937'");
        var vm = CreateViewModel();

        vm.AddResourceFolders([root]);

        Assert.Equal([first, second], vm.ResourcePaths.ToArray());
        Assert.True(vm.CanAnalyzeResources);
    }

    [Fact]
    public void AddingSingleResourceFolderWithManifestAddsThatFolder()
    {
        var resource = CreateTempDirectory("gui-single-resource");
        File.WriteAllText(Path.Combine(resource, "fxmanifest.lua"), "fx_version 'cerulean'");
        var vm = CreateViewModel();

        vm.AddResourceFolders([resource]);

        Assert.Equal([resource], vm.ResourcePaths.ToArray());
    }

    [Fact]
    public void AddingResourceFoldersFromDroppedTextAcceptsPathsAndFileUris()
    {
        var first = CreateTempDirectory("gui-dropped-text-first");
        var second = CreateTempDirectory("gui-dropped-text-second");
        File.WriteAllText(Path.Combine(first, "fxmanifest.lua"), "fx_version 'cerulean'");
        File.WriteAllText(Path.Combine(second, "__resource.lua"), "resource_manifest_version '44febabe-d386-4d18-afbe-5e627f4af937'");
        var text = $"\"{first}\"{Environment.NewLine}{new Uri(second).AbsoluteUri}";
        var vm = CreateViewModel();

        vm.AddResourceFoldersFromText(text);

        Assert.Equal([first, second], vm.ResourcePaths.ToArray());
        Assert.True(vm.CanAnalyzeResources);
    }

    [Fact]
    public async Task LastProjectLoadsAndOpenProjectPersistsResourceList()
    {
        var root = CreateTempDirectory("gui-settings-root");
        var generatedRoot = CreateTempDirectory("gui-settings-generated");
        var settingsStore = new FakeSettingsStore(new ProjectSettings
        {
            ResourcePaths = [root],
            GeneratedResourcesRoot = generatedRoot,
        }, "/tmp/project.json");
        var vm = new MainWindowViewModel(new FakeWorkflow(), settingsStore);

        await Task.Delay(50);

        Assert.Equal([root], vm.ResourcePaths.ToArray());
        Assert.Equal(generatedRoot, vm.GeneratedResourcesRoot);
        Assert.Equal("/tmp/project.json", vm.CurrentProjectPath);

        await vm.AnalyzeAsync();

        Assert.Equal("/tmp/project.json", settingsStore.LastSavedProjectPath);
        Assert.Equal([root], settingsStore.LastSavedProject.ResourcePaths);
        Assert.Equal(generatedRoot, settingsStore.LastSavedProject.GeneratedResourcesRoot);
        Assert.False(settingsStore.LastSavedProject.IncludeYmtXml);
        Assert.False(settingsStore.LastSavedProject.IncludeDebugClient);
        Assert.True(settingsStore.LastSavedProject.CopyResourcesToOutputBeforeRename);
    }

    [Fact]
    public async Task ClearProjectRestoresDefaultsAndForgetsLastProject()
    {
        var root = CreateTempDirectory("gui-clear-project");
        var settingsStore = new FakeSettingsStore(new ProjectSettings
        {
            ResourcePaths = [root],
            TargetResource = "custom_target",
            SavePlan = false,
            CopyResourcesToOutputBeforeRename = false,
        }, "/tmp/project.json");
        var vm = new MainWindowViewModel(new FakeWorkflow(), settingsStore);

        await Task.Delay(50);
        await vm.ClearProjectAsync();

        Assert.Empty(vm.CurrentProjectPath);
        Assert.Empty(vm.ResourcePaths);
        Assert.Equal("zz_merged_clothing_meta", vm.TargetResource);
        Assert.True(vm.SavePlan);
        Assert.True(vm.CopyResourcesToOutputBeforeRename);
        Assert.Null(settingsStore.LastSavedLastProjectPath);
    }

    [Fact]
    public async Task UpdateCheckShowsAvailableReleaseLink()
    {
        var updateChecker = new FakeUpdateChecker(new VersionCheckResult(
            AppVersion.FromInformationalVersion("1.0.0"),
            AppVersion.FromInformationalVersion("1.0.1"),
            "https://example.test/releases/1.0.1"));
        var vm = new MainWindowViewModel(new FakeWorkflow(), new FakeSettingsStore(), updateChecker);

        await WaitForAsync(() => vm.IsUpdateAvailable);

        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal("https://example.test/releases/1.0.1", vm.UpdateReleaseUrl);
        Assert.Contains("latest 1.0.1", vm.VersionCheckText);
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
                new SourceYmtSummary("pack_a", "/tmp/a.ymt", "mp_f_freemode_01", PedGender.Female, "pack_a", "mp_f_freemode_01_pack_a", "hash_00000000", new Dictionary<int, int> { [11] = 2 }, []),
                new SourceYmtSummary("pack_b", "/tmp/b.ymt", "mp_m_freemode_01", PedGender.Male, "pack_b", "mp_m_freemode_01_pack_b", "hash_00000000", [], []),
            ],
            TargetCollections =
            [
                new TargetCollectionPlan(
                    "merged_f_001",
                    "mp_f_freemode_01_merged_f_001",
                    PedGender.Female,
                    "zz_merged_clothing_meta/stream/mp_f_freemode_01_merged_f_001.ymt",
                    ["/tmp/a.ymt"],
                    [],
                    [],
                    new Dictionary<int, int> { [11] = 2 },
                    []),
            ],
            DrawableMappings =
            [
                new DrawableMapping("pack_a", "/tmp/a.ymt", "pack_a", "mp_f_freemode_01_pack_a", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 11, 0, 0),
                new DrawableMapping("pack_a", "/tmp/a.ymt", "pack_a", "mp_f_freemode_01_pack_a", "merged_f_001", "mp_f_freemode_01_merged_f_001", "mp_f_freemode_01", 11, 1, 1),
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class FakeSettingsStore : IProjectStore
    {
        private readonly ProjectSettings _settings;
        private readonly string _projectPath;

        public FakeSettingsStore(ProjectSettings? settings = null, string projectPath = "")
        {
            _settings = settings ?? new ProjectSettings();
            _projectPath = projectPath;
        }

        public string LastSavedProjectPath { get; private set; } = string.Empty;
        public string? LastSavedLastProjectPath { get; private set; }
        public ProjectSettings LastSavedProject { get; private set; } = new();

        public Task<ProjectLoadResult> LoadLastProjectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProjectLoadResult(_settings, _projectPath));

        public Task<ProjectSettings> LoadProjectAsync(string projectPath, CancellationToken cancellationToken = default)
            => Task.FromResult(_settings);

        public Task SaveProjectAsync(string projectPath, ProjectSettings settings, CancellationToken cancellationToken = default)
        {
            LastSavedProjectPath = projectPath;
            LastSavedProject = settings;
            LastSavedLastProjectPath = projectPath;
            return Task.CompletedTask;
        }

        public Task SaveLastProjectPathAsync(string? projectPath, CancellationToken cancellationToken = default)
        {
            LastSavedLastProjectPath = projectPath;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkflow : IRepackerWorkflow
    {
        public TaskCompletionSource ExportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockExportUntilCanceled { get; init; }
        public AnalyzeResult AnalyzeResult { get; init; } = BuildAnalyzeResult(errorCount: 0, warningCount: 0);
        public BuildResult BuildResult { get; init; } = new(Path.GetTempPath(), []);
        public Exception? BuildException { get; init; }
        public RestoreManifestPreview RestorePreview { get; init; } = new(string.Empty, [], [], []);

        public async Task<ExportXmlResult> ExportXmlAsync(string folderPath, bool overwrite, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            ExportStarted.TrySetResult();
            if (BlockExportUntilCanceled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ExportXmlResult(folderPath, [], []);
        }

        public IReadOnlyList<string> AnalyzeResourceFolders { get; private set; } = [];
        public string AnalyzeGeneratedResourcesRoot { get; private set; } = string.Empty;
        public MergePlanSettings AnalyzeSettings { get; private set; } = new();
        public BuildOptions BuildOptions { get; private set; } = new();
        public ApplyOptions ApplyOptions { get; private set; } = new();

        public Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<string> resourceFolders, string generatedResourcesRoot, string targetResource, MergePlanSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            AnalyzeResourceFolders = resourceFolders.ToList();
            AnalyzeGeneratedResourcesRoot = generatedResourcesRoot;
            AnalyzeSettings = settings;
            return Task.FromResult(AnalyzeResult);
        }

        public Task SavePlanAsync(MergePlan plan, string outputPath, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public async Task<BuildResult> BuildAsync(MergePlan plan, string outputRoot, BuildOptions options, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            BuildOptions = options;
            progress.Report(new OperationProgress("build", "build-target", 1, 1, Path.Combine(outputRoot, "broken.ymt"), "Building target collection broken."));
            await Task.Yield();
            if (BuildException is not null)
            {
                throw BuildException;
            }

            return BuildResult;
        }

        public Task<IReadOnlyList<BackupEntry>> ApplyAsync(MergePlan plan, string backupRoot, ApplyOptions options, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            ApplyOptions = options;
            return Task.FromResult<IReadOnlyList<BackupEntry>>([]);
        }

        public Task<RestoreManifestPreview> LoadRestoreManifestPreviewAsync(string backupManifestPath, CancellationToken cancellationToken)
            => Task.FromResult(RestorePreview with { ManifestPath = backupManifestPath });

        public Task RestoreAsync(string backupManifestPath, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeUpdateChecker : IUpdateChecker
    {
        private readonly VersionCheckResult? _result;

        public FakeUpdateChecker(VersionCheckResult? result)
        {
            _result = result;
        }

        public Task<VersionCheckResult?> CheckAsync(AppVersion currentVersion, CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }
}
