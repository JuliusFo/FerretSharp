using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly SettingsStore _store;

    public SettingsStoreTests() => _store = new SettingsStore(_folder.Combine("settings.json"));

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
    public async Task Language_is_English_by_default_also_in_files_written_before_it_existed_and_round_trips()
    {
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "theme": "dark" } }""", Ct);
        Assert.Equal(UiLanguage.English, (await _store.LoadAsync(Ct)).Language);

        await _store.SaveAsync(new AppSettings { Language = UiLanguage.German }, Ct);

        Assert.Equal(UiLanguage.German, (await _store.LoadAsync(Ct)).Language);
        Assert.Contains("\"language\": \"german\"", await File.ReadAllTextAsync(_store.FilePath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keep_alive_is_on_by_default_also_in_files_written_before_it_existed()
    {
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "theme": "dark" } }""", Ct);

        var settings = await _store.LoadAsync(Ct);

        Assert.Equal(ThemeMode.Dark, settings.Theme);
        Assert.True(settings.KeepAlive);
    }

    [Fact]
    public async Task Ui_stall_diagnostics_are_off_by_default_and_round_trip()
    {
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "theme": "dark" } }""", Ct);
        Assert.False((await _store.LoadAsync(Ct)).DiagnoseUiStalls);

        await _store.SaveAsync(new AppSettings { DiagnoseUiStalls = true }, Ct);

        Assert.True((await _store.LoadAsync(Ct)).DiagnoseUiStalls);
    }

    [Fact]
    public async Task Change_overview_is_closed_with_default_width_by_default_and_round_trips()
    {
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "theme": "dark" } }""", Ct);
        var loaded = await _store.LoadAsync(Ct);
        Assert.False(loaded.ChangesPanelOpen);
        Assert.Null(loaded.ChangesPanelWidth);

        await _store.SaveAsync(new AppSettings { ChangesPanelOpen = true, ChangesPanelWidth = 520 }, Ct);

        loaded = await _store.LoadAsync(Ct);
        Assert.True(loaded.ChangesPanelOpen);
        Assert.Equal(520, loaded.ChangesPanelWidth);
    }

    [Fact]
    public async Task Service_keeps_changes_of_different_settings_and_saves_all_of_them()
    {
        var service = new AppSettingsService(_store, AppSettings.Default);

        await service.UpdateAsync(s => s with { Theme = ThemeMode.Light }, Ct);
        await service.UpdateAsync(s => s with { KeepAlive = false }, Ct);

        Assert.Equal(new AppSettings { Theme = ThemeMode.Light, KeepAlive = false }, service.Current);
        Assert.Equal(service.Current, await _store.LoadAsync(Ct));
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
        await File.WriteAllTextAsync(_store.FilePath, content, Ct);

        await Assert.ThrowsAsync<SettingsStoreException>(() => _store.LoadAsync(Ct));
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 11, 15, 30, 0, TimeSpan.FromHours(2));

    /// <summary>
    /// R4a: the app started with defaults and the next save – moving a panel was enough – replaced the file, shortcuts and
    /// all. Now a copy is kept first and the user is told.
    /// </summary>
    [Fact]
    public async Task A_file_that_cannot_be_read_is_kept_as_a_copy_before_the_first_save()
    {
        const string newer = """{ "version": 99, "settings": { "theme": "dark" } }""";
        await File.WriteAllTextAsync(_store.FilePath, newer, Ct);

        var service = await AppSettingsService.OpenAsync(_store, Start, Ct);
        await service.UpdateAsync(s => s with { ChangesPanelOpen = true }, Ct);

        var copy = _store.FilePath + ".bad-20261011-153000";
        Assert.Equal(newer, await File.ReadAllTextAsync(copy, Ct));
        Assert.Equal(AppSettings.Default with { ChangesPanelOpen = true }, await _store.LoadAsync(Ct));
        Assert.Equal(
            $"{_store.FilePath} stammt aus einer neueren FerretSharp-Version (Format 99). FerretSharp startet mit den Standardeinstellungen; die Datei bleibt als {copy} erhalten.",
            service.StartupNotice);
        Assert.Null(service.NotSaving);
    }

    [Fact]
    public async Task Without_a_copy_nothing_is_saved_in_the_session()
    {
        await File.WriteAllTextAsync(_store.FilePath, "{ not json", Ct);
        await File.WriteAllTextAsync(_store.FilePath + ".bad-20261011-153000", "", Ct); // the copy cannot be made

        var service = await AppSettingsService.OpenAsync(_store, Start, Ct);
        var refused = await Assert.ThrowsAsync<IOException>(() => service.UpdateAsync(s => s with { Theme = ThemeMode.Dark }, Ct));

        Assert.Equal(ThemeMode.Dark, service.Current.Theme); // in effect for the session
        Assert.Equal("{ not json", await File.ReadAllTextAsync(_store.FilePath, Ct));
        Assert.Equal("Die Einstellungsdatei ließ sich beim Start nicht lesen.", refused.Message);
        Assert.EndsWith("FerretSharp startet mit den Standardeinstellungen und speichert in dieser Sitzung keine Einstellungen, damit die Datei bleibt, wie sie ist.",
            service.StartupNotice);
    }

    [Fact]
    public async Task A_readable_file_has_nothing_to_tell()
    {
        await _store.SaveAsync(new AppSettings { Theme = ThemeMode.Light }, Ct);

        var service = await AppSettingsService.OpenAsync(_store, Start, Ct);

        Assert.Equal(ThemeMode.Light, service.Current.Theme);
        Assert.Null(service.StartupNotice);
        Assert.Null(service.NotSaving);
        Assert.Single(Directory.GetFiles(_folder.Path));
    }

    public void Dispose()
    {
        _folder.Dispose();
    }
}
