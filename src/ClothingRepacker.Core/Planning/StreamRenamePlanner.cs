using System.Text.RegularExpressions;
using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Planning;

public sealed class StreamRenamePlanner
{
    private static readonly Regex FilenamePattern = new(
        @"^(?<collection>.+)\^(?<prefix>head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib|p_head|p_eyes|p_ears|p_lwrist|p_rwrist)(?:(?:_(?<drawableIndex>[0-9]{3})(?<drawableSuffix>(?:_[^.]+)?\.(?:ydd|yld)))|(?:_diff_(?<textureIndex>[0-9]{3})(?<textureSuffix>_.+\.ytd)))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly List<string> _planningErrors = [];

    /// <summary>
    /// Gets the number of stream filenames classified during the most recent plan build.
    /// This is an observable invariant: every supplied filename is classified exactly once.
    /// </summary>
    public int ParsedFilenameCount { get; private set; }

    public IReadOnlyList<StreamRename> BuildRenamePlan(
        IReadOnlyList<DrawableMapping> drawableMappings,
        IReadOnlyList<PropMapping> propMappings,
        IReadOnlyList<StreamFile> streamFiles)
    {
        _planningErrors.Clear();
        ParsedFilenameCount = 0;

        var index = BuildFilenameIndex(streamFiles);
        var claims = new List<StreamClaim>();

        for (var mappingIndex = 0; mappingIndex < drawableMappings.Count; mappingIndex++)
        {
            var mapping = drawableMappings[mappingIndex];
            if (!ClothingConstants.ComponentPrefixes.TryGetValue(mapping.ComponentId, out var prefix))
            {
                continue;
            }

            AddClaims(
                index,
                new FilenameKey(mapping.SourceFullCollection, prefix, mapping.OldDrawableIndex, IsTexture: false),
                mappingIndex,
                $"component {mapping.ComponentId} drawable {mapping.OldDrawableIndex} -> {mapping.NewDrawableIndex}",
                targetName => $"{mapping.TargetFullCollection}^{prefix}_{mapping.NewDrawableIndex:000}{targetName}",
                claims);
            AddClaims(
                index,
                new FilenameKey(mapping.SourceFullCollection, prefix, mapping.OldDrawableIndex, IsTexture: true),
                mappingIndex,
                $"component {mapping.ComponentId} drawable {mapping.OldDrawableIndex} -> {mapping.NewDrawableIndex}",
                targetName => $"{mapping.TargetFullCollection}^{prefix}_diff_{mapping.NewDrawableIndex:000}{targetName}",
                claims);
        }

        for (var mappingIndex = 0; mappingIndex < propMappings.Count; mappingIndex++)
        {
            var mapping = propMappings[mappingIndex];
            if (!ClothingConstants.PropPrefixes.TryGetValue(mapping.AnchorId, out var prefix))
            {
                continue;
            }

            foreach (var collectionPair in BuildPropCollectionPairs(mapping))
            {
                AddClaims(
                    index,
                    new FilenameKey(collectionPair.Source, prefix, mapping.OldPropIndex, IsTexture: false),
                    mappingIndex,
                    $"prop anchor {mapping.AnchorId} drawable {mapping.OldPropIndex} -> {mapping.NewPropIndex}",
                    targetName => $"{collectionPair.Target}^{prefix}_{mapping.NewPropIndex:000}{targetName}",
                    claims,
                    mappingKind: "prop");
                AddClaims(
                    index,
                    new FilenameKey(collectionPair.Source, prefix, mapping.OldPropIndex, IsTexture: true),
                    mappingIndex,
                    $"prop anchor {mapping.AnchorId} drawable {mapping.OldPropIndex} -> {mapping.NewPropIndex}",
                    targetName => $"{collectionPair.Target}^{prefix}_diff_{mapping.NewPropIndex:000}{targetName}",
                    claims,
                    mappingKind: "prop");
            }
        }

        var renames = new List<StreamRename>();
        foreach (var sourceGroup in claims
                     .GroupBy(claim => claim.File.FullPath, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            var distinctOwners = sourceGroup
                .Select(claim => claim.Owner)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(owner => owner, StringComparer.Ordinal)
                .ToList();
            if (distinctOwners.Count > 1)
            {
                var details = sourceGroup
                    .Select(claim => $"{claim.Owner} ({claim.Reason}) -> {Path.Combine(Path.GetDirectoryName(claim.File.FullPath)!, claim.TargetName)}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(detail => detail, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(detail => detail, StringComparer.Ordinal);
                _planningErrors.Add(
                    $"Ambiguous stream rename source claim: {sourceGroup.Key} is claimed by {string.Join("; ", details)}");
                continue;
            }

            var claim = sourceGroup
                .OrderBy(item => item.Sequence)
                .First();
            renames.Add(new StreamRename(
                claim.File.FullPath,
                Path.Combine(Path.GetDirectoryName(claim.File.FullPath)!, claim.TargetName),
                claim.File.ResourceName,
                claim.Reason,
                null,
                null,
                true));
        }

        return renames
            .OrderBy(rename => rename.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rename => rename.SourcePath, StringComparer.Ordinal)
            .ThenBy(rename => rename.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rename => rename.TargetPath, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<string> ValidateCollisions(IReadOnlyList<StreamRename> renames)
    {
        var errors = new List<string>(_planningErrors);
        var collisions = renames
            .GroupBy(rename => rename.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal);
        foreach (var collision in collisions)
        {
            errors.Add($"Planned target path collision: {collision.Key}");
        }

        foreach (var rename in renames
                     .OrderBy(rename => rename.TargetPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(rename => rename.TargetPath, StringComparer.Ordinal)
                     .ThenBy(rename => rename.SourcePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(rename => rename.SourcePath, StringComparer.Ordinal))
        {
            if (File.Exists(rename.TargetPath) && !rename.SourcePath.Equals(rename.TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Target already exists: {rename.TargetPath}");
            }
        }

        return errors
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(error => error, StringComparer.OrdinalIgnoreCase)
            .ThenBy(error => error, StringComparer.Ordinal)
            .ToList();
    }

    private Dictionary<FilenameKey, List<IndexedStreamFile>> BuildFilenameIndex(
        IReadOnlyList<StreamFile> streamFiles)
    {
        var index = new Dictionary<FilenameKey, List<IndexedStreamFile>>(FilenameKeyComparer.Instance);
        foreach (var streamFile in streamFiles)
        {
            ParsedFilenameCount++;
            var match = FilenamePattern.Match(streamFile.FileName);
            if (!match.Success)
            {
                continue;
            }

            var isTexture = match.Groups["textureIndex"].Success;
            var indexGroup = isTexture ? match.Groups["textureIndex"] : match.Groups["drawableIndex"];
            var suffixGroup = isTexture ? match.Groups["textureSuffix"] : match.Groups["drawableSuffix"];
            var key = new FilenameKey(
                match.Groups["collection"].Value,
                match.Groups["prefix"].Value,
                int.Parse(indexGroup.Value, System.Globalization.CultureInfo.InvariantCulture),
                isTexture);
            if (!index.TryGetValue(key, out var entries))
            {
                entries = [];
                index.Add(key, entries);
            }

            entries.Add(new IndexedStreamFile(streamFile, suffixGroup.Value));
        }

        return index;
    }

    private static void AddClaims(
        IReadOnlyDictionary<FilenameKey, List<IndexedStreamFile>> index,
        FilenameKey key,
        int mappingIndex,
        string reason,
        Func<string, string> targetNameFactory,
        ICollection<StreamClaim> claims,
        string mappingKind = "component")
    {
        if (!index.TryGetValue(key, out var entries))
        {
            return;
        }

        foreach (var entry in entries)
        {
            claims.Add(new StreamClaim(
                entry.File,
                targetNameFactory(entry.Suffix),
                reason,
                $"{mappingKind}:{mappingIndex}",
                claims.Count));
    }
    }

    private static IReadOnlyList<(string Source, string Target)> BuildPropCollectionPairs(PropMapping mapping)
    {
        var pairs = new List<(string Source, string Target)>();
        AddPairIfUnique(pairs, mapping.SourceFullCollection, mapping.TargetFullCollection);
        AddPairIfUnique(
            pairs,
            BuildPropFullCollection(mapping.PedBaseName, mapping.SourceCollection),
            BuildPropFullCollection(mapping.PedBaseName, mapping.TargetCollection));
        return pairs;
    }

    private static void AddPairIfUnique(
        ICollection<(string Source, string Target)> pairs,
        string source,
        string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)
            || pairs.Any(pair => pair.Source.Equals(source, StringComparison.OrdinalIgnoreCase)
                && pair.Target.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        pairs.Add((source, target));
    }

    private static string BuildPropFullCollection(string pedBaseName, string collectionName)
        => string.IsNullOrWhiteSpace(collectionName)
            ? $"{pedBaseName}_p"
            : $"{pedBaseName}_p_{collectionName}";

    private readonly record struct FilenameKey(
        string Collection,
        string Prefix,
        int Index,
        bool IsTexture);

    private sealed record IndexedStreamFile(StreamFile File, string Suffix);

    private sealed record StreamClaim(
        StreamFile File,
        string TargetName,
        string Reason,
        string Owner,
        int Sequence);

    private sealed class FilenameKeyComparer : IEqualityComparer<FilenameKey>
    {
        public static FilenameKeyComparer Instance { get; } = new();

        public bool Equals(FilenameKey x, FilenameKey y)
            => x.Index == y.Index
                && x.IsTexture == y.IsTexture
                && x.Collection.Equals(y.Collection, StringComparison.OrdinalIgnoreCase)
                && x.Prefix.Equals(y.Prefix, StringComparison.OrdinalIgnoreCase);


        public int GetHashCode(FilenameKey obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Collection),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Prefix),
                obj.Index,
                obj.IsTexture);
    }
}
