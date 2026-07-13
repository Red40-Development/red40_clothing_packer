using ClothingRepacker.Gui.Models;

namespace ClothingRepacker.Gui.Services;

public interface IProjectStore
{
    Task<ProjectLoadResult> LoadLastProjectAsync(CancellationToken cancellationToken = default);
    Task<ProjectSettings> LoadProjectAsync(string projectPath, CancellationToken cancellationToken = default);
    Task SaveProjectAsync(string projectPath, ProjectSettings settings, CancellationToken cancellationToken = default);
    Task SaveLastProjectPathAsync(string? projectPath, CancellationToken cancellationToken = default);
    Task<string?> LoadLanguageOverrideAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
    Task SaveLanguageOverrideAsync(string? languageOverride, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed record ProjectLoadResult(ProjectSettings Settings, string ProjectPath);
