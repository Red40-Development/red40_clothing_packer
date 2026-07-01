using System.Text.Json;
using ClothingRepacker.Gui.Models;

namespace ClothingRepacker.Gui.Services;

public sealed class ProjectStore : IProjectStore
{
    private readonly string _appSettingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ProjectStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        _appSettingsPath = Path.Combine(appData, "Red40", "ClothingRepacker", "app-state.json");
    }

    public async Task<ProjectLoadResult> LoadLastProjectAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_appSettingsPath))
        {
            return new ProjectLoadResult(new ProjectSettings(), string.Empty);
        }

        await using var stream = File.OpenRead(_appSettingsPath);
        var appSettings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions, cancellationToken)
            ?? new AppSettings(string.Empty);
        if (!string.IsNullOrWhiteSpace(appSettings.LastProjectPath) && File.Exists(appSettings.LastProjectPath))
        {
            return new ProjectLoadResult(await LoadProjectAsync(appSettings.LastProjectPath, cancellationToken), appSettings.LastProjectPath);
        }

        return new ProjectLoadResult(new ProjectSettings(), string.Empty);
    }

    public async Task<ProjectSettings> LoadProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(projectPath);
        return await JsonSerializer.DeserializeAsync<ProjectSettings>(stream, _jsonOptions, cancellationToken)
            ?? new ProjectSettings();
    }

    public async Task SaveProjectAsync(string projectPath, ProjectSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        await using var stream = File.Create(projectPath);
        await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken);
        await SaveLastProjectPathAsync(projectPath, cancellationToken);
    }

    public async Task SaveLastProjectPathAsync(string? projectPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_appSettingsPath)!);
        await using var stream = File.Create(_appSettingsPath);
        await JsonSerializer.SerializeAsync(stream, new AppSettings(projectPath ?? string.Empty), _jsonOptions, cancellationToken);
    }

    private sealed record AppSettings(string LastProjectPath);
}
