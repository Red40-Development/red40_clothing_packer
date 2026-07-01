using System.Text.Json;

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
}
