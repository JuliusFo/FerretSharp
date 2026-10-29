using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using NSubstitute;

namespace FerretSharp.Core.Tests.Workspaces;

public sealed class SqlHistoryStoreTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private static readonly Guid Connection = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SqlHistoryEntry Entry(string sql, params SqlVariable[] variables) =>
        new(sql, DateTimeOffset.Now, TimeSpan.FromMilliseconds(12), 3, false, false, null, variables);

    [Fact]
    public async Task Newest_first_repeats_replace_their_last_entry_and_values_survive()
    {
        var store = new SqlHistoryStore(_folder.Path);
        await store.AddAsync(Connection, Entry("SELECT 1 FROM dual"), Ct);
        await store.AddAsync(Connection, Entry("SELECT 2 FROM dual", new SqlVariable("id", SqlVariableType.Number, "4711")), Ct);
        await store.AddAsync(Connection, Entry(" SELECT 2 FROM dual "), Ct);

        var history = await new SqlHistoryStore(_folder.Path).LoadAsync(Connection, Ct);

        Assert.Equal([" SELECT 2 FROM dual ", "SELECT 1 FROM dual"], history.Select(e => e.Sql));
        Assert.Empty(await store.LoadAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task At_most_500_entries()
    {
        // A full history on disk and two more added – not 503 atomic writes (6 s; review 3.54.0, C10).
        var store = new SqlHistoryStore(_folder.Path);
        var full = Enumerable.Range(0, SqlHistoryStore.MaxEntries).Select(i => Entry($"SELECT {i} FROM dual")).Reverse().ToList();
        await File.WriteAllTextAsync(Path.Combine(_folder.Path, Connection.ToString("D") + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Entries = full }, FerretSharp.Core.IO.JsonFiles.Options), Ct);
        for (var i = SqlHistoryStore.MaxEntries; i < SqlHistoryStore.MaxEntries + 3; i++)
        {
            await store.AddAsync(Connection, Entry($"SELECT {i} FROM dual"), Ct);
        }

        var history = await store.LoadAsync(Connection, Ct);

        Assert.Equal(SqlHistoryStore.MaxEntries, history.Count);
        Assert.Equal($"SELECT {SqlHistoryStore.MaxEntries + 2} FROM dual", history[0].Sql);
    }

    [Fact]
    public async Task Variables_round_trip_and_prod_entries_keep_no_values()
    {
        var store = new SqlHistoryStore(_folder.Path);
        var entry = Entry("SELECT * FROM t WHERE id = :id", new SqlVariable("id", SqlVariableType.Number, "4711"));
        await store.AddAsync(Connection, entry.WithoutValues(), Ct);

        var loaded = Assert.Single(await store.LoadAsync(Connection, Ct));

        Assert.Equal([new SqlVariable("id", SqlVariableType.Number, "")], loaded.Variables);
        store.Delete(Connection);
        Assert.Empty(await store.LoadAsync(Connection, Ct));
    }

    [Fact]
    public async Task A_broken_file_is_an_empty_history()
    {
        await File.WriteAllTextAsync(_folder.Combine(Connection.ToString("D") + ".json"), "{ kaputt", Ct);

        Assert.Empty(await new SqlHistoryStore(_folder.Path).LoadAsync(Connection, Ct));
    }

    /// <summary>R5b (review 3.31.0, B2, decision of the user): a broken history was replaced without a trace at the next entry.</summary>
    [Theory]
    [InlineData("{ kaputt")]
    [InlineData("""{ "entries": [] }""")] // no version
    [InlineData("null")]
    public async Task A_broken_file_is_kept_as_a_copy_before_a_new_history_replaces_it(string content)
    {
        var path = _folder.Combine(Connection.ToString("D") + ".json");
        await File.WriteAllTextAsync(path, content, Ct);
        var store = new SqlHistoryStore(_folder.Path);

        await store.AddAsync(Connection, Entry("SELECT 1 FROM dual"), Ct);

        Assert.Equal(content, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_folder.Path, "*.json.bad-*")), Ct));
        Assert.Single(await store.LoadAsync(Connection, Ct));
    }

    [Fact]
    public async Task A_newer_or_locked_file_is_not_overwritten()
    {
        var path = _folder.Combine(Connection.ToString("D") + ".json");
        const string newer = """{ "version": 2, "entries": [] }""";
        await File.WriteAllTextAsync(path, newer, Ct);
        var store = new SqlHistoryStore(_folder.Path);

        var history = await store.AddAsync(Connection, Entry("SELECT 1 FROM dual"), Ct);

        Assert.Single(history); // shown for now …
        Assert.Equal(newer, await File.ReadAllTextAsync(path, Ct)); // … but the newer version's file stays

        File.Delete(path);
        await store.AddAsync(Connection, Entry("SELECT 1 FROM dual"), Ct);
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Single(await store.AddAsync(Connection, Entry("SELECT 2 FROM dual"), Ct));
        }

        Assert.Equal(["SELECT 1 FROM dual"], (await store.LoadAsync(Connection, Ct)).Select(e => e.Sql));
    }

    public void Dispose() => _folder.Dispose();
}
