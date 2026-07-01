using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClothingRepacker.Core;

public sealed class GitHubVersionChecker
{
    private readonly HttpClient _httpClient;
    private readonly string _owner;
    private readonly string _repository;

    public GitHubVersionChecker(HttpClient httpClient, string owner, string repository)
    {
        _httpClient = httpClient;
        _owner = owner;
        _repository = repository;
    }

    public async Task<VersionCheckResult?> CheckAsync(AppVersion currentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{_owner}/{_repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Red40-Clothing-Repacker", currentVersion.Display));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagNameProperty) ||
            tagNameProperty.GetString() is not { Length: > 0 } tagName ||
            !AppVersion.TryParse(tagName, out var latestVersion))
        {
            return null;
        }

        var releaseUrl = root.TryGetProperty("html_url", out var htmlUrlProperty)
            ? htmlUrlProperty.GetString() ?? $"https://github.com/{_owner}/{_repository}/releases/latest"
            : $"https://github.com/{_owner}/{_repository}/releases/latest";

        return new VersionCheckResult(currentVersion, latestVersion, releaseUrl);
    }
}

public sealed record VersionCheckResult(AppVersion CurrentVersion, AppVersion LatestVersion, string ReleaseUrl)
{
    public bool IsUpdateAvailable => LatestVersion.CompareTo(CurrentVersion) > 0;
}

public readonly partial record struct AppVersion(int Major, int Minor, int Patch, string Display) : IComparable<AppVersion>
{
    public static AppVersion FromInformationalVersion(string? value)
        => TryParse(value, out var version) ? version : new AppVersion(0, 0, 0, "0.0.0");

    public static bool TryParse(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var display = value.Split('+', 2)[0].Trim();
        var match = VersionPattern().Match(display);
        if (!match.Success)
        {
            return false;
        }

        version = new AppVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0,
            display);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        return Patch.CompareTo(other.Patch);
    }

    [GeneratedRegex(@"^v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:[-.][0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();
}
