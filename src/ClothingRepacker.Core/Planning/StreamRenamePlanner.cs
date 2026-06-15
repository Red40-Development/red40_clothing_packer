using System.Text.RegularExpressions;
using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Planning;

public sealed class StreamRenamePlanner
{
    public IReadOnlyList<StreamRename> BuildRenamePlan(
        IReadOnlyList<DrawableMapping> drawableMappings,
        IReadOnlyList<PropMapping> propMappings,
        IReadOnlyList<StreamFile> streamFiles)
    {
        var renames = new List<StreamRename>();
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in drawableMappings)
        {
            if (!ClothingConstants.ComponentPrefixes.TryGetValue(mapping.ComponentId, out var prefix))
            {
                continue;
            }

            var drawablePattern = new Regex($"^{Regex.Escape(mapping.SourceFullCollection)}\\^{Regex.Escape(prefix)}_{mapping.OldDrawableIndex:000}(?<suffix>(?:_[^.]+)?\\.(?:ydd|yld))$", RegexOptions.IgnoreCase);
            var texturePattern = new Regex($"^{Regex.Escape(mapping.SourceFullCollection)}\\^{Regex.Escape(prefix)}_diff_{mapping.OldDrawableIndex:000}(?<suffix>_.+\\.ytd)$", RegexOptions.IgnoreCase);

            foreach (var file in streamFiles.Where(file => !usedSources.Contains(file.FullPath)))
            {
                var newName = ReplaceFilename(file.FileName, drawablePattern, $"{mapping.TargetFullCollection}^{prefix}_{mapping.NewDrawableIndex:000}${{suffix}}")
                    ?? ReplaceFilename(file.FileName, texturePattern, $"{mapping.TargetFullCollection}^{prefix}_diff_{mapping.NewDrawableIndex:000}${{suffix}}");

                if (newName is null)
                {
                    continue;
                }

                usedSources.Add(file.FullPath);
                renames.Add(new StreamRename(
                    file.FullPath,
                    Path.Combine(Path.GetDirectoryName(file.FullPath)!, newName),
                    file.ResourceName,
                    $"component {mapping.ComponentId} drawable {mapping.OldDrawableIndex} -> {mapping.NewDrawableIndex}",
                    null,
                    null,
                    true));
            }
        }

        foreach (var mapping in propMappings)
        {
            if (!ClothingConstants.PropPrefixes.TryGetValue(mapping.AnchorId, out var prefix))
            {
                continue;
            }

            var drawablePattern = new Regex($"^{Regex.Escape(mapping.SourceFullCollection)}\\^{Regex.Escape(prefix)}_{mapping.OldPropIndex:000}(?<suffix>\\.(?:ydd|yld))$", RegexOptions.IgnoreCase);
            var texturePattern = new Regex($"^{Regex.Escape(mapping.SourceFullCollection)}\\^{Regex.Escape(prefix)}_diff_{mapping.OldPropIndex:000}(?<suffix>_.+\\.ytd)$", RegexOptions.IgnoreCase);

            foreach (var file in streamFiles.Where(file => !usedSources.Contains(file.FullPath)))
            {
                var newName = ReplaceFilename(file.FileName, drawablePattern, $"{mapping.TargetFullCollection}^{prefix}_{mapping.NewPropIndex:000}${{suffix}}")
                    ?? ReplaceFilename(file.FileName, texturePattern, $"{mapping.TargetFullCollection}^{prefix}_diff_{mapping.NewPropIndex:000}${{suffix}}");

                if (newName is null)
                {
                    continue;
                }

                usedSources.Add(file.FullPath);
                renames.Add(new StreamRename(
                    file.FullPath,
                    Path.Combine(Path.GetDirectoryName(file.FullPath)!, newName),
                    file.ResourceName,
                    $"prop anchor {mapping.AnchorId} drawable {mapping.OldPropIndex} -> {mapping.NewPropIndex}",
                    null,
                    null,
                    true));
            }
        }

        return renames;
    }

    public IReadOnlyList<string> ValidateCollisions(IReadOnlyList<StreamRename> renames)
    {
        var errors = new List<string>();
        var collisions = renames.GroupBy(rename => rename.TargetPath, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1);
        foreach (var collision in collisions)
        {
            errors.Add($"Planned target path collision: {collision.Key}");
        }

        foreach (var rename in renames)
        {
            if (File.Exists(rename.TargetPath) && !rename.SourcePath.Equals(rename.TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Target already exists: {rename.TargetPath}");
            }
        }

        return errors;
    }

    private static string? ReplaceFilename(string fileName, Regex pattern, string replacement)
    {
        if (!pattern.IsMatch(fileName))
        {
            return null;
        }

        return pattern.Replace(fileName, replacement);
    }
}
