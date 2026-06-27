using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using ClothingRepacker.Core;
using ClothingRepacker.CodeWalker;

var exitCode = await ProgramEntry.RunAsync(args);
return exitCode;

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());
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

    private static async Task<int> RunAnalyzeAsync(RepackerService service, Dictionary<string, string?> options)
    {
        using var progressWriter = new ConsoleProgressWriter();
        var resources = Required(options, "--resources");
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
            ShopMetaMode = options.GetValueOrDefault("--shop-meta-mode") ?? "complete",
            CreatureMetadataMode = options.GetValueOrDefault("--creature-metadata-mode") ?? "repair",
        };

        var result = await service.AnalyzeAsync(resources, targetResource, settings, CreateConsoleProgress(progressWriter));
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

    private static async Task<int> RunBuildAsync(RepackerService service, Dictionary<string, string?> options)
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

    private static async Task<int> RunApplyAsync(RepackerService service, Dictionary<string, string?> options)
    {
        using var progressWriter = new ConsoleProgressWriter();
        var plan = await service.LoadPlanAsync(Required(options, "--plan"));
        var entries = await service.ApplyAsync(plan, Required(options, "--backup-root"), CreateConsoleProgress(progressWriter));
        progressWriter.CompleteLine();
        Console.WriteLine($"Applied plan with {entries.Count} backup entries.");
        return 0;
    }

    private static async Task<int> RunRestoreAsync(RepackerService service, Dictionary<string, string?> options)
    {
        await service.RestoreAsync(Required(options, "--backup-manifest"));
        Console.WriteLine("Restore complete.");
        return 0;
    }

    private static async Task<int> RunValidateAsync(RepackerService service, Dictionary<string, string?> options)
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

        if (options.TryGetValue("--resources", out var resources) && !string.IsNullOrWhiteSpace(resources))
        {
            using var progressWriter = new ConsoleProgressWriter();
            var result = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings(), CreateConsoleProgress(progressWriter));
            progressWriter.CompleteLine();
            foreach (var error in result.Plan.Errors)
            {
                Console.Error.WriteLine(error);
            }

            Console.WriteLine(result.Plan.Errors.Count == 0 ? "Resources validated." : $"Resources have {result.Plan.Errors.Count} error(s).");
            return result.Plan.Errors.Count == 0 ? 0 : 1;
        }

        throw new InvalidOperationException("validate requires --plan or --resources.");
    }

    private static async Task<int> RunExportXmlAsync(RepackerService service, Dictionary<string, string?> options)
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

    private static void PrintHelp()
    {
        Console.WriteLine("""
clothing-repacker analyze --resources <path> --target-resource <name> --out <plan.json>
  [--max-drawables-per-component <128>] [--max-drawables-per-prop <255>]
  [--creature-metadata-mode <repair|preserve>]
clothing-repacker build --plan <plan.json> --out <folder>
  [--include-ymt-xml <true|false>] [--include-debug-client <true|false>]
clothing-repacker apply --plan <plan.json> --backup-root <folder>
clothing-repacker restore --backup-manifest <backup-manifest.json>
clothing-repacker validate --plan <plan.json>
clothing-repacker validate --resources <path>
clothing-repacker export-xml --folder <path> [--overwrite]
""");
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                options[arg] = args[i + 1];
                i++;
            }
            else
            {
                options[arg] = null;
            }
        }

        return options;
    }

    private static string Required(Dictionary<string, string?> options, string name)
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
            "load-source" => $"{prefix}{progressBar} source YMTs loaded{path}",
            "write-target" => $"{prefix}{progressBar} target collections built | files written {progress.WrittenFileCount}{path}",
            "export-file" => $"{prefix}{progressBar} files | written {progress.WrittenFileCount} | skipped {progress.SkippedCount}{path}",
            "build-staging" => $"{prefix} {progress.Message}",
            "rename-stream" => $"{prefix}{progressBar} stream files renamed | backups {progress.BackupCount}{path}",
            "backup-source-ymt" => $"{prefix}{progressBar} source YMTs backed up | renames {progress.RenameCount} | backups {progress.BackupCount}{path}",
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
            "apply" => $"renames {progress.RenameCount} | backups {progress.BackupCount} | generated files {progress.WrittenFileCount}",
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
    private readonly object _gate = new();
    private readonly bool _useLiveUpdates = !Console.IsOutputRedirected;
    private int _lastLength;
    private bool _hasActiveLine;

    public void Write(string text)
    {
        lock (_gate)
        {
            if (!_useLiveUpdates)
            {
                Console.WriteLine(text);
                return;
            }

            var padded = text.Length < _lastLength
                ? text + new string(' ', _lastLength - text.Length)
                : text;

            Console.Write('\r');
            Console.Write(padded);
            _lastLength = padded.Length;
            _hasActiveLine = true;
        }
    }

    public void CompleteLine()
    {
        lock (_gate)
        {
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
}
