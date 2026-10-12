using FerretSharp.Core.Connections;
using FerretSharp.Core.Display;
using FerretSharp.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.Display;

/// <summary>WP-31: the file of the display columns – read, written for colleagues to share, never broken by a write.</summary>
public sealed class DisplayColumnSourceTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly List<DisplayColumnSource> _sources = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DefaultPath => _folder.Combine("data", "display-columns.json");

    private (DisplayColumnSource Source, AppSettingsService Settings) Source(string? path = null)
    {
        var settings = new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), AppSettings.Default with { DisplayColumnsPath = path });
        var source = new DisplayColumnSource(settings, DefaultPath, _clock, watch: false);
        _sources.Add(source);
        return (source, settings);
    }

    [Fact]
    public void Without_a_file_there_are_no_display_columns()
    {
        var (source, _) = Source("  ");

        var load = source.Current();

        Assert.True(load.IsDefaultPath);
        Assert.Equal(DefaultPath, load.Path);
        Assert.False(load.Exists);
        Assert.Null(load.Error);
        Assert.Equal(0, load.Columns.Count);
    }

    [Fact]
    public async Task Marking_writes_one_sorted_entry_per_table()
    {
        var path = _folder.Combine("repo", "ferretsharp", "display-columns.json");
        var (source, _) = Source(path);

        await source.SetAsync("SDLAND", "BEZEICHNUNG", Ct);
        await source.SetAsync("KUNDE", "NAME", Ct);

        Assert.Equal(
            """
            {
              "version": 1,
              "displayColumns": {
                "KUNDE": "NAME",
                "SDLAND": "BEZEICHNUNG"
              }
            }
            """.ReplaceLineEndings(),
            (await File.ReadAllTextAsync(path, Ct)).ReplaceLineEndings());
        Assert.Equal("NAME", source.Current().Columns.ColumnOf("KUNDE"));
        Assert.False(source.Current().IsDefaultPath);
    }

    [Fact]
    public async Task Marking_keeps_what_others_wrote_meanwhile_and_what_a_newer_version_added()
    {
        var path = _folder.Combine("display-columns.json");
        var (source, _) = Source(path);
        await source.SetAsync("KUNDE", "NAME", Ct);
        Assert.Equal(1, source.Current().Columns.Count); // read and cached

        // A colleague's change arrived by git pull, with an entry of a future version.
        await File.WriteAllTextAsync(path, """
            {
              "version": 1,
              "displayColumns": { "KUNDE": "NAME", "ARTIKEL": "BEZ" },
              "virtualForeignKeys": [ { "from": "AUFTRAG.VERTRETER_ID" } ]
            }
            """, Ct);
        await source.SetAsync("SDLAND", "BEZEICHNUNG", Ct);

        var text = await File.ReadAllTextAsync(path, Ct);
        Assert.Equal(["ARTIKEL", "KUNDE", "SDLAND"], source.Current().Columns.All.Keys);
        Assert.Contains("virtualForeignKeys", text);
        Assert.Contains("AUFTRAG.VERTRETER_ID", text);
    }

    [Fact]
    public async Task Removing_a_mark_deletes_the_entry()
    {
        var (source, _) = Source(_folder.Combine("display-columns.json"));
        await source.SetAsync("KUNDE", "NAME", Ct);
        await source.SetAsync("SDLAND", "BEZEICHNUNG", Ct);

        await source.SetAsync("KUNDE", null, Ct);

        Assert.Equal(["SDLAND"], source.Current().Columns.All.Keys);
    }

    [Fact]
    public async Task A_broken_file_says_why_and_is_never_overwritten()
    {
        var path = _folder.Combine("display-columns.json");
        await File.WriteAllTextAsync(path, "{ \"displayColumns\": { \"KUNDE\": ", Ct); // a merge gone wrong
        var (source, _) = Source(path);

        var load = source.Current();
        var refused = await Assert.ThrowsAsync<RefusedException>(() => source.SetAsync("KUNDE", "NAME", Ct));

        Assert.Contains("kein gültiges JSON", load.Error);
        Assert.Equal(0, load.Columns.Count);
        Assert.Equal(load.Error, refused.Message);
        Assert.Equal("{ \"displayColumns\": { \"KUNDE\": ", await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task A_file_of_a_newer_version_is_read_but_not_changed()
    {
        var path = _folder.Combine("display-columns.json");
        await File.WriteAllTextAsync(path, """{ "version": 2, "displayColumns": { "KUNDE": "NAME" } }""", Ct);
        var (source, _) = Source(path);

        var load = source.Current();
        var refused = await Assert.ThrowsAsync<RefusedException>(() => source.SetAsync("SDLAND", "BEZEICHNUNG", Ct));

        Assert.Equal("NAME", load.Columns.ColumnOf("KUNDE"));
        Assert.Contains("neueren FerretSharp", load.ReadOnly);
        Assert.Equal(load.ReadOnly, refused.Message);
    }

    [Fact]
    public async Task A_file_is_read_again_when_it_changed_on_disk()
    {
        var path = _folder.Combine("display-columns.json");
        await File.WriteAllTextAsync(path, """{ "version": 1, "displayColumns": { "KUNDE": "NAME" } }""", Ct);
        var (source, _) = Source(path);
        var first = source.Current();
        Assert.Same(first, source.Current()); // fresh: the file is not looked at

        await File.WriteAllTextAsync(path, """{ "version": 1, "displayColumns": { "KUNDE": "KURZNAME" } }""", Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("NAME", source.Current().Columns.ColumnOf("KUNDE"));
        _clock.Advance(DisplayColumnSource.Fresh);

        Assert.Equal("KURZNAME", source.Current().Columns.ColumnOf("KUNDE"));
    }

    [Fact]
    public async Task Changes_are_reported_once_and_only_when_something_changed()
    {
        var path = _folder.Combine("display-columns.json");
        var (source, settings) = Source(path);
        var reported = 0;
        source.Changed += () => reported++;

        await source.SetAsync("KUNDE", "NAME", Ct);
        Assert.Equal(1, reported);
        source.Reload(); // the watcher saw the own write: nothing new
        Assert.Equal(1, reported);

        await File.WriteAllTextAsync(path, """{ "version": 1, "displayColumns": { "KUNDE": "NAME", "SDLAND": "BEZEICHNUNG" } }""", Ct);
        source.Reload();
        Assert.Equal(2, reported);

        await settings.UpdateAsync(s => s with { ShowDisplayNames = false }, Ct);
        Assert.Equal(3, reported);
        Assert.False(source.Enabled);
        Assert.Equal(0, source.Shown.Count);
        Assert.Equal(2, source.Current().Columns.Count);

        await settings.UpdateAsync(s => s with { DisplayColumnsPath = null }, Ct);
        Assert.Equal(4, reported);
        Assert.True(source.Current().IsDefaultPath);
        Assert.Equal(4, source.Version);
    }

    [Fact]
    public void Lobs_raw_and_long_cannot_be_display_columns()
    {
        static Core.Schema.ColumnInfo Col(string type) => new("C", type, null, false, null, null, true, false, null, 1);

        Assert.Null(DisplayColumns.NotDisplayableReason(Col("VARCHAR2")));
        Assert.Null(DisplayColumns.NotDisplayableReason(Col("NUMBER")));
        Assert.Null(DisplayColumns.NotDisplayableReason(Col("DATE")));
        Assert.Contains("CLOB", DisplayColumns.NotDisplayableReason(Col("CLOB")));
        Assert.NotNull(DisplayColumns.NotDisplayableReason(Col("BLOB")));
        Assert.NotNull(DisplayColumns.NotDisplayableReason(Col("RAW")));
        Assert.NotNull(DisplayColumns.NotDisplayableReason(Col("LONG")));
        Assert.NotNull(DisplayColumns.NotDisplayableReason(Col("XMLTYPE")));
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _folder.Dispose();
    }
}
