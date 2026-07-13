using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClothingRepacker.Core.Localization;

public sealed class LocalizationService : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly Regex PlaceholderPattern = new(@"\{(?<name>[A-Za-z0-9_.-]+)\}", RegexOptions.Compiled);
    private readonly Dictionary<string, LocalizationCatalog> _catalogs;
    private readonly LocalizationCatalog _english;
    private string? _overrideLocale;

    public LocalizationService(Assembly? assembly = null)
    {
        _catalogs = LoadCatalogs(assembly ?? typeof(LocalizationService).Assembly);
        if (!_catalogs.TryGetValue("en", out _english!))
        {
            throw new InvalidOperationException("The English localization catalog is missing or invalid.");
        }
    }

    public event EventHandler? LanguageChanged;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Translate(key);

    public IReadOnlyList<LocalizationCatalog> Catalogs
        => _catalogs.Values.OrderBy(catalog => catalog.Locale, StringComparer.OrdinalIgnoreCase).ToList();

    public string? OverrideLocale
    {
        get => _overrideLocale;
        set
        {
            var normalized = NormalizeLocale(value);
            if (string.Equals(_overrideLocale, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _overrideLocale = normalized;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ActiveLocale)));
        }
    }

    public string ActiveLocale => ResolveCatalog().Locale;

    public CultureInfo ActiveCulture
    {
        get
        {
            try
            {
                return CultureInfo.GetCultureInfo(ActiveLocale);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.InvariantCulture;
            }
        }
    }

    public IReadOnlyList<LocalizationOption> GetOptions()
    {
        var options = new List<LocalizationOption>
        {
            new(null, Translate("language.systemDefault"), true),
        };
        options.AddRange(Catalogs.Select(catalog => new LocalizationOption(catalog.Locale, catalog.DisplayName, false)));
        return options;
    }

    public string Translate(string key, IReadOnlyDictionary<string, object?>? arguments = null, string? fallback = null)
    {
        var template = FindEntry(ResolveCatalog(), key)
            ?? FindEntry(_english, key)
            ?? fallback
            ?? key;
        return Format(template, arguments);
    }

    public string Translate(LocalizedDiagnostic diagnostic)
        => Translate(diagnostic.Code, diagnostic.Arguments, diagnostic.FallbackText);

    public bool HasKey(string key)
        => _english.Entries.ContainsKey(key);

    public static IReadOnlySet<string> Placeholders(string value)
        => PlaceholderPattern.Matches(value)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private LocalizationCatalog ResolveCatalog()
    {
        var requested = NormalizeLocale(_overrideLocale) ?? NormalizeLocale(CultureInfo.CurrentUICulture.Name) ?? "en";
        foreach (var candidate in LocaleCandidates(requested))
        {
            if (_catalogs.TryGetValue(candidate, out var catalog))
            {
                return catalog;
            }
        }

        return _english;
    }

    private static IEnumerable<string> LocaleCandidates(string locale)
    {
        yield return locale;
        var separator = locale.IndexOf('-', StringComparison.Ordinal);
        if (separator > 0)
        {
            yield return locale[..separator];
        }

        var underscore = locale.IndexOf('_', StringComparison.Ordinal);
        if (underscore > 0)
        {
            yield return locale[..underscore];
        }
    }

    private static string? NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return null;
        }

        return locale.Trim().Replace('_', '-').ToLowerInvariant();
    }

    private static string? FindEntry(LocalizationCatalog catalog, string key)
        => catalog.Entries.TryGetValue(key, out var value) ? value : null;

    private string Format(string template, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return template;
        }

        return PlaceholderPattern.Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            if (!arguments.TryGetValue(name, out var value))
            {
                return match.Value;
            }

            return value is JsonElement element
                ? element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString()
                : Convert.ToString(value, ActiveCulture) ?? string.Empty;
        });
    }

    private static Dictionary<string, LocalizationCatalog> LoadCatalogs(Assembly assembly)
    {
        var catalogs = new Dictionary<string, LocalizationCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                var locale = NormalizeLocale(root.GetProperty("locale").GetString())
                    ?? throw new InvalidDataException("Catalog locale is empty.");
                var displayName = root.GetProperty("displayName").GetString()
                    ?? throw new InvalidDataException("Catalog display name is empty.");
                var entries = root.GetProperty("translations")
                    .EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
                catalogs[locale] = LocalizationCatalog.Create(locale, displayName, entries);
            }
            catch (Exception) when (resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // Optional catalogs must never prevent the application from starting.
            }
        }

        return catalogs;
    }
}
