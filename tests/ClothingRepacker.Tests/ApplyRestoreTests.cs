using System.Text.Json;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;

namespace ClothingRepacker.Tests;

public class ApplyRestoreTests
{
    [Fact]
    public async Task ApplyAndRestoreRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repacker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var resources = Path.Combine(root, "resources");
        var resource = Path.Combine(resources, "red40");
        var stream = Path.Combine(resource, "stream");
        Directory.CreateDirectory(stream);
        await File.WriteAllTextAsync(Path.Combine(resource, "fxmanifest.lua"), "fx_version 'cerulean'");

        var sourceYmt = Path.Combine(stream, "mp_f_freemode_01_red40_clothes.ymt.xml");
        File.Copy(Fixture("mp_f_freemode_01_red40_clothes.ymt.xml"), sourceYmt);
        var sourceDrawable = Path.Combine(stream, "mp_f_freemode_01_red40_clothes^jbib_000_u.ydd");
        await File.WriteAllTextAsync(sourceDrawable, "drawable");

        var service = new RepackerService(new XmlPassthroughYmtCodec());
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var planPath = Path.Combine(root, "plan.json");
        await service.SavePlanAsync(analyze.Plan, planPath);
        var plan = await service.LoadPlanAsync(planPath);

        var backupRoot = Path.Combine(root, "backups");
        var entries = await service.ApplyAsync(plan, backupRoot, yes: true);

        Assert.DoesNotContain(sourceYmt, Directory.GetFiles(stream, "*.xml", SearchOption.AllDirectories));
        Assert.Contains(entries, entry => entry.Kind == "old-ymt");
        Assert.True(Directory.Exists(Path.Combine(root, "zz_merged_clothing_meta")));

        var manifest = Directory.GetFiles(backupRoot, "backup-manifest.json", SearchOption.AllDirectories).Single();
        await service.RestoreAsync(manifest, yes: true);

        Assert.True(File.Exists(sourceYmt));
        Assert.True(File.Exists(sourceDrawable));
    }

    private static string Fixture(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ymts", fileName);
}
