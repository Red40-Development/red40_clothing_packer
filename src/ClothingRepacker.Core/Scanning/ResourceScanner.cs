using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Scanning;

public sealed class ResourceScanner
{
    private static readonly string[] StreamExtensions = [".ydd", ".ytd", ".yld", ".ymt", ".xml"];

    public IReadOnlyList<ResourceScanItem> ScanResources(
        string resourcesRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(resourcesRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var directories = Directory.GetDirectories(root)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resourceDirectories = ExpandResourcesRootDirectories(directories);

        var results = new List<ResourceScanItem>();
        for (var index = 0; index < resourceDirectories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resourceDir = resourceDirectories[index];
            progress?.Report(new OperationProgress(
                "analyze",
                "scan-resource",
                index,
                resourceDirectories.Count,
                resourceDir,
                $"Scanning resource {index + 1}/{resourceDirectories.Count}: {Path.GetFileName(resourceDir)}."));

            results.Add(ScanResourceFolder(resourceDir, cancellationToken));

            progress?.Report(new OperationProgress(
                "analyze",
                "scan-resource",
                index + 1,
                resourceDirectories.Count,
                resourceDir,
                $"Scanned resource {index + 1}/{resourceDirectories.Count}: {Path.GetFileName(resourceDir)}."));
        }

        return results;
    }

    public IReadOnlyList<ResourceScanItem> ScanResourceFolders(
        IEnumerable<string> resourceFolders,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var roots = new List<string>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in resourceFolders.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath))
        {
            foreach (var resourceRoot in ExpandSelectedResourceFolder(root))
            {
                if (seenRoots.Add(resourceRoot))
                {
                    roots.Add(resourceRoot);
                }
            }
        }

        var results = new List<ResourceScanItem>();
        for (var index = 0; index < roots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[index];
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException(root);
            }

            progress?.Report(new OperationProgress(
                "analyze",
                "scan-resource",
                index,
                roots.Count,
                root,
                $"Scanning resource {index + 1}/{roots.Count}: {Path.GetFileName(root)}."));

            results.Add(ScanResourceFolder(root, cancellationToken));

            progress?.Report(new OperationProgress(
                "analyze",
                "scan-resource",
                index + 1,
                roots.Count,
                root,
                $"Scanned resource {index + 1}/{roots.Count}: {Path.GetFileName(root)}."));
        }

        return results;
    }

    private static ResourceScanItem ScanResourceFolder(string resourceDir, CancellationToken cancellationToken)
    {
        var resourceName = Path.GetFileName(resourceDir);
        var files = Directory.GetFiles(resourceDir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();

        return new ResourceScanItem(
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
            files.FirstOrDefault(IsManifest));
    }

    private static IReadOnlyList<string> ExpandResourcesRootDirectories(IReadOnlyList<string> directories)
    {
        var resources = new List<string>();
        foreach (var directory in directories)
        {
            if (IsBracketFolder(directory))
            {
                var childResources = FindResourceFoldersUnder(directory);
                resources.AddRange(childResources.Count > 0 ? childResources : [directory]);
                continue;
            }

            if (IsResourceFolder(directory))
            {
                resources.Add(directory);
                continue;
            }

            resources.Add(directory);
        }

        return resources;
    }

    private static IReadOnlyList<string> ExpandSelectedResourceFolder(string root)
    {
        if (!Directory.Exists(root))
        {
            return [root];
        }

        if (IsBracketFolder(root))
        {
            var childResources = FindResourceFoldersUnder(root);
            return childResources.Count > 0 ? childResources : [root];
        }

        if (IsResourceFolder(root))
        {
            return [root];
        }

        var resources = new List<string>();
        resources.AddRange(Directory.GetDirectories(root)
            .Where(IsResourceFolder)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        foreach (var bracketFolder in Directory.GetDirectories(root)
                     .Where(IsBracketFolder)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            resources.AddRange(FindResourceFoldersUnder(bracketFolder));
        }

        var expanded = resources
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return expanded.Count > 0 ? expanded : [root];
    }

    private static IReadOnlyList<string> FindResourceFoldersUnder(string root)
    {
        var resources = new List<string>();
        foreach (var directory in Directory.GetDirectories(root)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (IsResourceFolder(directory))
            {
                resources.Add(directory);
                continue;
            }

            resources.AddRange(FindResourceFoldersUnder(directory));
        }

        return resources;
    }

    private static bool IsResourceFolder(string path)
        => File.Exists(Path.Combine(path, "fxmanifest.lua"))
           || File.Exists(Path.Combine(path, "__resource.lua"));

    private static bool IsBracketFolder(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains('[') && name.Contains(']');
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
