using WaveTandem.Core;

namespace WaveTandem.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "WaveTandem.Tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(folder, "settings.json");
    private SettingsStore Store => new(FilePath);

    [Fact]
    public void MissingFileUsesDefaults()
    {
        var result = Store.Load();
        Assert.Null(result.Error);
        Assert.True(result.CanSave);
        Assert.Equal("en", result.Preferences.Language);
    }

    [Fact]
    public void SavesAndReplacesSettings()
    {
        var initial = new Preferences
        {
            Language = "uk",
            Theme = "dark",
            SourceId = "device:a",
            SourceName = "Desk speakers",
            HasSeenWelcome = true,
            Outputs = new() { ["device:b"] = new("Headphones", new(true, 43, true, 85)) }
        };
        Store.Save(initial);
        var first = Store.Load().Preferences;
        Assert.Equal(initial.SourceName, first.SourceName);
        Assert.Equal(initial.Outputs["device:b"], first.Outputs["device:b"]);
        Store.Save(first with { Language = "ru", CloseToTray = true });
        var second = Store.Load().Preferences;
        Assert.Equal("ru", second.Language);
        Assert.True(second.CloseToTray);
        Assert.Single(Directory.EnumerateFiles(folder));
    }

    [Fact]
    public void MigratesSettingsWithoutAVersionAndNormalizesValues()
    {
        Write("""{"Language":"x","Theme":"red","Outputs":{"output":{"Name":"","Profile":{"Enabled":true,"Volume":999,"DelayMs":-3}},"bad":null}}""");
        var result = Store.Load();
        Assert.Null(result.Error);
        Assert.Equal(1, result.Preferences.Version);
        Assert.Equal("en", result.Preferences.Language);
        Assert.Equal("light", result.Preferences.Theme);
        Assert.Single(result.Preferences.Outputs);
        Assert.Equal(new DevicePreference("output", new(true, 100, false, 0)), result.Preferences.Outputs["output"]);
    }

    [Fact]
    public void SavedSetupsRoundTripAndDeduplicateNames()
    {
        var profile = new DevicePreference("Headphones", new(true, 54, false, 95));
        Store.Save(new Preferences
        {
            Theme = "system",
            FollowDefault = true,
            Setups =
            [
                new(" Movie night ", true, null, null, new() { ["listener"] = profile }),
                new("movie night", false, "old-source", null, new()),
                new(" ", false, null, null, new())
            ]
        });
        var preferences = Store.Load().Preferences;
        Assert.Equal("light", preferences.Theme);
        Assert.True(preferences.FollowDefault);
        var setup = Assert.Single(preferences.Setups);
        Assert.Equal("Movie night", setup.Name);
        Assert.True(setup.FollowDefault);
        Assert.Equal(profile, setup.Outputs["listener"]);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":2}")]
    public void UnreadableSettingsArePreserved(string settings)
    {
        Write(settings);
        var result = Store.Load();
        Assert.NotNull(result.Error);
        Assert.False(result.CanSave);
        Assert.Equal(settings, File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData(50, false, 0.5f)]
    [InlineData(50, true, 0f)]
    [InlineData(-1, false, 0f)]
    [InlineData(120, false, 1f)]
    public void GainRespectsVolumeAndMute(int volume, bool muted, float expected)
    {
        Assert.Equal(expected, new OutputProfile(Volume: volume, Muted: muted).Normalize().Gain);
    }

    private void Write(string value)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, value);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
