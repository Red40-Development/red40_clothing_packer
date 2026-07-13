using ClothingRepacker.Core.Localization;

namespace ClothingRepacker.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void EnglishCatalogIsEmbeddedAndExposesLanguageOptions()
    {
        var localization = new LocalizationService();

        Assert.Contains(localization.Catalogs, catalog => catalog.Locale == "en");
        Assert.Contains(localization.GetOptions(), option => option.IsSystemDefault);
        Assert.Contains(localization.GetOptions(), option => option.Locale == "en");
        Assert.Equal("Wrote 3 file(s) to /tmp/output.", localization.Translate(
            "cli.wroteFiles",
            new Dictionary<string, object?> { ["count"] = 3, ["path"] = "/tmp/output" }));
    }

    [Fact]
    public void MissingTranslationFallsBackToExplicitTextThenKey()
    {
        var localization = new LocalizationService();

        Assert.Equal("fallback", localization.Translate("missing.key", fallback: "fallback"));
        Assert.Equal("another.missing.key", localization.Translate("another.missing.key"));
    }

    [Fact]
    public void ChangingOverrideRaisesLanguageNotification()
    {
        var localization = new LocalizationService();
        var changed = 0;
        localization.LanguageChanged += (_, _) => changed++;

        localization.OverrideLocale = "en";

        Assert.Equal(1, changed);
        Assert.Equal("en", localization.ActiveLocale);
    }
}
