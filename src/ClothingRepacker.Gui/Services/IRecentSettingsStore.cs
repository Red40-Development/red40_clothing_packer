using ClothingRepacker.Gui.Models;

namespace ClothingRepacker.Gui.Services;

public interface IRecentSettingsStore
{
    Task<RecentSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RecentSettings settings, CancellationToken cancellationToken = default);
}
