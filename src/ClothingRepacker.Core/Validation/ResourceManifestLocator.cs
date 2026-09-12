namespace ClothingRepacker.Core.Validation;

public static class ResourceManifestLocator
{
    private static readonly string[] ManifestNames = ["fxmanifest.lua", "__resource.lua"];

    public static string? Find(string resourceRoot)
    {
        foreach (var manifestName in ManifestNames)
        {
            var path = SafePath.ResolveInsideRoot(resourceRoot, manifestName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
