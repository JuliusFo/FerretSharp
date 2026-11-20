using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Workspaces;

/// <summary>The objects last opened (WP-91); the file handling is the SQL history's (<see cref="SqlHistoryStoreTests"/>).</summary>
public sealed class RecentObjectStoreTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private static readonly Guid Connection = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RecentObject Table(string name) => RecentObject.Of(new TableSummary("APP", name, TableKind.Table), DateTimeOffset.Now);

    [Fact]
    public async Task Newest_first_each_once_and_thirty_at_most()
    {
        var store = new RecentObjectStore(_folder.Path);
        for (var i = 0; i < RecentObjectStore.MaxEntries + 2; i++)
        {
            await store.AddAsync(Connection, Table($"T{i}"), Ct);
        }

        await store.AddAsync(Connection, Table("T5"), Ct); // again: to the front, not twice
        await store.AddAsync(Connection, RecentObject.Of(new PlSqlObjectSummary("APP", "T5", PlSqlKind.Trigger, "VALID"), DateTimeOffset.Now), Ct);

        var recent = await new RecentObjectStore(_folder.Path).LoadAsync(Connection, Ct);

        Assert.Equal(RecentObjectStore.MaxEntries, recent.Count);
        Assert.Equal((SchemaObjectKind.Trigger, "T5"), (recent[0].Kind, recent[0].Name)); // a trigger is another object
        Assert.Equal((SchemaObjectKind.Table, "T5"), (recent[1].Kind, recent[1].Name));
        Assert.Equal("T31", recent[2].Name);
        Assert.Single(recent, r => r.Name == "T5" && r.Kind == SchemaObjectKind.Table);
        Assert.Empty(await store.LoadAsync(Guid.NewGuid(), Ct)); // per connection
    }

    [Fact]
    public async Task Entries_edited_by_hand_are_cleaned_up()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder.Path, Connection.ToString("D") + ".json"), """
            { "version": 1, "entries": [
              null,
              { "kind": "table", "owner": "APP", "name": null },
              { "kind": 42, "owner": "APP", "name": "SELTSAM" },
              { "kind": "table", "owner": "APP", "name": "KUNDEN", "openedAt": "2026-11-20T10:00:00+01:00" },
              { "kind": "table", "owner": "APP", "name": "KUNDEN", "openedAt": "2026-11-19T10:00:00+01:00" }
            ] }
            """, Ct);

        var recent = await new RecentObjectStore(_folder.Path).LoadAsync(Connection, Ct);

        var entry = Assert.Single(recent);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(1)), entry.OpenedAt);
    }

    public void Dispose() => _folder.Dispose();
}
