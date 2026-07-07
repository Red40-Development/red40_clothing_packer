namespace ClothingRepacker.Core.Scanning;

public static class ResourceFolderDiscovery
{
    public static IReadOnlyList<string> ExpandSelectedResourceFolders(IEnumerable<string> roots, bool includeMissing = false)
    {
        var resources = new List<string>();
        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath))
        {
            resources.AddRange(ExpandSelectedResourceFolder(root, includeMissing));
        }

        return resources
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<string> ExpandResourcesRootDirectories(IEnumerable<string> directories)
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

            resources.Add(directory);
        }

        return resources;
    }

    private static IReadOnlyList<string> ExpandSelectedResourceFolder(string root, bool includeMissing)
    {
        if (!Directory.Exists(root))
        {
            return includeMissing ? [root] : [];
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

        return resources.Count > 0 ? resources : [root];
    }

    public static bool IsResourceFolder(string path)
        => File.Exists(Path.Combine(path, "fxmanifest.lua"))
           || File.Exists(Path.Combine(path, "__resource.lua"));

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

    private static bool IsBracketFolder(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains('[') && name.Contains(']');
    }
}
