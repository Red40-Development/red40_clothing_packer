using System.Collections.ObjectModel;
using ClothingRepacker.Core;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Gui.Models;
using ClothingRepacker.Gui.Services;

namespace ClothingRepacker.Gui.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IRepackerWorkflow _workflow;
    private readonly IRecentSettingsStore _settingsStore;
    private CancellationTokenSource? _operationCts;
    private MergePlan? _lastPlan;
    private bool _hasSuccessfulBuild;

    private string _resourcesPath = string.Empty;
    private string _outputPath = string.Empty;
    private string _backupRoot = string.Empty;
    private string _planPath = string.Empty;
    private string _restoreManifestPath = string.Empty;
    private string _targetResource = "zz_merged_clothing_meta";
    private string _targetPrefix = "merged";
    private string _femalePrefix = "merged_f";
    private string _malePrefix = "merged_m";
    private int _maxDrawablesPerComponent = ClothingConstants.DefaultMaxDrawablesPerComponent;
    private int _maxDrawablesPerProp = ClothingConstants.DefaultMaxDrawablesPerProp;
    private bool _includeYmtXml = true;
    private bool _includeDebugClient = true;
    private bool _overwriteXml;
    private bool _savePlan = true;
    private bool _isBusy;
    private string _status = "Select a clothing resources folder to begin.";
    private string _currentStage = "Idle";
    private string _activePath = string.Empty;
    private int _progressCurrent;
    private int _progressTotal;
    private WorkflowSummary _summary = new(0, 0, 0, 0, 0);

    public MainWindowViewModel(IRepackerWorkflow workflow, IRecentSettingsStore settingsStore)
    {
        _workflow = workflow;
        _settingsStore = settingsStore;

        ExportXmlCommand = new AsyncRelayCommand(ExportXmlAsync, CanRunWithResources);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, CanRunWithResources);
        BuildPreviewCommand = new AsyncRelayCommand(BuildPreviewAsync, CanBuildPreview);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApply);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, CanRestore);
        CancelCommand = new RelayCommand(CancelOperation, () => IsBusy);

        _ = LoadSettingsAsync();
    }

    public AsyncRelayCommand ExportXmlCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand BuildPreviewCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand CancelCommand { get; }

    public ObservableCollection<string> SummaryLines { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public ObservableCollection<string> Files { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    public string ResourcesPath
    {
        get => _resourcesPath;
        set
        {
            if (SetProperty(ref _resourcesPath, value))
            {
                EnsureDerivedPaths();
                ResetPlanState();
                RefreshCommands();
            }
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        set
        {
            if (SetProperty(ref _outputPath, value))
            {
                _hasSuccessfulBuild = false;
                RefreshCommands();
            }
        }
    }

    public string BackupRoot
    {
        get => _backupRoot;
        set
        {
            if (SetProperty(ref _backupRoot, value))
            {
                RefreshCommands();
            }
        }
    }

    public string PlanPath
    {
        get => _planPath;
        set => SetProperty(ref _planPath, value);
    }

    public string RestoreManifestPath
    {
        get => _restoreManifestPath;
        set
        {
            if (SetProperty(ref _restoreManifestPath, value))
            {
                RefreshCommands();
            }
        }
    }

    public string TargetResource
    {
        get => _targetResource;
        set
        {
            if (SetProperty(ref _targetResource, value))
            {
                ResetPlanState();
            }
        }
    }

    public string TargetPrefix
    {
        get => _targetPrefix;
        set
        {
            if (SetProperty(ref _targetPrefix, value))
            {
                FemalePrefix = $"{value}_f";
                MalePrefix = $"{value}_m";
                ResetPlanState();
            }
        }
    }

    public string FemalePrefix
    {
        get => _femalePrefix;
        set
        {
            if (SetProperty(ref _femalePrefix, value))
            {
                ResetPlanState();
            }
        }
    }

    public string MalePrefix
    {
        get => _malePrefix;
        set
        {
            if (SetProperty(ref _malePrefix, value))
            {
                ResetPlanState();
            }
        }
    }

    public int MaxDrawablesPerComponent
    {
        get => _maxDrawablesPerComponent;
        set
        {
            if (SetProperty(ref _maxDrawablesPerComponent, Math.Max(1, value)))
            {
                ResetPlanState();
            }
        }
    }

    public int MaxDrawablesPerProp
    {
        get => _maxDrawablesPerProp;
        set
        {
            if (SetProperty(ref _maxDrawablesPerProp, Math.Max(1, value)))
            {
                ResetPlanState();
            }
        }
    }

    public bool IncludeYmtXml
    {
        get => _includeYmtXml;
        set => SetProperty(ref _includeYmtXml, value);
    }

    public bool IncludeDebugClient
    {
        get => _includeDebugClient;
        set => SetProperty(ref _includeDebugClient, value);
    }

    public bool OverwriteXml
    {
        get => _overwriteXml;
        set => SetProperty(ref _overwriteXml, value);
    }

    public bool SavePlan
    {
        get => _savePlan;
        set => SetProperty(ref _savePlan, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string CurrentStage
    {
        get => _currentStage;
        private set => SetProperty(ref _currentStage, value);
    }

    public string ActivePath
    {
        get => _activePath;
        private set => SetProperty(ref _activePath, value);
    }

    public int ProgressCurrent
    {
        get => _progressCurrent;
        private set
        {
            if (SetProperty(ref _progressCurrent, value))
            {
                OnPropertyChanged(nameof(ProgressValue));
            }
        }
    }

    public int ProgressTotal
    {
        get => _progressTotal;
        private set
        {
            if (SetProperty(ref _progressTotal, value))
            {
                OnPropertyChanged(nameof(ProgressValue));
                OnPropertyChanged(nameof(HasDeterminateProgress));
            }
        }
    }

    public double ProgressValue => ProgressTotal <= 0 ? 0 : (double)ProgressCurrent / ProgressTotal * 100;
    public bool HasDeterminateProgress => ProgressTotal > 0;
    public bool CanExportXml => CanRunWithResources();
    public bool CanAnalyzeResources => CanRunWithResources();
    public bool CanBuildPreviewPlan => CanBuildPreview();
    public bool CanApplyPlan => CanApply();
    public bool CanRestoreBackup => CanRestore();

    public WorkflowSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public int PlannedBackupCount => (_lastPlan?.TargetCollections.SelectMany(target => target.SourceYmts).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0)
        + (_lastPlan?.BrokenCreatureMetadataBackups.Count ?? 0);

    public void SelectResourcesFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ResourcesPath = path;
        Status = "Resources folder selected. Run Analyze to preview changes.";
    }

    public async Task ExportXmlAsync()
        => await RunOperationAsync("Exporting XML", async (progress, token) =>
        {
            var result = await _workflow.ExportXmlAsync(ResourcesPath, OverwriteXml, progress, token);
            Files.Clear();
            foreach (var file in result.WrittenFiles.Concat(result.SkippedFiles))
            {
                Files.Add(file);
            }

            Summary = new WorkflowSummary(0, 0, 0, 0, 0, result.WrittenFiles.Count, result.SkippedFiles.Count);
            SetSummaryLines("XML export", Summary);
            Status = $"XML export complete. Wrote {result.WrittenFiles.Count} file(s), skipped {result.SkippedFiles.Count}.";
        });

    public async Task AnalyzeAsync()
        => await RunOperationAsync("Analyzing resources", async (progress, token) =>
        {
            var settings = new MergePlanSettings
            {
                TargetPrefix = TargetPrefix,
                FemalePrefix = FemalePrefix,
                MalePrefix = MalePrefix,
                MaxDrawablesPerComponent = MaxDrawablesPerComponent,
                MaxDrawablesPerProp = MaxDrawablesPerProp,
            };

            var result = await _workflow.AnalyzeAsync(ResourcesPath, TargetResource, settings, progress, token);
            _lastPlan = result.Plan;
            _hasSuccessfulBuild = false;

            if (SavePlan && !string.IsNullOrWhiteSpace(PlanPath))
            {
                await _workflow.SavePlanAsync(result.Plan, PlanPath, token);
                Files.Add(PlanPath);
            }

            Warnings.Clear();
            Errors.Clear();
            foreach (var warning in result.Plan.Warnings)
            {
                Warnings.Add(warning);
            }

            foreach (var error in result.Plan.Errors)
            {
                Errors.Add(error);
            }

            Summary = new WorkflowSummary(
                result.Plan.SourceYmts.Count,
                result.Plan.TargetCollections.Count,
                result.Plan.StreamRenames.Count,
                result.Plan.Warnings.Count,
                result.Plan.Errors.Count);
            SetSummaryLines("Analyze", Summary);
            Status = result.Plan.Errors.Count == 0
                ? "Analyze complete. Build Preview is available."
                : $"Analyze complete with {result.Plan.Errors.Count} error(s). Fix errors before build/apply.";
            RefreshCommands();
        });

    public async Task BuildPreviewAsync()
        => await RunOperationAsync("Building preview", async (progress, token) =>
        {
            if (_lastPlan is null)
            {
                throw new InvalidOperationException("Analyze must complete before building.");
            }

            var result = await _workflow.BuildAsync(_lastPlan, OutputPath, new BuildOptions
            {
                IncludeYmtXml = IncludeYmtXml,
                IncludeDebugClient = IncludeDebugClient,
            }, progress, token);

            Files.Clear();
            foreach (var file in result.WrittenFiles)
            {
                Files.Add(file);
            }

            _hasSuccessfulBuild = true;
            Summary = Summary with { WrittenFileCount = result.WrittenFiles.Count };
            SetSummaryLines("Build Preview", Summary);
            Status = $"Build preview complete. Wrote {result.WrittenFiles.Count} file(s) to {result.OutputRoot}.";
            RefreshCommands();
        });

    public async Task ApplyAsync()
        => await RunOperationAsync("Applying plan", async (progress, token) =>
        {
            if (_lastPlan is null)
            {
                throw new InvalidOperationException("Analyze must complete before applying.");
            }

            var entries = await _workflow.ApplyAsync(_lastPlan, BackupRoot, progress, token);
            Summary = Summary with { BackupEntryCount = entries.Count };
            SetSummaryLines("Apply", Summary);
            Status = $"Apply complete. Created {entries.Count} backup manifest entr{(entries.Count == 1 ? "y" : "ies")}.";
        });

    public async Task RestoreAsync()
        => await RunOperationAsync("Restoring backup", async (_, token) =>
        {
            await _workflow.RestoreAsync(RestoreManifestPath, token);
            Status = "Restore complete.";
            LogLines.Add($"Restored from {RestoreManifestPath}");
        });

    public void CancelOperation()
    {
        _operationCts?.Cancel();
        Status = "Cancel requested. Waiting for the current step to stop.";
    }

    public bool CanRunWithResources()
        => !IsBusy && Directory.Exists(ResourcesPath);

    public bool CanBuildPreview()
        => !IsBusy
           && _lastPlan is not null
           && _lastPlan.Errors.Count == 0
           && !string.IsNullOrWhiteSpace(OutputPath);

    public bool CanApply()
        => !IsBusy
           && _hasSuccessfulBuild
           && _lastPlan is not null
           && _lastPlan.Errors.Count == 0
           && !string.IsNullOrWhiteSpace(BackupRoot);

    public bool CanRestore()
        => !IsBusy && File.Exists(RestoreManifestPath);

    private async Task RunOperationAsync(string status, Func<IProgress<OperationProgress>, CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = status;
        CurrentStage = "Starting";
        ActivePath = string.Empty;
        ProgressCurrent = 0;
        ProgressTotal = 0;
        _operationCts = new CancellationTokenSource();
        var progress = new Progress<OperationProgress>(ApplyProgress);

        try
        {
            await operation(progress, _operationCts.Token);
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "Operation canceled.";
            LogLines.Add("Operation canceled.");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Errors.Add(ex.Message);
            LogLines.Add($"Error: {ex.Message}");
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            IsBusy = false;
            CurrentStage = "Idle";
            RefreshCommands();
        }
    }

    private void ApplyProgress(OperationProgress progress)
    {
        CurrentStage = $"{progress.Operation}: {progress.Stage}";
        ActivePath = progress.Path ?? progress.Message ?? string.Empty;
        ProgressCurrent = progress.Current;
        ProgressTotal = progress.Total;

        var line = FormatProgress(progress);
        if (!string.IsNullOrWhiteSpace(line))
        {
            LogLines.Add(line);
        }
    }

    private static string FormatProgress(OperationProgress progress)
    {
        var path = string.IsNullOrWhiteSpace(progress.Path) ? string.Empty : $" | {Path.GetFileName(progress.Path)}";
        return progress.Stage switch
        {
            "start" => progress.Message ?? $"{progress.Operation} started.",
            "process-source" => $"Analyzed {progress.Current}/{progress.Total} | sources {progress.SourceCount} | warnings {progress.WarningCount} | errors {progress.ErrorCount}{path}",
            "write-target" => $"Built {progress.Current}/{progress.Total} target collections | files {progress.WrittenFileCount}{path}",
            "export-file" => $"Exported {progress.Current}/{progress.Total} | written {progress.WrittenFileCount} | skipped {progress.SkippedCount}{path}",
            "rename-stream" => $"Renamed {progress.Current}/{progress.Total} stream files{path}",
            "backup-source-ymt" => $"Backed up {progress.Current}/{progress.Total} source files{path}",
            "complete" => progress.Message ?? $"{progress.Operation} complete.",
            _ => progress.Message ?? $"{progress.Operation}: {progress.Stage}{path}",
        };
    }

    private void EnsureDerivedPaths()
    {
        if (string.IsNullOrWhiteSpace(ResourcesPath))
        {
            return;
        }

        var parent = Directory.GetParent(ResourcesPath)?.FullName ?? ResourcesPath;
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = parent;
        }

        if (string.IsNullOrWhiteSpace(BackupRoot))
        {
            BackupRoot = Path.Combine(parent, "backups");
        }

        if (string.IsNullOrWhiteSpace(PlanPath))
        {
            PlanPath = Path.Combine(ResourcesPath, "plan.json");
        }
    }

    private void ResetPlanState()
    {
        _lastPlan = null;
        _hasSuccessfulBuild = false;
        Summary = new WorkflowSummary(0, 0, 0, 0, 0);
        SummaryLines.Clear();
        Warnings.Clear();
        Errors.Clear();
        Files.Clear();
        RefreshCommands();
    }

    private void SetSummaryLines(string title, WorkflowSummary summary)
    {
        SummaryLines.Clear();
        SummaryLines.Add($"{title} summary");
        SummaryLines.Add($"Source YMTs: {summary.SourceYmtCount}");
        SummaryLines.Add($"Target collections: {summary.TargetCollectionCount}");
        SummaryLines.Add($"Stream renames: {summary.StreamRenameCount}");
        SummaryLines.Add($"Warnings: {summary.WarningCount}");
        SummaryLines.Add($"Errors: {summary.ErrorCount}");
        if (summary.WrittenFileCount > 0)
        {
            SummaryLines.Add($"Written files: {summary.WrittenFileCount}");
        }

        if (summary.SkippedFileCount > 0)
        {
            SummaryLines.Add($"Skipped files: {summary.SkippedFileCount}");
        }

        if (summary.BackupEntryCount > 0)
        {
            SummaryLines.Add($"Backup entries: {summary.BackupEntryCount}");
        }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            _resourcesPath = settings.ResourcesPath;
            _outputPath = settings.OutputPath;
            _backupRoot = settings.BackupRoot;
            _planPath = settings.PlanPath;
            _targetResource = settings.TargetResource;
            _targetPrefix = settings.TargetPrefix;
            _femalePrefix = settings.FemalePrefix;
            _malePrefix = settings.MalePrefix;
            _maxDrawablesPerComponent = settings.MaxDrawablesPerComponent;
            _maxDrawablesPerProp = settings.MaxDrawablesPerProp;
            _includeYmtXml = settings.IncludeYmtXml;
            _includeDebugClient = settings.IncludeDebugClient;
            _overwriteXml = settings.OverwriteXml;
            _savePlan = settings.SavePlan;
            OnPropertyChanged(string.Empty);
            EnsureDerivedPaths();
            RefreshCommands();
        }
        catch (Exception ex)
        {
            LogLines.Add($"Could not load recent settings: {ex.Message}");
        }
    }

    private async Task SaveSettingsAsync()
        => await _settingsStore.SaveAsync(new RecentSettings
        {
            ResourcesPath = ResourcesPath,
            OutputPath = OutputPath,
            BackupRoot = BackupRoot,
            PlanPath = PlanPath,
            TargetResource = TargetResource,
            TargetPrefix = TargetPrefix,
            FemalePrefix = FemalePrefix,
            MalePrefix = MalePrefix,
            MaxDrawablesPerComponent = MaxDrawablesPerComponent,
            MaxDrawablesPerProp = MaxDrawablesPerProp,
            IncludeYmtXml = IncludeYmtXml,
            IncludeDebugClient = IncludeDebugClient,
            OverwriteXml = OverwriteXml,
            SavePlan = SavePlan,
        });

    private void RefreshCommands()
    {
        ExportXmlCommand.RaiseCanExecuteChanged();
        AnalyzeCommand.RaiseCanExecuteChanged();
        BuildPreviewCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(PlannedBackupCount));
        OnPropertyChanged(nameof(CanExportXml));
        OnPropertyChanged(nameof(CanAnalyzeResources));
        OnPropertyChanged(nameof(CanBuildPreviewPlan));
        OnPropertyChanged(nameof(CanApplyPlan));
        OnPropertyChanged(nameof(CanRestoreBackup));
    }
}
