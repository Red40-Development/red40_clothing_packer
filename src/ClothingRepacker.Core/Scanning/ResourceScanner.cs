using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Scanning;

public sealed class ResourceScanner
{
    private static readonly string[] StreamExtensions = [".ydd", ".ytd", ".yld", ".ymt", ".xml"];

    public IReadOnlyList<ResourceScanItem> ScanResources(string resourcesRoot)
    {
        var root = Path.GetFullPath(resourcesRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var directories = Directory.GetDirectories(root)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<ResourceScanItem>();
        foreach (var resourceDir in directories)
        {
            var resourceName = Path.GetFileName(resourceDir);
            var files = Directory.GetFiles(resourceDir, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            results.Add(new ResourceScanItem(
                resourceName,
                resourceDir,
                files.Where(IsYmtCandidate).ToList(),
                files.Where(IsShopMetaCandidate).ToList(),
                files.Where(IsStreamCandidate).Select(path => new StreamFile(
                    resourceName,
                    resourceDir,
                    path,
                    Path.GetFileName(path),
                    Path.GetExtension(path),
                    path.Contains($"{Path.DirectorySeparatorChar}stream{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))).ToList(),
                files.FirstOrDefault(IsManifest)));
        }

        return results;
    }

    private static bool IsManifest(string path)
        => Path.GetFileName(path).Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase)
           || Path.GetFileName(path).Equals("__resource.lua", StringComparison.OrdinalIgnoreCase);

    private static bool IsYmtCandidate(string path)
        => path.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsShopMetaCandidate(string path)
        => path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
           || (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
               && !path.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase));

    private static bool IsStreamCandidate(string path)
    {
        var extension = Path.GetExtension(path);
        if (!StreamExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Contains($"{Path.DirectorySeparatorChar}stream{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ResourceScanItem(
    string ResourceName,
    string ResourceRoot,
    IReadOnlyList<string> YmtFiles,
    IReadOnlyList<string> ShopMetaFiles,
    IReadOnlyList<StreamFile> StreamFiles,
    string? ManifestPath);
