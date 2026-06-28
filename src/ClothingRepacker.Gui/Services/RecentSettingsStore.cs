using System.Text.Json;
using ClothingRepacker.Gui.Models;

namespace ClothingRepacker.Gui.Services;

public sealed class RecentSettingsStore : IRecentSettingsStore
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RecentSettingsStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        _settingsPath = Path.Combine(appData, "Red40", "ClothingRepacker", "settings.json");
    }

    public async Task<RecentSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new RecentSettings();
        }

        await using var stream = File.OpenRead(_settingsPath);
        return await JsonSerializer.DeserializeAsync<RecentSettings>(stream, _jsonOptions, cancellationToken)
            ?? new RecentSettings();
    }

    public async Task SaveAsync(RecentSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken);
    }
}
