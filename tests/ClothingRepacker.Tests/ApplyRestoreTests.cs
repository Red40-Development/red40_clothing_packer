using System.Text.Json;
using ClothingRepacker.CodeWalker;
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
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), Path.Combine(resources, "gang_flags"));

        var stream = Path.Combine(resources, "gang_flags", "stream");
        var sourceYmt = Path.Combine(stream, "mp_f_freemode_01_mp_f_gang_flags.ymt");
        var sourceDrawable = Path.Combine(stream, "mp_f_freemode_01_mp_f_gang_flags^decl_000_u.ydd");
        await File.WriteAllTextAsync(sourceDrawable, "drawable");

        var service = new RepackerService(new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec()));
        var analyze = await service.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var planPath = Path.Combine(root, "plan.json");
        await service.SavePlanAsync(analyze.Plan, planPath);
        var plan = await service.LoadPlanAsync(planPath);

        var backupRoot = Path.Combine(root, "backups");
        var entries = await service.ApplyAsync(plan, backupRoot);

        Assert.False(File.Exists(sourceYmt));
        Assert.Contains(entries, entry => entry.Kind == "old-ymt");
        Assert.True(Directory.Exists(Path.Combine(root, "zz_merged_clothing_meta")));

        var manifest = Directory.GetFiles(backupRoot, "backup-manifest.json", SearchOption.AllDirectories).Single();
        await service.RestoreAsync(manifest);

        Assert.True(File.Exists(sourceYmt));
        Assert.True(File.Exists(sourceDrawable));
    }
}
