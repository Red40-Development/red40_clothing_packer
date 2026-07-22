using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Reporting;
using ClothingRepacker.Core.Services;
using ClothingRepacker.Core;
using ClothingRepacker.CodeWalker;
using System.Reflection;

var exitCode = await ProgramEntry.RunAsync(args);
return exitCode;

public static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        var normalizedArgs = args.ToList();
        var skipVersionCheck = normalizedArgs.RemoveAll(arg =>
            string.Equals(arg, "--no-version-check", StringComparison.OrdinalIgnoreCase)) > 0;

        if (normalizedArgs.Count == 0 || normalizedArgs[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = normalizedArgs[0].ToLowerInvariant();
        var options = ParseOptions(normalizedArgs.Skip(1).ToArray());
        if (skipVersionCheck)
        {
            options.Add("--no-version-check", null);
        }

        await CheckForUpdatesAsync(options);
        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

        try
        {
            switch (command)
            {
                case "analyze":
                    return await RunAnalyzeAsync(service, options);
                case "build":
                    return await RunBuildAsync(service, options);
                case "apply":
                    return await RunApplyAsync(service, options);
                case "restore":
                    return await RunRestoreAsync(service, options);
                case "validate":
                    return await RunValidateAsync(service, options);
                case "report":
                    return await RunReportAsync(service, options);
                case "export-xml":
                    return await RunExportXmlAsync(service, options);
                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    PrintHelp();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunAnalyzeAsync(RepackerService service, CliOptions options)
    {
        using var progressWriter = new ConsoleProgressWriter();
        var targetResource = options.GetValueOrDefault("--target-resource") ?? "zz_merged_clothing_meta";
        var targetPrefix = options.GetValueOrDefault("--target-prefix") ?? "merged";
        var outPath = Required(options, "--out");
        var legacyMaxDrawablesPerType = ParseNullableInt(options.GetValueOrDefault("--max-drawables-per-type"));
        var settings = new MergePlanSettings
        {
            TargetPrefix = targetPrefix,
            FemalePrefix = options.GetValueOrDefault("--female-prefix") ?? $"{targetPrefix}_f",
            MalePrefix = options.GetValueOrDefault("--male-prefix") ?? $"{targetPrefix}_m",
            MaxDrawablesPerComponent = ParseInt(
                options.GetValueOrDefault("--max-drawables-per-component"),
                legacyMaxDrawablesPerType ?? ClothingConstants.DefaultMaxDrawablesPerComponent),
            MaxDrawablesPerProp = ParseInt(
                options.GetValueOrDefault("--max-drawables-per-prop"),
                legacyMaxDrawablesPerType ?? ClothingConstants.DefaultMaxDrawablesPerProp),
            OptimizeYmtUsage = options.ContainsKey("--optimize-ymt-usage"),
        };

        var result = await AnalyzeWithOptionsAsync(service, options, targetResource, settings, CreateConsoleProgress(progressWriter));
        progressWriter.CompleteLine();
        await service.SavePlanAsync(result.Plan, outPath);
        Console.WriteLine($"Analyzed {result.Plan.SourceYmts.Count} YMTs into {result.Plan.TargetCollections.Count} target collections.");
        Console.WriteLine($"Warnings: {result.Plan.Warnings.Count}. Errors: {result.Plan.Errors.Count}. Planned stream renames: {result.Plan.StreamRenames.Count}.");
        foreach (var warning in result.Plan.Warnings)
        {
            Console.Error.WriteLine(warning);
        }

        if (result.Plan.Errors.Count > 0)
        {
            foreach (var error in result.Plan.Errors)
            {
                Console.Error.WriteLine(error);
            }

            Console.WriteLine($"Plan contains {result.Plan.Errors.Count} error(s).");
            return 1;
        }

        return 0;
    }

    private static async Task<int> RunBuildAsync(RepackerService service, CliOptions options)
    {
        using var progressWriter = new ConsoleProgressWriter();
        var plan = await service.LoadPlanAsync(Required(options, "--plan"));
        var buildOptions = new BuildOptions
        {
            IncludeYmtXml = ParseBool(options.GetValueOrDefault("--include-ymt-xml"), fallback: true),
            IncludeDebugClient = ParseBool(options.GetValueOrDefault("--include-debug-client"), fallback: true),
        };
        var result = await service.BuildAsync(plan, Required(options, "--out"), buildOptions, CreateConsoleProgress(progressWriter));
        progressWriter.CompleteLine();
        Console.WriteLine($"Wrote {result.WrittenFiles.Count} file(s) to {result.OutputRoot}.");
        return 0;
    }

    private static async Task<int> RunApplyAsync(RepackerService service, CliOptions options)
    {
        using var progressWriter = new ConsoleProgressWriter();
        var plan = await service.LoadPlanAsync(Required(options, "--plan"));
        var applyOptions = new ApplyOptions
        {
            CopyResourcesToOutputBeforeRename = options.ContainsKey("--copy-resources-to-output") || !plan.Settings.RenameStreamsInPlace,
            IncludeYmtXml = ParseBool(options.GetValueOrDefault("--include-ymt-xml"), fallback: true),
            IncludeDebugClient = ParseBool(options.GetValueOrDefault("--include-debug-client"), fallback: true),
        };
        var entries = await service.ApplyAsync(plan, Required(options, "--backup-root"), applyOptions, CreateConsoleProgress(progressWriter));
        progressWriter.CompleteLine();
        Console.WriteLine($"Applied plan with {entries.Count} backup entries.");
        return 0;
    }

    private static async Task<int> RunRestoreAsync(RepackerService service, CliOptions options)
    {
        using var progressWriter = new ConsoleProgressWriter();
        await service.RestoreAsync(Required(options, "--backup-manifest"), CreateConsoleProgress(progressWriter));
        progressWriter.CompleteLine();
        Console.WriteLine("Restore complete.");
        return 0;
    }

    private static async Task<int> RunValidateAsync(RepackerService service, CliOptions options)
    {
        if (options.TryGetValue("--plan", out var planPath) && !string.IsNullOrWhiteSpace(planPath))
        {
            var plan = await service.LoadPlanAsync(planPath);
            var errors = service.ValidatePlan(plan);
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }

            Console.WriteLine(errors.Count == 0 ? "Plan is valid." : $"Plan has {errors.Count} error(s).");
            return errors.Count == 0 ? 0 : 1;
        }

        if (options.ContainsKey("--resources") || options.GetValues("--resource").Count > 0)
        {
            using var progressWriter = new ConsoleProgressWriter();
            var result = await AnalyzeWithOptionsAsync(service, options, "zz_merged_clothing_meta", new MergePlanSettings(), CreateConsoleProgress(progressWriter));
            progressWriter.CompleteLine();
            foreach (var error in result.Plan.Errors)
            {
                Console.Error.WriteLine(error);
            }

            Console.WriteLine(result.Plan.Errors.Count == 0 ? "Resources validated." : $"Resources have {result.Plan.Errors.Count} error(s).");
            return result.Plan.Errors.Count == 0 ? 0 : 1;
        }

        throw new InvalidOperationException("validate requires --plan, --resources, or at least one --resource.");
    }

    private static async Task<int> RunReportAsync(RepackerService service, CliOptions options)
    {
        var plan = await service.LoadPlanAsync(Required(options, "--plan"));
        var report = new YmtRepackReportBuilder().Build(plan);
        var text = new YmtRepackReportFormatter().Format(report);
        if (options.TryGetValue("--out", out var outPath) && !string.IsNullOrWhiteSpace(outPath))
        {
            var directory = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(outPath, text + Environment.NewLine);
            Console.WriteLine($"Wrote YMT repack report to {outPath}.");
            return 0;
        }

        Console.WriteLine(text);
        return 0;
    }

    private static async Task<int> RunExportXmlAsync(RepackerService service, CliOptions options)
    {
        using var progressWriter = new ConsoleProgressWriter();
        var folder = Required(options, "--folder");
        var result = await service.ExportYmtsToXmlAsync(folder, options.ContainsKey("--overwrite"), CreateConsoleProgress(progressWriter));
        progressWriter.CompleteLine();
        Console.WriteLine($"Exported {result.WrittenFiles.Count} YMT XML file(s).");
        if (result.SkippedFiles.Count > 0)
        {
            Console.WriteLine($"Skipped {result.SkippedFiles.Count} existing XML file(s).");
        }

        return 0;
    }

    private static Task<AnalyzeResult> AnalyzeWithOptionsAsync(RepackerService service, CliOptions options, string targetResource, MergePlanSettings settings, IProgress<OperationProgress> progress)
    {
        var resources = options.GetValueOrDefault("--resources");
        var resourceFolders = options.GetValues("--resource");
        if (!string.IsNullOrWhiteSpace(resources) && resourceFolders.Count > 0)
        {
            throw new InvalidOperationException("Use either --resources <parent> or repeated --resource <folder>, not both.");
        }

        if (resourceFolders.Count > 0)
        {
            var generatedRoot = options.GetValueOrDefault("--generated-root");
            if (string.IsNullOrWhiteSpace(generatedRoot))
            {
                throw new InvalidOperationException("--generated-root is required when using --resource.");
            }

            return service.AnalyzeAsync(resourceFolders, generatedRoot, targetResource, settings, progress);
        }

        if (!string.IsNullOrWhiteSpace(resources))
        {
            return service.AnalyzeAsync(resources, targetResource, settings, progress);
        }

        throw new InvalidOperationException("Missing required option --resources or --resource.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
clothing-repacker analyze --resources <path> --target-resource <name> --out <plan.json>
clothing-repacker analyze --resource <path> [--resource <path> ...] --generated-root <folder> --target-resource <name> --out <plan.json>
  [--max-drawables-per-component <256>] [--max-drawables-per-prop <256>]
  [--optimize-ymt-usage]
clothing-repacker build --plan <plan.json> --out <folder>
  [--include-ymt-xml <true|false>] [--include-debug-client <true|false>]
clothing-repacker apply --plan <plan.json> --backup-root <folder> [--copy-resources-to-output]
  [--include-ymt-xml <true|false>] [--include-debug-client <true|false>]
clothing-repacker restore --backup-manifest <backup-manifest.json>
clothing-repacker validate --plan <plan.json>
clothing-repacker validate --resources <path>
clothing-repacker validate --resource <path> [--resource <path> ...] --generated-root <folder>
clothing-repacker report --plan <plan.json> [--out <report.txt>]
clothing-repacker export-xml --folder <path> [--overwrite]

Global options:
  --no-version-check   Skip the GitHub update check.

Apply options:
  --copy-resources-to-output
                       Copy source resources into the plan's generated/output root
                       and rename the copies instead of modifying originals.

Analyze options:
  --optimize-ymt-usage
                       Split source YMT lanes across target collections when it
                       can reduce the total number of generated YMTs.
""");
    }

    private static async Task CheckForUpdatesAsync(CliOptions options)
    {
        if (options.ContainsKey("--no-version-check") ||
            string.Equals(Environment.GetEnvironmentVariable("RED40_NO_VERSION_CHECK"), "1", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var httpClient = new HttpClient();
            var checker = new GitHubVersionChecker(httpClient, "Red40-Development", "red40_clothing_packer");
            var currentVersion = AppVersion.FromInformationalVersion(
                typeof(ProgramEntry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            var result = await checker.CheckAsync(currentVersion, cts.Token);
            if (result is not null && result.IsUpdateAvailable)
            {
                Console.Error.WriteLine(
                    $"Update available: {result.LatestVersion.Display} is newer than {result.CurrentVersion.Display}. Download: {result.ReleaseUrl}");
            }
        }
        catch
        {
            // A failed update check should never block the requested CLI operation.
        }
    }

    public static CliOptions ParseOptions(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                options.Add(arg, args[i + 1]);
                i++;
            }
            else
            {
                options.Add(arg, null);
            }
        }

        return options;
    }

    private static string Required(CliOptions options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required option {name}.");

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static IProgress<OperationProgress> CreateConsoleProgress(ConsoleProgressWriter writer)
        => new Progress<OperationProgress>(progress => writer.Write(FormatProgress(progress)));

    private static string FormatProgress(OperationProgress progress)
    {
        var prefix = $"[{progress.Operation}]";
        var progressBar = progress.Total > 0 ? $" {BuildBar(progress.Current, progress.Total)} {progress.Current}/{progress.Total}" : string.Empty;
        var path = string.IsNullOrWhiteSpace(progress.Path) ? string.Empty : $" | {Path.GetFileName(progress.Path)}";

        return progress.Stage switch
        {
            "start" => $"{prefix} {progress.Message}",
            "process-source" => $"{prefix}{progressBar} files | sources {progress.SourceCount} | warnings {progress.WarningCount} | errors {progress.ErrorCount}{path}",
            "plan-targets" => $"{prefix} Planning target collections | sources {progress.SourceCount} | warnings {progress.WarningCount} | errors {progress.ErrorCount}",
            "build-plan" => $"{prefix}{progressBar} targets | planned {progress.TargetCount}{path}",
            "finalize-plan" => $"{prefix}{progressBar} finalizing merge plan | targets {progress.TargetCount} | warnings {progress.WarningCount} | errors {progress.ErrorCount}",
            "load-source" => $"{prefix}{progressBar} source YMTs loading{path}",
            "build-target" => $"{prefix}{progressBar} target collections building{path}",
            "build-creature-metadata" => $"{prefix}{progressBar} creature metadata building{path}",
            "write-target" => $"{prefix}{progressBar} target collections built | files written {progress.WrittenFileCount}{path}",
            "export-file" => $"{prefix}{progressBar} files | written {progress.WrittenFileCount} | skipped {progress.SkippedCount}{path}",
            "build-staging" => $"{prefix} {progress.Message}",
            "copy-source-resource" => $"{prefix}{progressBar} source resources copied{path}",
            "rename-stream" => $"{prefix}{progressBar} stream files renamed | backups {progress.BackupCount}{path}",
            "backup-source-ymt" => $"{prefix}{progressBar} source YMTs backed up | renames {progress.RenameCount} | backups {progress.BackupCount}{path}",
            "remove-source-ymt" => $"{prefix}{progressBar} source YMTs removed from copy | renames {progress.RenameCount} | removed {progress.RemovedCount}{path}",
            "backup-source-metadata" => $"{prefix}{progressBar} source metadata backed up | renames {progress.RenameCount} | backups {progress.BackupCount}{path}",
            "remove-source-metadata" => $"{prefix}{progressBar} source metadata removed from copy | renames {progress.RenameCount} | removed {progress.RemovedCount}{path}",
            "copy-generated-resource" => $"{prefix} {progress.Message} | generated files {progress.WrittenFileCount}",
            "complete" => FormatCompleteProgress(prefix, progress),
            _ => $"{prefix} {progress.Message ?? progress.Stage}",
        };
    }

    private static string FormatCompleteProgress(string prefix, OperationProgress progress)
    {
        var stats = progress.Operation switch
        {
            "analyze" => $"sources {progress.SourceCount} | warnings {progress.WarningCount} | errors {progress.ErrorCount} | targets {progress.TargetCount} | renames {progress.RenameCount}",
            "build" => $"targets {progress.TargetCount} | files written {progress.WrittenFileCount}",
            "apply" => progress.RemovedCount > 0
                ? $"renames {progress.RenameCount} | removed {progress.RemovedCount} | generated files {progress.WrittenFileCount}"
                : $"renames {progress.RenameCount} | backups {progress.BackupCount} | generated files {progress.WrittenFileCount}",
            "export-xml" => $"written {progress.WrittenFileCount} | skipped {progress.SkippedCount}",
            _ => string.Empty,
        };

        return string.IsNullOrWhiteSpace(stats)
            ? $"{prefix} {progress.Message ?? "Complete."}"
            : $"{prefix} {progress.Message ?? "Complete."} | {stats}";
    }

    private static string BuildBar(int current, int total)
    {
        const int width = 20;
        if (total <= 0)
        {
            return "[--------------------]";
        }

        var filled = (int)Math.Round((double)current / total * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);
        return $"[{new string('#', filled)}{new string('-', width - filled)}]";
    }
}

internal sealed class ConsoleProgressWriter : IDisposable
{
    private static readonly char[] SpinnerFrames = ['|', '/', '-', '\\'];

    private readonly object _gate = new();
    private readonly bool _useLiveUpdates = !Console.IsOutputRedirected;
    private readonly Timer? _timer;
    private int _lastLength;
    private bool _hasActiveLine;
    private int _spinnerIndex;
    private string _currentText = "Working...";
    private bool _isComplete;

    public ConsoleProgressWriter()
    {
        if (_useLiveUpdates)
        {
            _timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(125));
        }
    }

    public void Write(string text)
    {
        lock (_gate)
        {
            if (!_useLiveUpdates)
            {
                Console.WriteLine(text);
                return;
            }

            _currentText = text;
            RenderLine();
        }
    }

    public void CompleteLine()
    {
        lock (_gate)
        {
            if (_isComplete)
            {
                return;
            }

            _isComplete = true;
            _timer?.Dispose();
            if (!_useLiveUpdates || !_hasActiveLine)
            {
                return;
            }

            Console.WriteLine();
            _lastLength = 0;
            _hasActiveLine = false;
        }
    }

    public void Dispose()
        => CompleteLine();

    private void Tick(object? state)
    {
        lock (_gate)
        {
            if (_isComplete)
            {
                return;
            }

            RenderLine();
        }
    }

    private void RenderLine()
    {
        var frame = SpinnerFrames[_spinnerIndex++ % SpinnerFrames.Length];
        var text = $"{frame} {_currentText}";
        var padded = text.Length < _lastLength
            ? text + new string(' ', _lastLength - text.Length)
            : text;

        Console.Write('\r');
        Console.Write(padded);
        _lastLength = padded.Length;
        _hasActiveLine = true;
    }
}

public sealed class CliOptions
{
    private readonly Dictionary<string, List<string?>> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string name, string? value)
    {
        if (!_values.TryGetValue(name, out var values))
        {
            values = [];
            _values[name] = values;
        }

        values.Add(value);
    }

    public bool ContainsKey(string name)
        => _values.ContainsKey(name);

    public bool TryGetValue(string name, out string? value)
    {
        if (_values.TryGetValue(name, out var values) && values.Count > 0)
        {
            value = values[^1];
            return true;
        }

        value = null;
        return false;
    }

    public string? GetValueOrDefault(string name)
        => TryGetValue(name, out var value) ? value : null;

    public IReadOnlyList<string> GetValues(string name)
        => _values.TryGetValue(name, out var values)
            ? values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList()
            : [];
}
