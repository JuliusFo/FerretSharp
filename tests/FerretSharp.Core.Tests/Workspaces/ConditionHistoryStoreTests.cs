using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Workspaces;

/// <summary>The conditions last applied in table tabs (WP-79); the file handling is the SQL history's (<see cref="SqlHistoryStoreTests"/>).</summary>
public sealed class ConditionHistoryStoreTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private static readonly Guid Connection = Guid.NewGuid();
    private static readonly TableRef Kunden = new("APP", "KUNDEN");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConditionHistoryEntry Entry(string text, string table = "KUNDEN") => new("APP", table, text, DateTimeOffset.Now);

    [Fact]
    public async Task Per_table_newest_first_a_repeat_moves_to_the_front_and_ten_at_most()
    {
        var store = new ConditionHistoryStore(_folder.Path);
        await store.AddAsync(Connection, Entry("ID = 1", "AUFTRAG"), Ct);
        for (var i = 0; i < ConditionHistoryStore.PerTable + 2; i++)
        {
            await store.AddAsync(Connection, Entry($"ID = {i}"), Ct);
        }

        await store.AddAsync(Connection, Entry("ID = 11\r\n"), Ct); // the editor's line ends make no other condition (E2E)
        await store.AddAsync(Connection, Entry(" ID = 5 "), Ct);

        var history = await new ConditionHistoryStore(_folder.Path).LoadAsync(Connection, Ct);

        Assert.Equal([" ID = 5 ", "ID = 11\r\n", "ID = 10", "ID = 9", "ID = 8", "ID = 7", "ID = 6", "ID = 4", "ID = 3", "ID = 2"],
            ConditionHistoryStore.Of(history, Kunden));
        Assert.Equal(["ID = 1"], ConditionHistoryStore.Of(history, new TableRef("APP", "AUFTRAG"))); // other tables keep theirs
    }

    [Fact]
    public async Task Entries_set_to_null_by_hand_are_left_out()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder.Path, Connection.ToString("D") + ".json"),
            """{ "version": 1, "entries": [ null, { "owner": "APP", "table": "KUNDEN", "text": null }, { "owner": "APP", "table": "KUNDEN", "text": "ID = 1" } ] }""", Ct);

        var history = await new ConditionHistoryStore(_folder.Path).LoadAsync(Connection, Ct);

        Assert.Equal(["ID = 1"], ConditionHistoryStore.Of(history, Kunden));
    }

    public void Dispose() => _folder.Dispose();
}
