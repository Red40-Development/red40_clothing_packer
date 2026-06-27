using ClothingRepacker.Core.Models;

namespace ClothingRepacker.Core.Planning;

public sealed record SourceYmtContribution(
    SourceYmt Source,
    IReadOnlyDictionary<int, SourceIndexRange> ComponentRanges,
    IReadOnlyDictionary<int, SourceIndexRange> PropRanges);
