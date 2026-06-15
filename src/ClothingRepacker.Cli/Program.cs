using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using ClothingRepacker.Core;

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
        var service = new RepackerService(new XmlPassthroughYmtCodec());

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
        var resources = Required(options, "--resources");
        var targetResource = options.GetValueOrDefault("--target-resource") ?? "zz_merged_clothing_meta";
        var targetPrefix = options.GetValueOrDefault("--target-prefix") ?? "merged";
        var outPath = Required(options, "--out");
        var settings = new MergePlanSettings
        {
            TargetPrefix = targetPrefix,
            FemalePrefix = options.GetValueOrDefault("--female-prefix") ?? $"{targetPrefix}_f",
            MalePrefix = options.GetValueOrDefault("--male-prefix") ?? $"{targetPrefix}_m",
            MaxDrawablesPerType = ParseInt(options.GetValueOrDefault("--max-drawables-per-type"), ClothingConstants.DefaultMaxDrawablesPerType),
            ShopMetaMode = options.GetValueOrDefault("--shop-meta-mode") ?? "minimal",
        };

        var result = await service.AnalyzeAsync(resources, targetResource, settings);
        await service.SavePlanAsync(result.Plan, outPath);
        Console.WriteLine($"Analyzed {result.Plan.SourceYmts.Count} YMTs into {result.Plan.TargetCollections.Count} target collections.");
        if (result.Plan.Errors.Count > 0)
        {
            Console.WriteLine($"Plan contains {result.Plan.Errors.Count} error(s).");
            return 1;
        }

        return 0;
    }

    private static async Task<int> RunBuildAsync(RepackerService service, Dictionary<string, string?> options)
    {
        var plan = await service.LoadPlanAsync(Required(options, "--plan"));
        var result = await service.BuildAsync(plan, Required(options, "--out"));
        Console.WriteLine($"Wrote {result.WrittenFiles.Count} file(s) to {result.OutputRoot}.");
        return 0;
    }

    private static async Task<int> RunApplyAsync(RepackerService service, Dictionary<string, string?> options)
    {
        var plan = await service.LoadPlanAsync(Required(options, "--plan"));
        var entries = await service.ApplyAsync(plan, Required(options, "--backup-root"), options.ContainsKey("--yes"));
        Console.WriteLine($"Applied plan with {entries.Count} backup entries.");
        return 0;
    }

    private static async Task<int> RunRestoreAsync(RepackerService service, Dictionary<string, string?> options)
    {
        await service.RestoreAsync(Required(options, "--backup-manifest"), options.ContainsKey("--yes"));
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
            var result = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
            foreach (var error in result.Plan.Errors)
            {
                Console.Error.WriteLine(error);
            }

            Console.WriteLine(result.Plan.Errors.Count == 0 ? "Resources validated." : $"Resources have {result.Plan.Errors.Count} error(s).");
            return result.Plan.Errors.Count == 0 ? 0 : 1;
        }

        throw new InvalidOperationException("validate requires --plan or --resources.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
clothing-repacker analyze --resources <path> --target-resource <name> --out <plan.json>
clothing-repacker build --plan <plan.json> --out <folder>
clothing-repacker apply --plan <plan.json> --backup-root <folder> --yes
clothing-repacker restore --backup-manifest <backup-manifest.json> --yes
clothing-repacker validate --plan <plan.json>
clothing-repacker validate --resources <path>
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
}
