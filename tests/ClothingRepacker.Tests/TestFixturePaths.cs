namespace ClothingRepacker.Tests;

internal static class TestFixturePaths
{
    public static string Ymt(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ymts", fileName);

    public static string Meta(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Metas", fileName);

    public static string ResourceFile(string relativePath)
        => Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Resources",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string ResourceDirectory(string resourceName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Resources", resourceName);

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
