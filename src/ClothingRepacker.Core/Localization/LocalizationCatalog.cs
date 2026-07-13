using System.Collections.ObjectModel;

namespace ClothingRepacker.Core.Localization;

public sealed record LocalizationCatalog(
    string Locale,
    string DisplayName,
    IReadOnlyDictionary<string, string> Entries)
{
    public static LocalizationCatalog Create(string locale, string displayName, IDictionary<string, string> entries)
        => new(locale, displayName, new ReadOnlyDictionary<string, string>(entries));
}

public sealed record LocalizedDiagnostic(
    string Code,
    IReadOnlyDictionary<string, object?> Arguments,
    string FallbackText)
{
    public static LocalizedDiagnostic Legacy(string text)
        => new("legacy", new Dictionary<string, object?>(), text);
}

public sealed record LocalizationOption(string? Locale, string DisplayName, bool IsSystemDefault);
