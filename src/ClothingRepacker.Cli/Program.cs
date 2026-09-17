using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Reporting;
using ClothingRepacker.Core.Services;
using ClothingRepacker.Core;
using ClothingRepacker.CodeWalker;
using System.Reflection;
using System.Globalization;

var exitCode = await ProgramEntry.RunAsync(args);
return exitCode;

public static class ProgramEntry
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--resources",
        "--resource",
        "--generated-root",
        "--target-resource",
        "--target-prefix",
        "--female-prefix",
        "--male-prefix",
        "--max-drawables-per-component",
        "--max-drawables-per-prop",
        "--out",
        "--plan",
        "--backup-root",
        "--backup-manifest",
        "--folder",
        "--include-ymt-xml",
        "--include-debug-client",
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--no-version-check",
        "--optimize-ymt-usage",
        "--copy-resources-to-output",
        "--overwrite",
    };

    private static readonly HashSet<string> KnownOptions = new(
        ValueOptions.Concat(FlagOptions),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "analyze",
        "build",
        "apply",
        "restore",
        "validate",
        "report",
        "export-xml",
        "diagnostics",
    };

    private static readonly HashSet<string> RepeatableOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--resource",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var skipVersionCheckCount = args.Count(arg =>
                string.Equals(arg, "--no-version-check", StringComparison.OrdinalIgnoreCase));
            if (skipVersionCheckCount > 1)
            {
                throw new InvalidOperationException("Option '--no-version-check' may only be specified once.");
            }

            var normalizedArgs = args
                .Where(arg => !string.Equals(arg, "--no-version-check", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (normalizedArgs.Count == 0)
            {
                PrintHelp();
                return 0;
            }

            if (normalizedArgs.Count == 1
                && (string.Equals(normalizedArgs[0], "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedArgs[0], "-h", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedArgs[0], "help", StringComparison.OrdinalIgnoreCase)))
            {
                PrintHelp();
                return 0;
            }

            var command = normalizedArgs[0].ToLowerInvariant();
            if (!Commands.Contains(command))
            {
                throw new InvalidOperationException($"Unknown command '{normalizedArgs[0]}'.");
            }

            var options = ParseOptions(normalizedArgs.Skip(1).ToArray());
            if (skipVersionCheckCount == 1)
            {
                options.Add("--no-version-check", null);
            }

            ValidateCommandOptions(command, options);
            await CheckForUpdatesAsync(options);
            var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));

            return command switch
            {
                "analyze" => await RunAnalyzeAsync(service, options),
                "build" => await RunBuildAsync(service, options),
                "apply" => await RunApplyAsync(service, options),
                "restore" => await RunRestoreAsync(service, options),
                "validate" => await RunValidateAsync(service, options),
                "report" => await RunReportAsync(service, options),
                "export-xml" => await RunExportXmlAsync(service, options),
                "diagnostics" => await RunDiagnosticsAsync(options),
                _ => throw new InvalidOperationException($"Unknown command '{command}'."),
            };
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
        var settings = new MergePlanSettings
        {
            TargetPrefix = targetPrefix,
            FemalePrefix = options.GetValueOrDefault("--female-prefix") ?? $"{targetPrefix}_f",
            MalePrefix = options.GetValueOrDefault("--male-prefix") ?? $"{targetPrefix}_m",
            MaxDrawablesPerComponent = ParseInt(
                options.GetValueOrDefault("--max-drawables-per-component"),
                ClothingConstants.DefaultMaxDrawablesPerComponent),
            MaxDrawablesPerProp = ParseInt(
                options.GetValueOrDefault("--max-drawables-per-prop"),
                ClothingConstants.DefaultMaxDrawablesPerProp),
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
    private static async Task<int> RunDiagnosticsAsync(CliOptions options)
    {
        using var progressWriter = new ConsoleProgressWriter();
        var result = await new DiagnosticBundleService().CreateAsync(
            [Required(options, "--folder")],
            Required(options, "--out"),
            CreateConsoleProgress(progressWriter));
        progressWriter.CompleteLine();
        Console.WriteLine(
            $"Created diagnostic bundle at {result.OutputPath} with {result.FileCount} file(s): "
            + $"{result.PreservedFileCount} preserved, {result.PlaceholderFileCount} replaced with placeholders.");
        if (result.SkippedLinkCount > 0)
        {
            Console.Error.WriteLine($"Skipped {result.SkippedLinkCount} unresolved or recursive link(s).");
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
  [--max-drawables-per-component <1-255>] [--max-drawables-per-prop <1-255>]
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
clothing-repacker diagnostics --folder <path> --out <bundle.zip>

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
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected positional argument '{arg}'.");
            }

            var separator = arg.IndexOf('=');
            if (separator >= 0)
            {
                var optionName = arg[..separator];
                if (KnownOptions.Contains(optionName))
                {
                    throw new InvalidOperationException($"Option '{optionName}' does not accept an attached value.");
                }

                throw new InvalidOperationException($"Unknown option '{optionName}'.");
            }

            if (!KnownOptions.Contains(arg))
            {
                throw new InvalidOperationException($"Unknown option '{arg}'.");
            }

            if (options.ContainsKey(arg) && !RepeatableOptions.Contains(arg))
            {
                throw new InvalidOperationException($"Option '{arg}' may only be specified once.");
            }

            if (ValueOptions.Contains(arg))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Option '{arg}' requires a value.");
                }

                options.Add(arg, args[++i]);
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Flag option '{arg}' does not accept a value.");
            }

            options.Add(arg, null);
        }

        ValidateLimit(options, "--max-drawables-per-component");
        ValidateLimit(options, "--max-drawables-per-prop");
        ValidateBoolean(options, "--include-ymt-xml");
        ValidateBoolean(options, "--include-debug-client");

        return options;
    }

    private static void ValidateCommandOptions(string command, CliOptions options)
    {
        var allowed = command switch
        {
            "analyze" => Allowed(
                "--no-version-check",
                "--resources",
                "--resource",
                "--generated-root",
                "--target-resource",
                "--target-prefix",
                "--female-prefix",
                "--male-prefix",
                "--max-drawables-per-component",
                "--max-drawables-per-prop",
                "--out",
                "--optimize-ymt-usage"),
            "build" => Allowed(
                "--no-version-check",
                "--plan",
                "--out",
                "--include-ymt-xml",
                "--include-debug-client"),
            "apply" => Allowed(
                "--no-version-check",
                "--plan",
                "--backup-root",
                "--copy-resources-to-output",
                "--include-ymt-xml",
                "--include-debug-client"),
            "restore" => Allowed(
                "--no-version-check",
                "--backup-manifest"),
            "validate" => Allowed(
                "--no-version-check",
                "--plan",
                "--resources",
                "--resource",
                "--generated-root"),
            "report" => Allowed(
                "--no-version-check",
                "--plan",
                "--out"),
            "export-xml" => Allowed(
                "--no-version-check",
                "--folder",
                "--overwrite"),
            "diagnostics" => Allowed(
                "--no-version-check",
                "--folder",
                "--out"),
            _ => throw new InvalidOperationException($"Unknown command '{command}'."),
        };

        foreach (var name in options.Names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!allowed.Contains(name))
            {
                throw new InvalidOperationException($"Option '{name}' is not valid for command '{command}'.");
            }
        }

        ValidateNonEmptyValues(options);
        switch (command)
        {
            case "analyze":
                ValidateAnalyzeOptions(options);
                break;
            case "build":
                Required(options, "--plan");
                Required(options, "--out");
                ValidateBoolean(options, "--include-ymt-xml");
                ValidateBoolean(options, "--include-debug-client");
                break;
            case "apply":
                Required(options, "--plan");
                Required(options, "--backup-root");
                ValidateBoolean(options, "--include-ymt-xml");
                ValidateBoolean(options, "--include-debug-client");
                break;
            case "restore":
                Required(options, "--backup-manifest");
                break;
            case "validate":
                ValidateValidateOptions(options);
                break;
            case "report":
                Required(options, "--plan");
                break;
            case "export-xml":
                Required(options, "--folder");
                break;
            case "diagnostics":
                Required(options, "--folder");
                Required(options, "--out");
                break;
        }
    }

    private static HashSet<string> Allowed(params string[] names)
        => new(names, StringComparer.OrdinalIgnoreCase);

    private static void ValidateAnalyzeOptions(CliOptions options)
    {
        Required(options, "--out");
        ValidateLimit(options, "--max-drawables-per-component");
        ValidateLimit(options, "--max-drawables-per-prop");

        var resources = options.ContainsKey("--resources");
        var resourceFolders = options.GetValues("--resource");
        if (resources && resourceFolders.Count > 0)
        {
            throw new InvalidOperationException("Use either --resources <parent> or repeated --resource <folder>, not both.");
        }

        if (!resources && resourceFolders.Count == 0)
        {
            throw new InvalidOperationException("Missing required option --resources or --resource.");
        }

        if (resourceFolders.Count > 0 && !options.ContainsKey("--generated-root"))
        {
            throw new InvalidOperationException("--generated-root is required when using --resource.");
        }

        if (resources && options.ContainsKey("--generated-root"))
        {
            throw new InvalidOperationException("--generated-root is only valid with --resource.");
        }
    }

    private static void ValidateValidateOptions(CliOptions options)
    {
        var hasPlan = options.ContainsKey("--plan");
        var hasResources = options.ContainsKey("--resources");
        var resourceFolders = options.GetValues("--resource");
        var inputModes = (hasPlan ? 1 : 0) + (hasResources ? 1 : 0) + (resourceFolders.Count > 0 ? 1 : 0);
        if (inputModes == 0)
        {
            throw new InvalidOperationException("validate requires --plan, --resources, or at least one --resource.");
        }

        if (inputModes > 1)
        {
            throw new InvalidOperationException("validate accepts exactly one input mode: --plan, --resources, or --resource.");
        }

        if (hasPlan)
        {
            Required(options, "--plan");
            return;
        }

        if (hasResources)
        {
            Required(options, "--resources");
            if (options.ContainsKey("--generated-root"))
            {
                throw new InvalidOperationException("--generated-root is only valid with --resource.");
            }

            return;
        }

        if (!options.ContainsKey("--generated-root"))
        {
            throw new InvalidOperationException("--generated-root is required when using --resource.");
        }

        Required(options, "--generated-root");
    }

    private static void ValidateNonEmptyValues(CliOptions options)
    {
        foreach (var name in options.Names.Where(ValueOptions.Contains))
        {
            foreach (var value in options.GetValues(name))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException($"Option '{name}' requires a non-empty value.");
                }
            }
        }
    }

    private static void ValidateLimit(CliOptions options, string name)
    {
        if (!options.TryGetValue(name, out var value))
        {
            return;
        }

        var parsed = ParseInt(value, fallback: 0);
        if (parsed is < 1 or > 255)
        {
            throw new InvalidOperationException($"Option '{name}' must be an integer from 1 through 255.");
        }
    }

    private static void ValidateBoolean(CliOptions options, string name)
    {
        if (options.TryGetValue(name, out var value))
        {
            ParseBool(value, fallback: true);
        }
    }

    private static string Required(CliOptions options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required option {name}.");

    private static int ParseInt(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid integer value '{value}'.");
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid Boolean value '{value}'. Expected true or false.");
    }

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
            "bundle-file" => $"{prefix} files bundled {progress.Current}{path}",
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

    public IReadOnlyCollection<string> Names
        => _values.Keys;

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
            ? values.Select(value => value ?? string.Empty).ToList()
            : [];
}
