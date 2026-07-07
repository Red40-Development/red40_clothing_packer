using System.Text.Json;
using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Tests;

public class CliResourceOptionTests
{
    [Fact]
    public void ParserPreservesRepeatedResourceValues()
    {
        var options = ProgramEntry.ParseOptions(["--resource", "/tmp/a", "--resource", "/tmp/b"]);

        Assert.Equal(["/tmp/a", "/tmp/b"], options.GetValues("--resource"));
    }

    [Fact]
    public async Task AnalyzeWithRepeatedResourceOptionsWritesPlanWithBothResources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cli-explicit-resources-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var generatedRoot = Path.Combine(root, "generated");
        var first = Path.Combine(resources, "gang_flags");
        var second = Path.Combine(resources, "gang_outfits");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), first);
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_outfits"), second);
        var planPath = Path.Combine(root, "plan.json");

        var exitCode = await ProgramEntry.RunAsync([
            "--no-version-check",
            "analyze",
            "--resource",
            first,
            "--resource",
            second,
            "--generated-root",
            generatedRoot,
            "--target-resource",
            "zz_merged_clothing_meta",
            "--optimize-ymt-usage",
            "--out",
            planPath,
        ]);

        Assert.Equal(0, exitCode);
        using var stream = File.OpenRead(planPath);
        var plan = await JsonSerializer.DeserializeAsync<JsonElement>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        Assert.Equal(Path.GetFullPath(generatedRoot), plan.GetProperty("generatedResourcesRoot").GetString());
        Assert.Equal(2, plan.GetProperty("resourceRoots").GetArrayLength());
        Assert.True(plan.GetProperty("settings").GetProperty("optimizeYmtUsage").GetBoolean());
    }

    [Fact]
    public async Task AnalyzeWithResourceRequiresGeneratedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cli-missing-generated-root-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var exitCode = await ProgramEntry.RunAsync([
            "--no-version-check",
            "analyze",
            "--resource",
            root,
            "--out",
            Path.Combine(root, "plan.json"),
        ]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task AnalyzeRejectsMixedResourceInputModes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cli-mixed-input-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var exitCode = await ProgramEntry.RunAsync([
            "--no-version-check",
            "analyze",
            "--resources",
            root,
            "--resource",
            root,
            "--generated-root",
            root,
            "--out",
            Path.Combine(root, "plan.json"),
        ]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ReportCommandWritesTextToStdout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cli-report-stdout-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var planPath = Path.Combine(root, "plan.json");
        await WritePlanAsync(planPath);
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            var exitCode = await ProgramEntry.RunAsync([
                "--no-version-check",
                "report",
                "--plan",
                planPath,
            ]);

            Assert.Equal(0, exitCode);
            var output = writer.ToString();
            Assert.Contains("Target", output);
            Assert.Contains("Source Resource", output);
            Assert.Contains("mp_f_freemode_01_merged_f_001", output);
            Assert.Contains("/tmp/a.ymt", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task ReportCommandWritesTextToFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cli-report-file-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var planPath = Path.Combine(root, "plan.json");
        var reportPath = Path.Combine(root, "report.txt");
        await WritePlanAsync(planPath);

        var exitCode = await ProgramEntry.RunAsync([
            "--no-version-check",
            "report",
            "--plan",
            planPath,
            "--out",
            reportPath,
        ]);

        Assert.Equal(0, exitCode);
        var text = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("component", text);
        Assert.Contains("11 jbib", text);
        Assert.Contains("0-1", text);
    }

    private static async Task WritePlanAsync(string planPath)
    {
        var plan = new MergePlan
        {
            TargetResource = "zz_merged_clothing_meta",
            Settings = new MergePlanSettings
            {
                MaxDrawablesPerComponent = 10,
                MaxDrawablesPerProp = 5,
            },
            SourceYmts =
            [
                new SourceYmtSummary("pack_a", "/tmp/a.ymt", "mp_f_freemode_01", PedGender.Female, "pack_a", "mp_f_freemode_01_pack_a", "hash_a", [], []),
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
        };

        await using var stream = File.Create(planPath);
        await JsonSerializer.SerializeAsync(stream, plan, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }
}
