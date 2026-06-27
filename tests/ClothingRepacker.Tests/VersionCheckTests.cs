public class VersionCheckTests
{
    [Theory]
    [InlineData("v1.2.3", "v1.2.3")]
    [InlineData("1.2", "1.2")]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("v1.2.3-beta.1", "v1.2.3-beta.1")]
    public void AppVersionParsesReleaseTags(string input, string expectedDisplay)
    {
        Assert.True(AppVersion.TryParse(input, out var version));

        Assert.Equal(expectedDisplay, version.Display);
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    public void VersionCheckResultDetectsNewerRelease(string latest, string current, bool expected)
    {
        var result = new VersionCheckResult(
            AppVersion.FromInformationalVersion(current),
            AppVersion.FromInformationalVersion(latest),
            "https://example.test/release");

        Assert.Equal(expected, result.IsUpdateAvailable);
    }
}
