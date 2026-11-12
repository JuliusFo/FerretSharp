using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly SettingsStore _store;

    public SettingsStoreTests() => _store = new SettingsStore(_folder.Combine("settings.json"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The defaults are decisions of the user, each held here (review of R8: C13 merged the tests that held them, and a
    /// changed default no longer failed anything).
    /// </summary>
    [Fact]
    public async Task A_missing_file_gives_the_decided_defaults()
    {
        var settings = await _store.LoadAsync(Ct);

        Assert.Equal(AppSettings.Default, settings);
        Assert.Equal(
            (ThemeMode.System, UiLanguage.English, KeepAlive: true, DiagnoseUiStalls: false, FkCountMode.Immediately, FilterSqlColumns.Listed, DdlWithoutOwner: false),
            (settings.Theme, settings.Language, settings.KeepAlive, settings.DiagnoseUiStalls, settings.FkCounts, settings.FilterSql, settings.DdlWithoutOwner));
        Assert.Equal(
            (ShowColumnTypes: true, ShowNotNull: true, SyncAuditInDesigner: true, CreateAuditTables: true, CheckForUpdates: true, ChangesPanelOpen: false, ChangesPanelWidth: (int?)null),
            (settings.ShowColumnTypes, settings.ShowNotNull, settings.SyncAuditInDesigner, settings.CreateAuditTables, settings.CheckForUpdates, settings.ChangesPanelOpen, settings.ChangesPanelWidth));
        Assert.Null(settings.ExplorerWidth);
        Assert.Null(settings.PathsPanelWidth);
        Assert.Null(settings.QueryPanelWidth);
        Assert.Equal((TabMode.Data, RowLoadMode.Immediately), (settings.OpenView, settings.LoadRows));
        Assert.Equal(TabBarLayout.SingleRow, settings.TabBar);
    }

    /// <summary>
    /// A file written before a setting existed gives that setting its default – every setting at once (review 3.54.0, C13: one
    /// test per setting before, ten of them, each new setting brought another).
    /// </summary>
    [Fact]
    public async Task An_old_file_gives_every_setting_it_does_not_name_its_default()
    {
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "theme": "dark" } }""", Ct);

        Assert.Equal(AppSettings.Default with { Theme = ThemeMode.Dark }, await _store.LoadAsync(Ct));
    }

    /// <summary>
    /// Every setting comes back as saved: each one set away from its default (review 3.54.0, C13) – a new setting the file does
    /// not keep fails here without a test of its own. Maps have their own tests below; a property of another type must get a
    /// value here first.
    /// </summary>
    [Fact]
    public async Task Every_setting_round_trips()
    {
        var changed = AppSettings.Default;
        foreach (var property in Properties.Where(p => p.PropertyType != typeof(SettingsMap)))
        {
            var value = Changed(property.PropertyType, property.GetValue(AppSettings.Default));
            changed = With(changed, property, value);
        }

        await _store.SaveAsync(changed, Ct);
        var loaded = await _store.LoadAsync(Ct);

        Assert.All(Properties, p => Assert.Equal(p.GetValue(changed), p.GetValue(loaded)));
        Assert.Contains("\"language\": \"german\"", await File.ReadAllTextAsync(_store.FilePath, Ct), StringComparison.Ordinal); // enums by name
    }

    private static readonly System.Reflection.PropertyInfo[] Properties =
        typeof(AppSettings).GetProperties().Where(p => p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();

    /// <summary>A value other than <paramref name="current"/>, valid for the setting.</summary>
    private static object? Changed(Type type, object? current) => type switch
    {
        _ when type == typeof(bool) => !(bool)current!,
        _ when type == typeof(int) => (int)current! + 1,
        _ when type == typeof(int?) => (int?)current is { } n ? n + 1 : 520,
        _ when type == typeof(string) => current is null ? "C:\\team\\file.json" : null,
        _ when type.IsEnum => Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current)),
        _ when type == typeof(TableListMap) => TableListMap.Empty.With(Guid.Empty.ToString(), [new("APP", "BENUTZER")]),
        _ => throw new InvalidOperationException($"No changed value for a setting of type {type.Name}: add one here."),
    };

    private static AppSettings With(AppSettings settings, System.Reflection.PropertyInfo property, object? value)
    {
        var copy = settings with { };
        property.SetValue(copy, value);
        return copy;
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
    [InlineData("""{ "version": 1, "settings": [ "theme" ] }""")]
    public async Task Broken_or_newer_files_are_reported(string content)
    {
        await File.WriteAllTextAsync(_store.FilePath, content, Ct);

        await Assert.ThrowsAsync<SettingsStoreException>(() => _store.LoadAsync(Ct));
    }

    /// <summary>
    /// fix/robustness (code review of small-tab-open-options): an enum value this version does not know – written by a newer
    /// one or by hand – dropped the whole file. Now only that setting has its default.
    /// </summary>
    [Fact]
    public async Task A_setting_this_version_cannot_read_has_its_default_and_the_others_stay()
    {
        await File.WriteAllTextAsync(_store.FilePath,
            """{ "version": 1, "settings": { "theme": "purple", "language": "german", "fkCounts": 3, "keepAlive": false } }""", Ct);

        var read = await _store.ReadAsync(Ct);

        Assert.Equal(AppSettings.Default with { Language = UiLanguage.German, KeepAlive = false }, read.Settings);
        Assert.Equal(["theme", "fkCounts"], read.Skipped);
    }

    /// <summary>
    /// Code review: an entry of a map that is no text failed with the path <c>$.openTable</c> (the map's converter reads on its
    /// own), naming no setting – the whole file was lost. Now only that entry goes.
    /// </summary>
    [Fact]
    public async Task An_entry_of_a_map_that_is_no_text_goes_alone()
    {
        await File.WriteAllTextAsync(_store.FilePath,
            """{ "version": 1, "settings": { "shortcuts": { "openTable": 5, "openSql": "Ctrl+K", "x": null }, "language": "german" } }""", Ct);

        var read = await _store.ReadAsync(Ct);

        Assert.Equal(("Ctrl+K", 1, UiLanguage.German), (read.Settings.Shortcuts["openSql"], read.Settings.Shortcuts.Count, read.Settings.Language));
        Assert.Equal(["shortcuts.openTable", "shortcuts.x"], read.Skipped);
    }

    /// <summary>
    /// Code review: a duplicate key alone is read (the last one wins), but taking the file apart to leave out an unreadable
    /// value threw ArgumentException at the first use of the object – past every handler, FerretSharp did not start.
    /// </summary>
    [Fact]
    public async Task A_file_that_cannot_be_taken_apart_stays_broken()
    {
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "theme": "purple", "theme": "dark", "language": "german" } }""", Ct);

        await Assert.ThrowsAsync<SettingsStoreException>(() => _store.ReadAsync(Ct));
        var service = await AppSettingsService.OpenAsync(_store, Start, Ct);
        Assert.Equal(AppSettings.Default, service.Current);
    }

    [Theory]
    [InlineData("$.settings.theme", "theme")]
    [InlineData("$.settings.shortcuts.openTable", "shortcuts")] // a value inside a map: the whole setting
    [InlineData("$.settings.pathAvoidedTables[0]", "pathAvoidedTables")]
    [InlineData("$.settings['odd.name'].x", "odd.name")]
    [InlineData("$.settings", null)]
    [InlineData("$.version", null)]
    [InlineData(null, null)]
    public void An_error_path_names_the_setting_it_lies_in(string? path, string? setting)
    {
        Assert.Equal(setting, SettingsStore.SettingOf(path));
    }

    /// <summary>The unreadable value is kept in a copy, since the next save writes the default (fix/robustness).</summary>
    [Fact]
    public async Task Settings_left_out_are_told_at_start_and_the_file_is_kept_as_a_copy()
    {
        const string content = """{ "version": 1, "settings": { "theme": "purple", "language": "german" } }""";
        await File.WriteAllTextAsync(_store.FilePath, content, Ct);

        var service = await AppSettingsService.OpenAsync(_store, Start, Ct);

        var copy = _store.FilePath + ".bad-20261011-153000";
        Assert.Equal(content, await File.ReadAllTextAsync(copy, Ct));
        Assert.Equal(UiLanguage.German, service.Current.Language);
        Assert.Equal(
            $"Einige Einstellungen in {_store.FilePath} ließen sich nicht lesen und haben ihren Standard: theme. Die übrigen wurden gelesen; die Datei bleibt als {copy} erhalten.",
            service.StartupNotice);
        Assert.Null(service.NotSaving);
    }

    /// <summary>R5b (review 3.31.0, B2): a newer file with a value this version does not know said "no valid settings file".</summary>
    [Fact]
    public async Task A_newer_file_with_an_unknown_value_is_newer_not_broken()
    {
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 2, "settings": { "theme": "sepia" } }""", Ct);

        var error = await Assert.ThrowsAsync<SettingsStoreException>(() => _store.LoadAsync(Ct));

        Assert.Equal($"{_store.FilePath} stammt aus einer neueren FerretSharp-Version (Format 2).", error.Message);
    }

    /// <summary>Review 3.31.0, A6: "shortcuts": null (edited by hand) made the start fail building the shortcut map.</summary>
    [Fact]
    public async Task Shortcuts_set_to_null_by_hand_are_the_defaults()
    {
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "shortcuts": null } }""", Ct);

        Assert.Same(SettingsMap.Empty, (await _store.LoadAsync(Ct)).Shortcuts);
    }

    /// <summary>WP-25: the changed shortcuts as a plain object with the action names as given (R6b: one map type for both).</summary>
    [Fact]
    public async Task Shortcuts_round_trip_as_a_plain_object_with_action_names_as_keys()
    {
        var settings = AppSettings.Default with { Shortcuts = new ShortcutMap().With(ShortcutAction.Flush, KeyChord.Parse("ctrl+shift+s")) };

        await _store.SaveAsync(settings, Ct);

        Assert.Contains("\"Flush\": \"ctrl+shift+s\"", await File.ReadAllTextAsync(_store.FilePath, Ct), StringComparison.Ordinal);
        Assert.Equal(settings, await _store.LoadAsync(Ct));
    }

    /// <summary>WP-51: the audit templates of groups as a plain object, compared by content; null by hand is none.</summary>
    [Fact]
    public async Task Audit_templates_of_groups_round_trip_as_a_plain_object()
    {
        var settings = AppSettings.Default with { AuditTemplateGroups = SettingsMap.From([new("ERP", @"C:\repo\erp.sql"), new("Kasse", @"C:\repo\kasse.sql")]) };

        await _store.SaveAsync(settings, Ct);

        Assert.Contains("""
                        "auditTemplateGroups": {
                        """, await File.ReadAllTextAsync(_store.FilePath, Ct));
        Assert.Equal(settings, await _store.LoadAsync(Ct));

        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "auditTemplateGroups": null } }""", Ct);
        Assert.Same(SettingsMap.Empty, (await _store.LoadAsync(Ct)).AuditTemplateGroups);
    }

    /// <summary>WP-74: the tables ways avoid per connection, owner and name apart (a quoted name may hold a dot); null by hand is none.</summary>
    [Fact]
    public async Task Avoided_tables_round_trip_by_owner_and_name()
    {
        var id = Guid.NewGuid().ToString();
        var settings = AppSettings.Default with { PathAvoidedTables = TableListMap.Empty.With(id, [new("APP", "BENUTZER"), new("APP", "A.B"), new("APP", "BENUTZER")]) };

        await _store.SaveAsync(settings, Ct);

        var text = await File.ReadAllTextAsync(_store.FilePath, Ct);
        Assert.Contains("\"name\": \"A.B\"", text, StringComparison.Ordinal);
        var loaded = await _store.LoadAsync(Ct);
        Assert.Equal(settings, loaded);
        Assert.Equal(2, loaded.PathAvoidedTables.Of(id).Count);

        await File.WriteAllTextAsync(_store.FilePath, $$"""{ "version": 1, "settings": { "pathAvoidedTables": { "{{id}}": null, "x": [ null ] } } }""", Ct);
        Assert.Equal(0, (await _store.LoadAsync(Ct)).PathAvoidedTables.Count);
        await File.WriteAllTextAsync(_store.FilePath, """{ "version": 1, "settings": { "pathAvoidedTables": null } }""", Ct);
        Assert.Same(TableListMap.Empty, (await _store.LoadAsync(Ct)).PathAvoidedTables);
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
    public async Task Starting_again_with_the_same_broken_file_keeps_no_second_copy()
    {
        await File.WriteAllTextAsync(_store.FilePath, "{ kaputt", Ct);

        var first = await AppSettingsService.OpenAsync(_store, Start, Ct);
        var second = await AppSettingsService.OpenAsync(_store, Start.AddMinutes(5), Ct);

        Assert.Equal(first.StartupNotice, second.StartupNotice); // names the same copy
        Assert.Single(Directory.GetFiles(_folder.Path, "settings.json.bad-*"));
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

    /// <summary>
    /// Code review: without a copy, a file read in part is not saved over either; and the notice is in the language of the
    /// moment it is shown – the language is one of the settings read, and the app sets it only after reading them.
    /// </summary>
    [Fact]
    public async Task Settings_left_out_without_a_copy_save_nothing_and_are_told_in_the_language_set_later()
    {
        const string content = """{ "version": 1, "settings": { "theme": "purple", "language": "english" } }""";
        await File.WriteAllTextAsync(_store.FilePath, content, Ct);
        await File.WriteAllTextAsync(_store.FilePath + ".bad-20261011-153000", "", Ct); // the copy cannot be made

        var service = await AppSettingsService.OpenAsync(_store, Start, Ct);
        await Assert.ThrowsAsync<IOException>(() => service.UpdateAsync(s => s with { KeepAlive = false }, Ct));

        Assert.Equal(content, await File.ReadAllTextAsync(_store.FilePath, Ct));
        using (UiCulture.Use("en"))
        {
            Assert.Equal(
                $"Some settings in {_store.FilePath} could not be read and have their defaults: theme. The other settings were read; FerretSharp saves no settings in this session, so the file stays as it is.",
                service.StartupNotice);
            Assert.Equal("The settings file could not be read at start.", service.NotSaving);
        }
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
