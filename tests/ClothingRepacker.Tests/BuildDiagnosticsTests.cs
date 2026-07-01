using ClothingRepacker.CodeWalker;
using ClothingRepacker.Core.Codecs;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Services;
using System.Xml.Linq;

namespace ClothingRepacker.Tests;

public class BuildDiagnosticsTests
{
    [Fact]
    public async Task BuildWritesFailedXmlAndAddsContextForEncodeOverflow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"build-diagnostics-test-{Guid.NewGuid():N}");
        var resources = Path.Combine(root, "resources");
        var outputRoot = Path.Combine(root, "output");
        TestFixturePaths.CopyDirectory(TestFixturePaths.ResourceDirectory("gang_flags"), Path.Combine(resources, "gang_flags"));

        var normalCodec = new CompositeYmtCodec(new XmlPassthroughYmtCodec(), new CodeWalkerYmtCodec());
        var analyzeService = new RepackerService(normalCodec);
        var analyze = await analyzeService.AnalyzeAsync(resources, "zz_merged_clothing_meta", new MergePlanSettings());
        var targetPlan = analyze.Plan.TargetCollections[0];
        var expectedYmtPath = Path.Combine(outputRoot, targetPlan.OutputYmtPath.Replace('/', Path.DirectorySeparatorChar));
        var expectedFailedXmlPath = expectedYmtPath + ".failed.xml";

        var buildService = new RepackerService(new OverflowOnEncodeCodec(normalCodec));
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => buildService.BuildAsync(analyze.Plan, outputRoot, new BuildOptions { IncludeYmtXml = false }));

        Assert.Contains(targetPlan.FullCollectionName, exception.Message);
        Assert.Contains(expectedYmtPath, exception.Message);
        Assert.Contains("signed 32-bit integer", exception.Message);
        Assert.Contains(expectedFailedXmlPath, exception.Message);
        Assert.IsType<OverflowException>(exception.InnerException);
        Assert.True(File.Exists(expectedFailedXmlPath));
    }

    private sealed class OverflowOnEncodeCodec : IYmtCodec
    {
        private readonly IYmtCodec _inner;

        public OverflowOnEncodeCodec(IYmtCodec inner)
        {
            _inner = inner;
        }

        public Task<XDocument> DecodeToXmlAsync(string ymtPath, CancellationToken cancellationToken = default)
            => _inner.DecodeToXmlAsync(ymtPath, cancellationToken);

        public Task EncodeFromXmlAsync(XDocument xml, string outputYmtPath, CancellationToken cancellationToken = default)
            => throw new OverflowException("Value was either too large or too small for an Int32.");
    }
}
