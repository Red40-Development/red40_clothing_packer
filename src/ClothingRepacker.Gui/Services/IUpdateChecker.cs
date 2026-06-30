using ClothingRepacker.Core;

namespace ClothingRepacker.Gui.Services;

public interface IUpdateChecker
{
    Task<VersionCheckResult?> CheckAsync(AppVersion currentVersion, CancellationToken cancellationToken);
}
