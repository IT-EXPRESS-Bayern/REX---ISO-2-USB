// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Settings;

namespace Bootrix.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var store = new SettingsStore(FilePath);

        Assert.Equal(new AppSettings(), store.Current);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void UpdateIsPersistedAndReadBackByANewStore()
    {
        new SettingsStore(FilePath).Update(s => s with { Language = "en-US", Theme = AppTheme.Dark, ServiceMode = true });

        var reloaded = new SettingsStore(FilePath).Current;

        Assert.Equal("en-US", reloaded.Language);
        Assert.Equal(AppTheme.Dark, reloaded.Theme);
        Assert.True(reloaded.ServiceMode);
        Assert.True(reloaded.VerifyAfterWrite);
    }

    [Fact]
    public void FieldsMissingFromTheFileFallBackToTheirDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{ "theme": "Light" }""");

        var settings = new SettingsStore(FilePath).Current;

        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.True(settings.VerifyAfterWrite);
        Assert.True(settings.PlaySoundWhenDone);
    }

    [Fact]
    public void DamagedFileIsMovedAsideAndDefaultsAreUsed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json");

        var store = new SettingsStore(FilePath);

        Assert.Equal(new AppSettings(), store.Current);
        Assert.True(File.Exists(FilePath + ".bad"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void ChangedIsRaisedWithTheNewValues()
    {
        var store = new SettingsStore(FilePath);
        AppSettings? seen = null;
        store.Changed += (_, settings) => seen = settings;

        store.Update(s => s with { ShowUsbHardDisks = true });

        Assert.NotNull(seen);
        Assert.True(seen.ShowUsbHardDisks);
    }

    [Fact]
    public void NoTemporaryFileRemainsAfterSaving()
    {
        new SettingsStore(FilePath).Update(s => s with { Language = "de-DE" });

        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
