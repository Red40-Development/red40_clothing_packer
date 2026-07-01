using ClothingRepacker.Core;

namespace ClothingRepacker.Gui.Services;

public sealed class GitHubUpdateChecker : IUpdateChecker
{
    public async Task<VersionCheckResult?> CheckAsync(AppVersion currentVersion, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var checker = new GitHubVersionChecker(httpClient, "Red40-Development", "red40_clothing_packer");
        return await checker.CheckAsync(currentVersion, cancellationToken);
    }
}
