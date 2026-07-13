using System.Collections.ObjectModel;
using System.Reflection;
using ClothingRepacker.Core;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Reporting;
using ClothingRepacker.Core.Scanning;
using ClothingRepacker.Core.Localization;
using ClothingRepacker.Gui.Models;
using ClothingRepacker.Gui.Services;

namespace ClothingRepacker.Gui.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const string DefaultCopyOutputFolderName = "red40_output";

    private readonly IRepackerWorkflow _workflow;
    private readonly IProjectStore _projectStore;
    private readonly IUpdateChecker? _updateChecker;
    private readonly LocalizationService _localization;
    private CancellationTokenSource? _operationCts;
    private MergePlan? _lastPlan;
    private bool _hasSuccessfulBuild;

    private string _currentProjectPath = string.Empty;
    private string _resourcesPath = string.Empty;
    private string? _selectedResourcePath;
    private string _outputPath = string.Empty;
    private string _generatedResourcesRoot = string.Empty;
    private string _backupRoot = string.Empty;
    private string _planPath = string.Empty;
    private string _restoreManifestPath = string.Empty;
    private string _targetResource = "zz_merged_clothing_meta";
    private string _targetPrefix = "merged";
    private string _femalePrefix = "merged_f";
    private string _malePrefix = "merged_m";
    private int _maxDrawablesPerComponent = ClothingConstants.DefaultMaxDrawablesPerComponent;
    private int _maxDrawablesPerProp = ClothingConstants.DefaultMaxDrawablesPerProp;
    private bool _includeYmtXml;
    private bool _includeDebugClient;
    private bool _overwriteXml;
    private bool _savePlan = true;
    private bool _copyResourcesToOutputBeforeRename = true;
    private bool _optimizeYmtUsage;
    private bool _isBusy;
    private string _status = string.Empty;
    private string _versionCheckText;
    private string _updateReleaseUrl = string.Empty;
    private bool _isUpdateAvailable;
    private string _currentStage = string.Empty;
    private string _activePath = string.Empty;
    private int _progressCurrent;
    private int _progressTotal;
    private int _selectedTabIndex;
    private WorkflowSummary _summary = new(0, 0, 0, 0, 0);
    private YmtRepackReport? _repackReport;
    private RestoreManifestPreview? _restorePreview;
    private int _restoreManifestLoadVersion;
    private IReadOnlyList<string> _restoreSummaryLines = [];
    private IReadOnlyList<string> _restoreActionLines = [];
    private string _restoreSummaryText = string.Empty;
    private LocalizationOption? _selectedLanguage;

    public MainWindowViewModel(IRepackerWorkflow workflow, IProjectStore projectStore, IUpdateChecker? updateChecker = null)
    {
        _workflow = workflow;
        _projectStore = projectStore;
        _updateChecker = updateChecker;
        _localization = new LocalizationService();
        _localization.LanguageChanged += Localization_LanguageChanged;
        LanguageOptions = _localization.GetOptions();
        _selectedLanguage = LanguageOptions[0];
        CurrentVersion = AppVersion.FromInformationalVersion(
            typeof(MainWindowViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        _versionCheckText = T("version.current", Args(("version", CurrentVersion.Display)));
        _status = T("status.initial");
        _currentStage = T("status.idle");

        ExportXmlCommand = new AsyncRelayCommand(ExportXmlAsync, CanRunWithResources);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, CanRunWithResources);
        BuildPreviewCommand = new AsyncRelayCommand(BuildPreviewAsync, CanBuildPreview);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApply);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, CanRestore);
        CancelCommand = new RelayCommand(CancelOperation, () => IsBusy);
        HelpCommand = new RelayCommand(RequestHelp);
        MoveResourceUpCommand = new RelayCommand(MoveSelectedResourceFolderUp, CanMoveSelectedResourceFolderUp);
        MoveResourceDownCommand = new RelayCommand(MoveSelectedResourceFolderDown, CanMoveSelectedResourceFolderDown);

        _ = LoadLastProjectAsync();
        _ = CheckForUpdatesAsync();
    }

    public AsyncRelayCommand ExportXmlCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand BuildPreviewCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand HelpCommand { get; }
    public RelayCommand MoveResourceUpCommand { get; }
    public RelayCommand MoveResourceDownCommand { get; }

    public event EventHandler? HelpRequested;

    public LocalizationService Localization => _localization;
    public IReadOnlyList<LocalizationOption> LanguageOptions { get; private set; }

    public LocalizationOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value))
            {
                return;
            }

            _localization.OverrideLocale = value is { IsSystemDefault: false } ? value.Locale : null;
            _ = _projectStore.SaveLanguageOverrideAsync(_localization.OverrideLocale);
        }
    }

    public ObservableCollection<string> SummaryLines { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public ObservableCollection<string> Files { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<string> RepackReportLines { get; } = [];
    public ObservableCollection<string> ResourcePaths { get; } = [];

    public AppVersion CurrentVersion { get; }

    public string VersionCheckText
    {
        get => _versionCheckText;
        private set => SetProperty(ref _versionCheckText, value);
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetProperty(ref _isUpdateAvailable, value);
    }

    public string UpdateReleaseUrl
    {
        get => _updateReleaseUrl;
        private set => SetProperty(ref _updateReleaseUrl, value);
    }

    public string CurrentProjectPath
    {
        get => _currentProjectPath;
        private set
        {
            if (SetProperty(ref _currentProjectPath, value))
            {
                OnPropertyChanged(nameof(ProjectDisplayName));
                OnPropertyChanged(nameof(HasCurrentProject));
            }
        }
    }

    public string ProjectDisplayName => string.IsNullOrWhiteSpace(CurrentProjectPath)
        ? T("project.unsaved")
        : Path.GetFileNameWithoutExtension(CurrentProjectPath);

    public bool HasCurrentProject => !string.IsNullOrWhiteSpace(CurrentProjectPath);

    public IReadOnlyList<string> RestoreSummaryLines
    {
        get => _restoreSummaryLines;
        private set
        {
            if (SetProperty(ref _restoreSummaryLines, value))
            {
                RefreshRestoreText();
            }
        }
    }

    public IReadOnlyList<string> RestoreActionLines
    {
        get => _restoreActionLines;
        private set => SetProperty(ref _restoreActionLines, value);
    }

    public string RestoreSummaryText
    {
        get => _restoreSummaryText;
        private set => SetProperty(ref _restoreSummaryText, value);
    }

    public string ResourcesPath
    {
        get => _resourcesPath;
        set
        {
            if (SetProperty(ref _resourcesPath, value))
            {
                ReplaceResourceFolders(string.IsNullOrWhiteSpace(value) ? [] : [value]);
            }
        }
    }

    public string? SelectedResourcePath
    {
        get => _selectedResourcePath;
        set
        {
            if (SetProperty(ref _selectedResourcePath, value))
            {
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
                SetProperty(ref _generatedResourcesRoot, value, nameof(GeneratedResourcesRoot));
                ResetPlanState();
            }
        }
    }

    public string GeneratedResourcesRoot
    {
        get => _generatedResourcesRoot;
        set => OutputPath = value;
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
                _ = LoadRestoreManifestPreviewAsync(value);
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

    public bool CopyResourcesToOutputBeforeRename
    {
        get => _copyResourcesToOutputBeforeRename;
        set
        {
            if (SetProperty(ref _copyResourcesToOutputBeforeRename, value))
            {
                ResetPlanState();
            }
        }
    }

    public bool OptimizeYmtUsage
    {
        get => _optimizeYmtUsage;
        set
        {
            if (SetProperty(ref _optimizeYmtUsage, value))
            {
                ResetPlanState();
            }
        }
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
    public bool CanMoveResourceUp => CanMoveSelectedResourceFolderUp();
    public bool CanMoveResourceDown => CanMoveSelectedResourceFolderDown();
    public bool CanEditProject => !IsBusy;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public WorkflowSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public YmtRepackReport? RepackReport
    {
        get => _repackReport;
        private set
        {
            if (SetProperty(ref _repackReport, value))
            {
                OnPropertyChanged(nameof(HasRepackReport));
            }
        }
    }

    public bool HasRepackReport => _repackReport is { } report
        && (report.SegmentCount > 0 || report.CreatureMetadataTargets.Count > 0);

    public bool HasRestoreManifestPreview => _restorePreview is not null;

    public int PlannedBackupCount => (_lastPlan?.TargetCollections.SelectMany(target => target.SourceYmts).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0)
        + (_lastPlan?.BrokenCreatureMetadataBackups.Count ?? 0)
        + (_lastPlan?.SourceAlternateMetadataBackups.Count ?? 0);

    private void RequestHelp()
        => HelpRequested?.Invoke(this, EventArgs.Empty);

    private string T(string key, IReadOnlyDictionary<string, object?>? arguments = null)
        => _localization.Translate(key, arguments);

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] values)
        => values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        var selectedLocale = _selectedLanguage?.Locale;
        LanguageOptions = _localization.GetOptions();
        _selectedLanguage = LanguageOptions.FirstOrDefault(option =>
            string.Equals(option.Locale, selectedLocale, StringComparison.OrdinalIgnoreCase))
            ?? LanguageOptions[0];
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(string.Empty);
        RefreshLocalizedCollections();
    }

    private void RefreshLocalizedCollections()
    {
        if (_lastPlan is not null)
        {
            Warnings.Clear();
            Errors.Clear();
            foreach (var warning in LocalizeDiagnostics(_lastPlan.WarningDiagnostics, _lastPlan.Warnings))
            {
                Warnings.Add(warning);
            }

            foreach (var error in LocalizeDiagnostics(_lastPlan.ErrorDiagnostics, _lastPlan.Errors))
            {
                Errors.Add(error);
            }
            SetSummaryLines(T("summary.analyze"), Summary);
            SetRepackReport(_lastPlan);
        }

        if (_restorePreview is not null)
        {
            SetRestorePreviewLines(_restorePreview);
        }
    }

    private IReadOnlyList<string> LocalizeDiagnostics(
        IReadOnlyList<LocalizedDiagnostic> diagnostics,
        IReadOnlyList<string> legacy)
        => diagnostics.Count > 0
            ? diagnostics.Select(_localization.Translate).ToList()
            : legacy;

    public void SelectResourcesFolder(string path)
    {
        AddResourceFolders([path]);
    }

    public void AddResourceFolders(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in ExpandResourceFolderSelections(paths))
        {
            if (ResourcePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            ResourcePaths.Add(path);
            added = true;
        }

        if (!added)
        {
            return;
        }

        SyncResourcesPath();
        EnsureDerivedPaths();
        ResetPlanState();
        Status = T("status.resourcesSelected", Args(("count", ResourcePaths.Count)));
    }

    public void AddResourceFoldersFromText(string text)
    {
        var paths = ParseResourceFolderText(text);
        AddResourceFolders(paths);
    }

    private static IReadOnlyList<string> ParseResourceFolderText(string text)
    {
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(NormalizeDroppedPathText)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }

    private static string NormalizeDroppedPathText(string text)
    {
        var path = text.Trim().Trim('"', '\'');
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return path;
    }

    private static IReadOnlyList<string> ExpandResourceFolderSelections(IEnumerable<string> paths)
        => ResourceFolderDiscovery.ExpandSelectedResourceFolders(paths);

    public void RemoveSelectedResourceFolder()
    {
        if (SelectedResourcePath is null)
        {
            return;
        }

        ResourcePaths.Remove(SelectedResourcePath);
        SelectedResourcePath = null;
        SyncResourcesPath();
        EnsureDerivedPaths();
        ResetPlanState();
        Status = ResourcePaths.Count == 0
            ? T("status.initial")
            : T("status.resourcesSelectedShort", Args(("count", ResourcePaths.Count)));
    }

    public void MoveSelectedResourceFolderUp()
    {
        MoveSelectedResourceFolder(-1);
    }

    public void MoveSelectedResourceFolderDown()
    {
        MoveSelectedResourceFolder(1);
    }

    public void ClearResourceFolders()
    {
        ResourcePaths.Clear();
        SelectedResourcePath = null;
        SyncResourcesPath();
        ResetPlanState();
        Status = T("status.initial");
    }

    public async Task ExportXmlAsync()
        => await RunOperationAsync(T("operation.exportingXml"), async (progress, token) =>
        {
            var writtenFiles = new List<string>();
            var skippedFiles = new List<string>();
            foreach (var resourcePath in ResourcePaths)
            {
                var result = await _workflow.ExportXmlAsync(resourcePath, OverwriteXml, progress, token);
                writtenFiles.AddRange(result.WrittenFiles);
                skippedFiles.AddRange(result.SkippedFiles);
            }

            Files.Clear();
            foreach (var file in writtenFiles.Concat(skippedFiles))
            {
                Files.Add(file);
            }

            Summary = new WorkflowSummary(0, 0, 0, 0, 0, writtenFiles.Count, skippedFiles.Count);
            SetSummaryLines(T("summary.xmlExport"), Summary);
            Status = T("status.exportComplete", Args(("written", writtenFiles.Count), ("skipped", skippedFiles.Count)));
        });

    public async Task AnalyzeAsync()
        => await RunOperationAsync(T("operation.analyzing"), async (progress, token) =>
        {
            var settings = new MergePlanSettings
            {
                TargetPrefix = TargetPrefix,
                FemalePrefix = FemalePrefix,
                MalePrefix = MalePrefix,
                MaxDrawablesPerComponent = MaxDrawablesPerComponent,
                MaxDrawablesPerProp = MaxDrawablesPerProp,
                OptimizeYmtUsage = OptimizeYmtUsage,
                RenameStreamsInPlace = !CopyResourcesToOutputBeforeRename,
            };

            var result = await _workflow.AnalyzeAsync(ResourcePaths.ToList(), OutputPath, TargetResource, settings, progress, token);
            _lastPlan = result.Plan;
            _hasSuccessfulBuild = false;

            if (SavePlan && !string.IsNullOrWhiteSpace(PlanPath))
            {
                await _workflow.SavePlanAsync(result.Plan, PlanPath, token);
                Files.Add(PlanPath);
            }

            Warnings.Clear();
            Errors.Clear();
            foreach (var warning in LocalizeDiagnostics(result.Plan.WarningDiagnostics, result.Plan.Warnings))
            {
                Warnings.Add(warning);
            }

            foreach (var error in LocalizeDiagnostics(result.Plan.ErrorDiagnostics, result.Plan.Errors))
            {
                Errors.Add(error);
            }

            Summary = new WorkflowSummary(
                result.Plan.SourceYmts.Count,
                result.Plan.TargetCollections.Count,
                result.Plan.StreamRenames.Count,
                result.Plan.Warnings.Count,
                result.Plan.Errors.Count);
            SetRepackReport(result.Plan);
            SetSummaryLines(T("summary.analyze"), Summary);
            SelectedTabIndex = result.Plan.TargetCollections.Count > 0 ? 2 : 1;
            Status = result.Plan.Errors.Count == 0
                ? T("status.analyzeComplete")
                : T("status.analyzeErrors", Args(("count", result.Plan.Errors.Count)));
            RefreshCommands();
        });

    public async Task BuildPreviewAsync()
        => await RunOperationAsync(T("operation.buildingPreview"), async (progress, token) =>
        {
            if (_lastPlan is null)
            {
                throw new InvalidOperationException(T("error.analyzeBeforeBuild"));
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
            SetSummaryLines(T("summary.buildPreview"), Summary);
            Status = T("status.buildComplete", Args(("count", result.WrittenFiles.Count), ("path", result.OutputRoot)));
            RefreshCommands();
        });

    public async Task ApplyAsync()
        => await RunOperationAsync(T("operation.applying"), async (progress, token) =>
        {
            if (_lastPlan is null)
            {
                throw new InvalidOperationException(T("error.analyzeBeforeApply"));
            }

            var entries = await _workflow.ApplyAsync(_lastPlan, BackupRoot, new ApplyOptions
            {
                CopyResourcesToOutputBeforeRename = CopyResourcesToOutputBeforeRename,
                IncludeYmtXml = IncludeYmtXml,
                IncludeDebugClient = IncludeDebugClient,
            }, progress, token);
            Summary = Summary with { BackupEntryCount = entries.Count };
            SetSummaryLines(T("summary.apply"), Summary);
            Status = T("status.applyComplete", Args(("count", entries.Count)));
        });

    public async Task RestoreAsync()
        => await RunOperationAsync(T("operation.restoring"), async (progress, token) =>
        {
            await _workflow.RestoreAsync(RestoreManifestPath, progress, token);
            Status = T("status.restoreComplete");
            LogLines.Add(T("log.restoredFrom", Args(("path", RestoreManifestPath))));
            await LoadRestoreManifestPreviewAsync(RestoreManifestPath, updateStatus: false);
        });

    public async Task LoadProjectAsync(string projectPath)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var settings = await _projectStore.LoadProjectAsync(projectPath);
            ApplyProjectSettings(settings, projectPath);
            await _projectStore.SaveLastProjectPathAsync(projectPath);
            Status = T("status.projectLoaded", Args(("name", Path.GetFileName(projectPath))));
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Errors.Add(ex.Message);
            LogLines.Add(T("log.couldNotLoadProject", Args(("message", ex.Message))));
        }
    }

    public async Task SaveProjectAsync(string projectPath)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            await _projectStore.SaveProjectAsync(projectPath, CreateProjectSettings());
            CurrentProjectPath = projectPath;
            Status = T("status.projectSaved", Args(("name", Path.GetFileName(projectPath))));
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Errors.Add(ex.Message);
            LogLines.Add(T("log.couldNotSaveProject", Args(("message", ex.Message))));
        }
    }

    public async Task SaveCurrentProjectAsync()
    {
        if (!string.IsNullOrWhiteSpace(CurrentProjectPath))
        {
            await SaveProjectAsync(CurrentProjectPath);
        }
    }

    public async Task ClearProjectAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ApplyProjectSettings(new ProjectSettings(), string.Empty);
        await _projectStore.SaveLastProjectPathAsync(null);
        Status = T("status.projectCleared");
    }

    public void CancelOperation()
    {
        _operationCts?.Cancel();
        Status = T("status.cancelRequested");
    }

    public bool CanRunWithResources()
        => !IsBusy && ResourcePaths.Count > 0 && ResourcePaths.All(Directory.Exists) && !string.IsNullOrWhiteSpace(OutputPath);

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

    private bool CanMoveSelectedResourceFolderUp()
        => !IsBusy
           && SelectedResourcePath is not null
           && ResourcePaths.IndexOf(SelectedResourcePath) > 0;

    private bool CanMoveSelectedResourceFolderDown()
    {
        if (IsBusy || SelectedResourcePath is null)
        {
            return false;
        }

        var index = ResourcePaths.IndexOf(SelectedResourcePath);
        return index >= 0 && index < ResourcePaths.Count - 1;
    }

    private void MoveSelectedResourceFolder(int offset)
    {
        if (SelectedResourcePath is not { } selected)
        {
            return;
        }

        var oldIndex = ResourcePaths.IndexOf(selected);
        var newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= ResourcePaths.Count)
        {
            return;
        }

        ResourcePaths.Move(oldIndex, newIndex);
        SelectedResourcePath = selected;
        SyncResourcesPath();
        ResetPlanState();
        Status = T("status.resourceOrderUpdated");
    }

    private async Task RunOperationAsync(string status, Func<IProgress<OperationProgress>, CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = status;
        CurrentStage = T("status.starting");
        ActivePath = string.Empty;
        ProgressCurrent = 0;
        ProgressTotal = 0;
        _operationCts = new CancellationTokenSource();
        var progress = new Progress<OperationProgress>(ApplyProgress);

        try
        {
            await operation(progress, _operationCts.Token);
            await SaveOpenProjectAsync();
        }
        catch (OperationCanceledException)
        {
            Status = T("status.canceled");
            LogLines.Add(T("status.canceled"));
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Errors.Add(ex.Message);
            if (!string.IsNullOrWhiteSpace(ActivePath))
            {
                LogLines.Add(T("log.activePath", Args(("path", ActivePath))));
            }

            LogLines.Add(T("log.error", Args(("message", ex.ToString()))));
        }
      finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            IsBusy = false;
            CurrentStage = T("status.idle");
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

    private string FormatProgress(OperationProgress progress)
    {
        var path = string.IsNullOrWhiteSpace(progress.Path) ? string.Empty : $" | {Path.GetFileName(progress.Path)}";
        var message = progress.MessageKey is { } key ? T(key, progress.MessageArguments) : progress.Message;
        return progress.Stage switch
        {
            "start" => message ?? T("progress.started", Args(("operation", progress.Operation))),
            "scan-resource" => message ?? T("progress.scannedResources", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "process-source" => T("progress.processedSources", Args(("current", progress.Current), ("total", progress.Total), ("sources", progress.SourceCount), ("warnings", progress.WarningCount), ("errors", progress.ErrorCount), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "finalize-plan" => message ?? T("progress.finalizing"),
            "load-source" => message ?? T("progress.loadingSource", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "build-target" => message ?? T("progress.buildingTarget", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "build-creature-metadata" => message ?? T("progress.buildingCreatureMetadata", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "write-target" => T("progress.wroteTargets", Args(("current", progress.Current), ("total", progress.Total), ("files", progress.WrittenFileCount), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "export-file" => T("progress.exportedFiles", Args(("current", progress.Current), ("total", progress.Total), ("written", progress.WrittenFileCount), ("skipped", progress.SkippedCount), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "copy-source-resource" => T("progress.copiedResources", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "copy-source-file" => T("progress.copiedSourceFiles", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "copy-generated-file" => T("progress.copiedGeneratedFiles", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "rename-stream" => T("progress.renamedStreams", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "backup-source-ymt" => T("progress.backedUpSourceFiles", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "remove-source-ymt" => T("progress.removedSourceFiles", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "backup-source-metadata" => T("progress.backedUpMetadata", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "remove-source-metadata" => T("progress.removedMetadata", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "delete-generated-resource" => message ?? T("progress.removedGeneratedResource", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "copy-backup-file" => message ?? T("progress.restoredBackupFile", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "move-stream-file" => message ?? T("progress.movedStreamFile", Args(("current", progress.Current), ("total", progress.Total), ("path", Path.GetFileName(progress.Path ?? string.Empty)))),
            "complete" => message ?? T("progress.complete", Args(("operation", progress.Operation))),
            _ => message ?? $"{progress.Operation}: {progress.Stage}{path}",
        };
    }

    private async Task LoadRestoreManifestPreviewAsync(string manifestPath, bool updateStatus = true)
    {
        var version = ++_restoreManifestLoadVersion;
        RestoreSummaryLines = [];
        RestoreActionLines = [];
        _restorePreview = null;
        OnPropertyChanged(nameof(HasRestoreManifestPreview));

        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            RefreshCommands();
            return;
        }

        try
        {
            var preview = await _workflow.LoadRestoreManifestPreviewAsync(manifestPath, CancellationToken.None);
            if (version != _restoreManifestLoadVersion || !string.Equals(manifestPath, RestoreManifestPath, StringComparison.Ordinal))
            {
                return;
            }

            _restorePreview = preview;
            SetRestorePreviewLines(preview);
            SelectedTabIndex = 5;
            if (updateStatus)
            {
                Status = T("status.backupManifestLoaded", Args(("count", preview.Actions.Count)));
            }
            OnPropertyChanged(nameof(HasRestoreManifestPreview));
        }
        catch (Exception ex)
        {
            if (version != _restoreManifestLoadVersion)
            {
                return;
            }

            RestoreSummaryLines = [T("status.couldNotLoadRestoreManifest", Args(("message", ex.Message)))];
            Status = ex.Message;
            LogLines.Add(T("status.couldNotLoadRestoreManifest", Args(("message", ex.Message))));
        }
        finally
        {
            RefreshCommands();
        }
    }

    private void SetRestorePreviewLines(RestoreManifestPreview preview)
    {
        var deleteCount = preview.Actions.Count(action => action.Kind == "delete-generated-resource");
        var copyCount = preview.Actions.Count(action => action.Kind == "copy-backup-file");
        var moveCount = preview.Actions.Count(action => action.Kind == "move-stream-file");
        var summaryLines = new List<string>
        {
            T("restore.summary"),
            T("restore.manifest", Args(("path", preview.ManifestPath))),
            T("restore.manifestEntries", Args(("count", preview.Entries.Count))),
            T("restore.actions", Args(("count", preview.Actions.Count))),
            T("restore.generatedResources", Args(("count", deleteCount))),
            T("restore.backupFiles", Args(("count", copyCount))),
            T("restore.streamFiles", Args(("count", moveCount))),
        };

        if (preview.SkippedActions.Count > 0)
        {
            summaryLines.Add(T("restore.skippedNested", Args(("count", preview.SkippedActions.Count))));
        }

        var actionLines = new List<string>(preview.Actions.Count + preview.SkippedActions.Count + 1);
        foreach (var action in preview.Actions)
        {
            actionLines.Add(FormatRestoreAction(action));
        }

        if (preview.SkippedActions.Count > 0)
        {
            actionLines.Add(T("restore.skippedEntries"));
            foreach (var action in preview.SkippedActions)
            {
                actionLines.Add(T("restore.skip", Args(("text", FormatRestoreAction(action)))));
            }
        }

        RestoreSummaryLines = summaryLines;
        RestoreActionLines = actionLines;
    }

    private void SetRepackReport(MergePlan plan)
    {
        RepackReport = new YmtRepackReportBuilder().Build(plan);
        RepackReportLines.Clear();
        var text = new YmtRepackReportFormatter(_localization).Format(RepackReport);
        foreach (var line in text.Split(Environment.NewLine))
        {
            RepackReportLines.Add(line);
        }
    }

    private void RefreshRestoreText()
    {
        RestoreSummaryText = string.Join(Environment.NewLine, RestoreSummaryLines);
    }

    private string FormatRestoreAction(RestoreAction action)
        => action.Kind switch
        {
            "delete-generated-resource" => T("restore.removeGenerated", Args(("path", action.DestinationPath))),
            "copy-backup-file" => T("restore.copyBackup", Args(("source", action.SourcePath), ("destination", action.DestinationPath))),
            "move-stream-file" => T("restore.moveStream", Args(("source", action.SourcePath), ("destination", action.DestinationPath))),
            _ => action.Description,
        };

    private void EnsureDerivedPaths()
    {
        if (ResourcePaths.Count == 0)
        {
            return;
        }

        var firstResourcePath = ResourcePaths[0];
        var parent = Directory.GetParent(firstResourcePath)?.FullName ?? firstResourcePath;
        var defaultOutputRoot = CopyResourcesToOutputBeforeRename
            ? Path.Combine(parent, DefaultCopyOutputFolderName)
            : parent;
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = defaultOutputRoot;
        }
        else
        {
            _generatedResourcesRoot = OutputPath;
            OnPropertyChanged(nameof(GeneratedResourcesRoot));
        }

        if (string.IsNullOrWhiteSpace(BackupRoot))
        {
            BackupRoot = Path.Combine(parent, "backups");
        }

        if (string.IsNullOrWhiteSpace(PlanPath))
        {
            PlanPath = Path.Combine(parent, "plan.json");
        }
    }

    private void ReplaceResourceFolders(IEnumerable<string> paths)
    {
        ResourcePaths.Clear();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ResourcePaths.Add(path);
        }

        SyncResourcesPath();
        EnsureDerivedPaths();
        ResetPlanState();
        RefreshCommands();
    }

    private void SyncResourcesPath()
    {
        _resourcesPath = ResourcePaths.FirstOrDefault() ?? string.Empty;
        OnPropertyChanged(nameof(ResourcesPath));
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
        RepackReport = null;
        RepackReportLines.Clear();
        RefreshCommands();
    }

    private void SetSummaryLines(string title, WorkflowSummary summary)
    {
        SummaryLines.Clear();
        SummaryLines.Add(T("summary.title", Args(("title", title))));
        SummaryLines.Add(T("summary.sourceYmts", Args(("count", summary.SourceYmtCount))));
        SummaryLines.Add(T("summary.targetCollections", Args(("count", summary.TargetCollectionCount))));
        SummaryLines.Add(T("summary.streamRenames", Args(("count", summary.StreamRenameCount))));
        SummaryLines.Add(T("summary.warnings", Args(("count", summary.WarningCount))));
        SummaryLines.Add(T("summary.errors", Args(("count", summary.ErrorCount))));
        if (summary.WrittenFileCount > 0)
        {
            SummaryLines.Add(T("summary.writtenFiles", Args(("count", summary.WrittenFileCount))));
        }

        if (summary.SkippedFileCount > 0)
        {
            SummaryLines.Add(T("summary.skippedFiles", Args(("count", summary.SkippedFileCount))));
        }

        if (summary.BackupEntryCount > 0)
        {
            SummaryLines.Add(T("summary.backupEntries", Args(("count", summary.BackupEntryCount))));
        }
    }

    private async Task LoadLastProjectAsync()
    {
        try
        {
            var languageOverride = await _projectStore.LoadLanguageOverrideAsync();
            if (!string.IsNullOrWhiteSpace(languageOverride) && _localization.Catalogs.Any(catalog => catalog.Locale.Equals(languageOverride, StringComparison.OrdinalIgnoreCase)))
            {
                _localization.OverrideLocale = languageOverride;
                _selectedLanguage = LanguageOptions.FirstOrDefault(option => option.Locale?.Equals(languageOverride, StringComparison.OrdinalIgnoreCase) == true);
                OnPropertyChanged(nameof(SelectedLanguage));
            }

            var project = await _projectStore.LoadLastProjectAsync();
            ApplyProjectSettings(project.Settings, project.ProjectPath);
            Status = string.IsNullOrWhiteSpace(project.ProjectPath)
                ? Status
                : T("status.projectLoaded", Args(("name", Path.GetFileName(project.ProjectPath))));
        }
        catch (Exception ex)
        {
            LogLines.Add(T("log.couldNotLoadProject", Args(("message", ex.Message))));
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateChecker is null)
        {
            return;
        }

        VersionCheckText = T("version.checking", Args(("version", CurrentVersion.Display)));

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await _updateChecker.CheckAsync(CurrentVersion, cts.Token);
            if (result is { IsUpdateAvailable: true })
            {
                VersionCheckText = T("version.latest", Args(("version", CurrentVersion.Display), ("latest", result.LatestVersion.Display)));
                UpdateReleaseUrl = result.ReleaseUrl;
                IsUpdateAvailable = true;
                return;
            }

            VersionCheckText = T("version.upToDate", Args(("version", CurrentVersion.Display)));
            IsUpdateAvailable = false;
            UpdateReleaseUrl = string.Empty;
        }
        catch
        {
            VersionCheckText = T("version.unavailable", Args(("version", CurrentVersion.Display)));
            IsUpdateAvailable = false;
            UpdateReleaseUrl = string.Empty;
        }
    }

    private async Task SaveOpenProjectAsync()
    {
        if (!string.IsNullOrWhiteSpace(CurrentProjectPath))
        {
            await _projectStore.SaveProjectAsync(CurrentProjectPath, CreateProjectSettings());
        }
    }

    private ProjectSettings CreateProjectSettings()
        => new()
        {
            ResourcesPath = ResourcesPath,
            ResourcePaths = ResourcePaths.ToList(),
            OutputPath = OutputPath,
            GeneratedResourcesRoot = OutputPath,
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
            CopyResourcesToOutputBeforeRename = CopyResourcesToOutputBeforeRename,
            OptimizeYmtUsage = OptimizeYmtUsage,
        };

    private void ApplyProjectSettings(ProjectSettings settings, string projectPath)
    {
        ReplaceResourceFolders(settings.ResourcePaths.Count > 0 ? settings.ResourcePaths : string.IsNullOrWhiteSpace(settings.ResourcesPath) ? [] : [settings.ResourcesPath]);
        _outputPath = !string.IsNullOrWhiteSpace(settings.OutputPath)
            ? settings.OutputPath
            : settings.GeneratedResourcesRoot;
        _generatedResourcesRoot = _outputPath;
        _backupRoot = settings.BackupRoot;
        _planPath = settings.PlanPath;
        _restoreManifestPath = string.Empty;
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
        _copyResourcesToOutputBeforeRename = settings.CopyResourcesToOutputBeforeRename;
        _optimizeYmtUsage = settings.OptimizeYmtUsage;
        CurrentProjectPath = projectPath;
        ResetPlanState();
        OnPropertyChanged(string.Empty);
        EnsureDerivedPaths();
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ExportXmlCommand.RaiseCanExecuteChanged();
        AnalyzeCommand.RaiseCanExecuteChanged();
        BuildPreviewCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        MoveResourceUpCommand.RaiseCanExecuteChanged();
        MoveResourceDownCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(PlannedBackupCount));
        OnPropertyChanged(nameof(CanExportXml));
        OnPropertyChanged(nameof(CanAnalyzeResources));
        OnPropertyChanged(nameof(CanBuildPreviewPlan));
        OnPropertyChanged(nameof(CanApplyPlan));
        OnPropertyChanged(nameof(CanRestoreBackup));
        OnPropertyChanged(nameof(CanMoveResourceUp));
        OnPropertyChanged(nameof(CanMoveResourceDown));
        OnPropertyChanged(nameof(CanEditProject));
        OnPropertyChanged(nameof(HasCurrentProject));
    }
}
