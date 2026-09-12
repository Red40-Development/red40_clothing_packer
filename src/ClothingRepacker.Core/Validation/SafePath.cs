namespace ClothingRepacker.Core.Validation;

public static class SafePath
{
    public static string ResolveInsideRoot(string root, string relativePath, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("A path root is required.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A relative path is required.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Rooted paths are not allowed: {relativePath}");
        }

        if (ContainsTraversal(relativePath))
        {
            throw new InvalidOperationException($"Path traversal is not allowed: {relativePath}");
        }

        var fullRoot = Normalize(root);
        var fullPath = Normalize(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (PathsEqual(fullPath, fullRoot))
        {
            if (allowRoot)
            {
                return fullPath;
            }

            throw new InvalidOperationException($"Path must be inside root: {relativePath}");
        }

        if (!IsInside(fullPath, fullRoot))
        {
            throw new InvalidOperationException($"Path is outside root: {relativePath}");
        }

        return fullPath;
    }

    public static string RequireInside(string path, string root, bool allowEqual = true)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("A path and root are required.");
        }

        var fullPath = Normalize(path);
        var fullRoot = Normalize(root);
        if (PathsEqual(fullPath, fullRoot))
        {
            if (allowEqual)
            {
                return fullPath;
            }

            throw new InvalidOperationException($"Path must be strictly inside root: {path}");
        }

        if (!IsInside(fullPath, fullRoot))
        {
            throw new InvalidOperationException($"Path is outside root '{fullRoot}': {path}");
        }

        return fullPath;
    }

    public static bool IsInside(string path, string root)
        => Normalize(path).StartsWith(Normalize(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static bool PathsEqual(string left, string right)
        => Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    public static bool IsSafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || Path.IsPathRooted(value))
        {
            return false;
        }

        if (value.Contains('/') || value.Contains('\\') || value.Contains(':'))
        {
            return false;
        }

        return value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    public static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool ContainsTraversal(string path)
        => path.Replace('\\', '/').Split('/', StringSplitOptions.None)
            .Any(segment => segment is "." or "..");
}
