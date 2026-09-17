using System.IO.Compression;
using System.Text;
using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Services;

public sealed record DiagnosticBundleResult(
    string OutputPath,
    int FileCount,
    int PreservedFileCount,
    int PlaceholderFileCount,
    int SkippedLinkCount);

public sealed class DiagnosticBundleService
{
    private const string OperationName = "diagnostics";
    private static readonly HashSet<string> PreservedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".meta",
        ".ymt",
        ".xml",
        ".lua",
    };
    private static readonly byte[] PlaceholderContents = Encoding.UTF8.GetBytes(".");

    public async Task<DiagnosticBundleResult> CreateAsync(
        IReadOnlyList<string> sourceFolders,
        string outputPath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceFolders.Count == 0)
        {
            throw new InvalidOperationException("At least one clothing folder is required.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException("A diagnostic bundle output path is required.");
        }

        var sources = sourceFolders
            .Select(Path.GetFullPath)
            .Distinct(GetPathComparer())
            .ToList();
        foreach (var source in sources)
        {
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException($"Clothing folder does not exist: {source}");
            }
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullOutputPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The diagnostic bundle output path must end in .zip.");
        }

        foreach (var source in sources)
        {
            if (IsAtOrInside(fullOutputPath, source))
            {
                throw new InvalidOperationException("The diagnostic bundle must be saved outside the clothing folders being bundled.");
            }
        }

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("The diagnostic bundle output path must include a directory.");
        Directory.CreateDirectory(outputDirectory);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        var counts = new BundleCounts();
        progress?.Report(new OperationProgress(
            OperationName,
            "start",
            Message: $"Creating diagnostic bundle from {sources.Count} clothing folder{(sources.Count == 1 ? string.Empty : "s")}."));

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                var usedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rootName = GetUniqueRootName(source, usedRootNames);
                    archive.CreateEntry($"{rootName}/", CompressionLevel.SmallestSize);
                    await AddDirectoryAsync(
                        archive,
                        new DirectoryInfo(source),
                        rootName,
                        new HashSet<string>(GetPathComparer()),
                        counts,
                        progress,
                        cancellationToken);
                }
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }

        progress?.Report(new OperationProgress(
            OperationName,
            "complete",
            Current: counts.FileCount,
            Path: fullOutputPath,
            Message: $"Diagnostic bundle created with {counts.FileCount} file{(counts.FileCount == 1 ? string.Empty : "s")}.",
            WrittenFileCount: counts.FileCount,
            SkippedCount: counts.SkippedLinkCount));

        return new DiagnosticBundleResult(
            fullOutputPath,
            counts.FileCount,
            counts.PreservedFileCount,
            counts.PlaceholderFileCount,
            counts.SkippedLinkCount);
    }

    private static async Task AddDirectoryAsync(
        ZipArchive archive,
        DirectoryInfo directory,
        string entryRoot,
        HashSet<string> ancestorPaths,
        BundleCounts counts,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetFullPath(directory.FullName);
        if (!ancestorPaths.Add(directoryPath))
        {
            counts.SkippedLinkCount++;
            return;
        }

        try
        {
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var effectiveItem = ResolveLink(item, counts);
                if (effectiveItem is null)
                {
                    continue;
                }

                var entryPath = $"{entryRoot}/{item.Name}";
                if (effectiveItem is DirectoryInfo childDirectory)
                {
                    if (item.Name.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    archive.CreateEntry($"{entryPath}/", CompressionLevel.SmallestSize);
                    await AddDirectoryAsync(
                        archive,
                        childDirectory,
                        entryPath,
                        ancestorPaths,
                        counts,
                        progress,
                        cancellationToken);
                    continue;
                }

                if (effectiveItem is not FileInfo file)
                {
                    continue;
                }

                var entry = archive.CreateEntry(entryPath, CompressionLevel.SmallestSize);
                await using var entryStream = entry.Open();
                if (PreservedExtensions.Contains(Path.GetExtension(item.Name)))
                {
                    await using var sourceStream = new FileStream(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await sourceStream.CopyToAsync(entryStream, cancellationToken);
                    counts.PreservedFileCount++;
                }
                else
                {
                    await entryStream.WriteAsync(PlaceholderContents, cancellationToken);
                    counts.PlaceholderFileCount++;
                }

                counts.FileCount++;
                progress?.Report(new OperationProgress(
                    OperationName,
                    "bundle-file",
                    Current: counts.FileCount,
                    Path: file.FullName,
                    WrittenFileCount: counts.FileCount,
                    SkippedCount: counts.SkippedLinkCount));
            }
        }
        finally
        {
            ancestorPaths.Remove(directoryPath);
        }
    }

    private static FileSystemInfo? ResolveLink(FileSystemInfo item, BundleCounts counts)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return item;
        }

        try
        {
            var target = item.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null && target.Exists)
            {
                return target;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        counts.SkippedLinkCount++;
        return null;
    }

    private static string GetUniqueRootName(string sourcePath, HashSet<string> usedNames)
    {
        var trimmedPath = Path.TrimEndingDirectorySeparator(sourcePath);
        var baseName = Path.GetFileName(trimmedPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "clothing-folder";
        }

        var candidate = baseName;
        for (var suffix = 2; !usedNames.Add(candidate); suffix++)
        {
            candidate = $"{baseName}-{suffix}";
        }

        return candidate;
    }

    private static bool IsAtOrInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class BundleCounts
    {
        public int FileCount { get; set; }
        public int PreservedFileCount { get; set; }
        public int PlaceholderFileCount { get; set; }
        public int SkippedLinkCount { get; set; }
    }
}
