using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ferret-tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;

    public SettingsStoreTests() => _store = new SettingsStore(Path.Combine(_directory, "settings.json"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_file_means_defaults()
    {
        var settings = await _store.LoadAsync(Ct);

        Assert.Equal(ThemeMode.System, settings.Theme);
    }

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.System)]
    public async Task Theme_round_trips(ThemeMode theme)
    {
        await _store.SaveAsync(new AppSettings { Theme = theme }, Ct);

        Assert.Equal(theme, (await _store.LoadAsync(Ct)).Theme);
        Assert.False(File.Exists(_store.FilePath + ".tmp"));
    }

    [Fact]
    public async Task File_is_versioned_and_readable()
    {
        await _store.SaveAsync(new AppSettings { Theme = ThemeMode.Dark }, Ct);

        var json = await File.ReadAllTextAsync(_store.FilePath, Ct);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"theme\": \"dark\"", json);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 99, "settings": { "theme": "dark" } }""")]
    [InlineData("""{ "version": 1, "settings": { "theme": "purple" } }""")]
    public async Task Broken_or_newer_files_are_reported(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(_store.FilePath, content, Ct);

        await Assert.ThrowsAsync<SettingsStoreException>(() => _store.LoadAsync(Ct));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
