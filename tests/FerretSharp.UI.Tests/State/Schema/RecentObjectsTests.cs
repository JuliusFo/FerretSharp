using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace FerretSharp.UI.Tests.State;

/// <summary>The objects last opened (WP-91): which active tabs count, newest first, per connection, kept in the file.</summary>
public sealed class RecentObjectsTests : IDisposable
{
    private static readonly Guid Connection = Guid.NewGuid();
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly TableSummary Auftrag = new("APP_USER", "AUFTRAG", TableKind.Table);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private RecentObjects Service() => new(new RecentObjectStore(_folder.Path), NullLogger<RecentObjects>.Instance);

    private static IEnumerable<string> Names(IReadOnlyList<RecentObject> list) => list.Select(r => r.Name);

    [Fact]
    public async Task Table_and_PL_SQL_tabs_count_newest_first_each_once_and_are_kept()
    {
        var recent = Service();
        var kunden = new TableTab(Workspace, TestApp.Kunden);
        recent.Track(Connection, kunden);
        recent.Track(Connection, new PlSqlTab(Workspace, TestApp.Rechnung));
        recent.Track(Connection, new TableTab(Workspace, Auftrag));
        recent.Track(Connection, kunden); // back to it: to the front again

        Assert.Equal(["KUNDEN", "AUFTRAG", "PKG_RECHNUNG"], Names(recent.For(Connection))); // shown at once
        await recent.IdleAsync();
        var reread = Service();
        reread.For(Connection);
        await reread.IdleAsync();
        Assert.Equal(["KUNDEN", "AUFTRAG", "PKG_RECHNUNG"], Names(reread.For(Connection)));
        Assert.Equal(SchemaObjectKind.Package, reread.For(Connection)[2].Kind);
        Assert.Empty(reread.For(Guid.NewGuid())); // per connection
    }

    [Fact]
    public async Task SQL_and_time_travel_tabs_and_no_connection_do_not_count()
    {
        var recent = Service();
        recent.Track(Connection, new SqlTab(Workspace, "SQL 1"));
        recent.Track(Connection, new TableTab(Workspace, Auftrag) { TimeTravel = new TimeTravelState(42, DateTimeOffset.Now, "DB") });
        recent.Track(null, new TableTab(Workspace, Auftrag));
        recent.Track(Connection, null);

        await recent.IdleAsync();
        Assert.Empty(recent.For(Connection));
        await recent.IdleAsync();
        Assert.Empty(recent.For(Connection));
    }

    /// <summary>
    /// Review WP-91, 2: a file of a newer FerretSharp (or one locked for the moment) takes no visits – they stay in memory,
    /// all of them, not only the last one.
    /// </summary>
    [Fact]
    public async Task Visits_a_newer_file_does_not_take_stay_in_memory()
    {
        const string newer = """{ "version": 2, "entries": [], "somethingNew": true }""";
        await File.WriteAllTextAsync(Path.Combine(_folder.Path, Connection.ToString("D") + ".json"), newer, Ct);
        var recent = Service();

        recent.Track(Connection, new TableTab(Workspace, Auftrag));
        recent.Track(Connection, new TableTab(Workspace, TestApp.Kunden));
        await recent.IdleAsync();

        Assert.Equal(["KUNDEN", "AUFTRAG"], Names(recent.For(Connection)));
        Assert.Equal(newer, await File.ReadAllTextAsync(Path.Combine(_folder.Path, Connection.ToString("D") + ".json"), Ct));
    }

    /// <summary>
    /// Second review of WP-91, A: a file that could not be read at first (locked, of a newer FerretSharp) and can be later –
    /// the next write brings what it holds behind the visits of this session.
    /// </summary>
    [Fact]
    public async Task What_a_file_holds_comes_back_with_the_next_write_after_a_first_read_failed()
    {
        var file = Path.Combine(_folder.Path, Connection.ToString("D") + ".json");
        await File.WriteAllTextAsync(file, """{ "version": 2, "entries": [] }""", Ct);
        var recent = Service();
        recent.Track(Connection, new TableTab(Workspace, Auftrag));
        await recent.IdleAsync();

        await File.WriteAllTextAsync(file, """
            { "version": 1, "entries": [ { "kind": "package", "owner": "APP_USER", "name": "PKG_RECHNUNG", "openedAt": "2026-11-19T10:00:00+01:00" } ] }
            """, Ct);
        recent.Track(Connection, new TableTab(Workspace, TestApp.Kunden));
        await recent.IdleAsync();

        Assert.Equal(["KUNDEN", "AUFTRAG", "PKG_RECHNUNG"], Names(recent.For(Connection)));
    }

    /// <summary>The file is read on first use; a tab shown before the read ends stays in front of what the file held.</summary>
    [Fact]
    public async Task A_visit_before_the_file_was_read_stays_in_front()
    {
        var earlier = Service();
        earlier.Track(Connection, new TableTab(Workspace, Auftrag));
        await earlier.IdleAsync();

        var recent = Service();
        recent.Track(Connection, new TableTab(Workspace, TestApp.Kunden)); // starts the read, queues the write
        Assert.Equal(["KUNDEN"], Names(recent.For(Connection)));
        await recent.IdleAsync();

        Assert.Equal(["KUNDEN", "AUFTRAG"], Names(recent.For(Connection)));
    }
}
