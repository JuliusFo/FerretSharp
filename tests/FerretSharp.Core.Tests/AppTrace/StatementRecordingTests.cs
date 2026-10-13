using FerretSharp.Core.AppTrace;

namespace FerretSharp.Core.Tests.AppTrace;

/// <summary>WP-33: the statements of a traced app from its EF Core events – event sequences as seen in the spike.</summary>
public sealed class StatementRecordingTests
{
    private const string Orders = "SELECT \"a\".\"Id\", \"a\".\"Nummer\"\nFROM \"Auftraege\" \"a\"\nORDER BY \"a\".\"Id\"";
    private const string Lines = "SELECT \"p\".\"Id\", \"p\".\"Artikel\"\nFROM \"Positionen\" \"p\"\nWHERE \"p\".\"AuftragId\" = :p_0";
    private const string Update = "UPDATE \"Positionen\" SET \"Menge\" = \"Menge\" WHERE \"Id\" = :p0";

    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private const string Ctx = "8af2d955-ed6d-44f6-9c19-50210305a742:0";

    private int _ids;

    private static EfEvent Event(string name, double at, params (string Key, string Value)[] fields) =>
        new(name, fields.ToDictionary(f => f.Key, f => f.Value), T0.AddMilliseconds(at));

    private static string Ms(double ms) => TimeSpan.FromMilliseconds(ms).ToString();

    private EfEvent Executed(string text, string source, double ms, double at = 0, string context = Ctx, string method = "ExecuteReader", string? result = null, string? id = null) =>
        Event(EfEvents.CommandExecuted, at,
            (EfEvents.Text, text), (EfEvents.Source, source), (EfEvents.Method, method), (EfEvents.Result, result ?? "Oracle.ManagedDataAccess.Client.OracleDataReader"),
            (EfEvents.Duration, Ms(ms)), (EfEvents.Context, context), (EfEvents.CommandId, id ?? $"cmd-{++_ids}"), (EfEvents.DataSource, "localhost:1531/FREEPDB1"));

    private static EfEvent ReaderClosed(string id, int readCount, double ms, double at = 0) =>
        Event(EfEvents.DataReaderDisposing, at, (EfEvents.ReadCount, readCount.ToString(System.Globalization.CultureInfo.InvariantCulture)), (EfEvents.Duration, Ms(ms)), (EfEvents.CommandId, id));

    private static EfEvent Lazy(string navigation, double at = 0, string context = Ctx) =>
        Event(EfEvents.NavigationLazyLoading, at, (EfEvents.Navigation, navigation), (EfEvents.Context, context));

    [Fact]
    public void Commands_are_grouped_by_origin_and_statement()
    {
        var recording = new StatementRecording();

        recording.Apply(Executed(Orders, "LinqQuery", 2));
        recording.Apply(Executed(Update, "ExecuteSqlRaw", 3, method: "ExecuteNonQuery", result: "1"));
        recording.Apply(Executed("BEGIN …", "SaveChanges", 1));
        recording.Apply(Executed("UPDATE \"Positionen\" \"p\" SET …", "BulkUpdate", 4, method: "ExecuteNonQuery", result: "6"));
        recording.Apply(Executed("SELECT * FROM \"Positionen\"", "FromSqlQuery", 1));
        recording.Apply(Executed(Orders, "LinqQuery", 1));

        var snapshot = recording.Snapshot();
        Assert.Equal(6, snapshot.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(12), snapshot.Time);
        Assert.Equal(
            [StatementOrigin.ExecuteUpdate, StatementOrigin.Linq, StatementOrigin.ExecuteSql, StatementOrigin.FromSql, StatementOrigin.SaveChanges],
            snapshot.Origins.Select(o => o.Origin)); // by time, ties by origin
        var orders = Assert.Single(snapshot.Statements, s => s.Text == Orders);
        Assert.Equal((StatementOrigin.Linq, 2, TimeSpan.FromMilliseconds(3)), (orders.Origin, orders.Count, orders.Time));
        Assert.Equal(6, Assert.Single(snapshot.Statements, s => s.Origin == StatementOrigin.ExecuteUpdate).Rows); // affected rows
        Assert.Equal("localhost:1531/FREEPDB1", snapshot.DataSource);
        Assert.Equal(T0, snapshot.Since);
        Assert.Equal(4.0 / 12, snapshot.Share(snapshot.Origins[0]), 6); // ExecuteUpdate: 4 of 12 ms
    }

    [Fact]
    public void The_command_after_a_lazy_load_in_the_same_context_is_the_lazy_load()
    {
        var recording = new StatementRecording();

        recording.Apply(Executed(Orders, "LinqQuery", 1));
        recording.Apply(Lazy("Positionen"));
        recording.Apply(Executed(Lines, "LinqQuery", 1));
        recording.Apply(Lazy("Positionen", context: "other:0"));
        recording.Apply(Executed(Lines, "LinqQuery", 1)); // same context as the first lazy load, which is used up
        recording.Apply(Executed(Lines, "LinqQuery", 1, context: "other:0"));

        var statements = recording.Snapshot().Statements;
        var lazy = Assert.Single(statements, s => s.Origin == StatementOrigin.LazyLoading);
        Assert.Equal((Lines, 2, "Positionen"), (lazy.Text, lazy.Count, lazy.Navigation));
        Assert.Equal(1, Assert.Single(statements, s => s.Origin == StatementOrigin.Linq && s.Text == Lines).Count);
    }

    [Fact]
    public void Reading_the_rows_adds_its_time_and_the_rows_without_the_last_read()
    {
        var recording = new StatementRecording();

        recording.Apply(Executed(Orders, "LinqQuery", 2, id: "a"));
        var changed = recording.Apply(ReaderClosed("a", 6, 5));

        Assert.True(changed);
        var snapshot = recording.Snapshot();
        var orders = Assert.Single(snapshot.Statements);
        Assert.Equal((TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(7), 5L), (orders.Time, orders.Max, orders.Rows));
        Assert.Equal((TimeSpan.FromMilliseconds(7), 5L), (snapshot.Origins[0].Time, snapshot.Origins[0].Rows));
        Assert.Equal(TimeSpan.FromMilliseconds(7), snapshot.Time);
        Assert.False(recording.Apply(ReaderClosed("a", 6, 5))); // once
        Assert.False(recording.Apply(ReaderClosed("unknown", 2, 1)));
    }

    [Fact]
    public void Scalar_and_reader_results_are_no_row_counts()
    {
        var recording = new StatementRecording();

        recording.Apply(Executed("SELECT COUNT(*) FROM \"Positionen\"", "LinqQuery", 1, method: "ExecuteScalar", result: "42"));

        Assert.Equal(0, Assert.Single(recording.Snapshot().Statements).Rows);
    }

    [Fact]
    public void Errors_count_as_runs_with_their_message()
    {
        var recording = new StatementRecording();

        recording.Apply(Event(EfEvents.CommandError, 0, (EfEvents.Text, Update), (EfEvents.Source, "ExecuteSqlRaw"), (EfEvents.Duration, Ms(2)),
            (EfEvents.Context, Ctx), (EfEvents.CommandId, "x"), (EfEvents.Error, "ORA-00001: unique constraint violated")));

        var statement = Assert.Single(recording.Snapshot().Statements);
        Assert.Equal((1, 1, "ORA-00001: unique constraint violated"), (statement.Count, statement.Errors, statement.LastError));
    }

    [Fact]
    public void Ten_runs_within_two_seconds_are_repeated()
    {
        var recording = new StatementRecording();

        for (var i = 0; i < 9; i++)
        {
            recording.Apply(Executed(Lines, "LinqQuery", 1, at: i * 300)); // 9 runs in 2.4 s: at most 7 within 2 s
        }

        var spread = Assert.Single(recording.Snapshot().Statements);
        Assert.Equal((7, false), (spread.Burst, spread.Repeated));

        for (var i = 0; i < 10; i++)
        {
            recording.Apply(Executed(Lines, "LinqQuery", 1, at: 10_000 + i * 50));
        }

        var burst = Assert.Single(recording.Snapshot().Statements);
        Assert.Equal((10, true), (burst.Burst, burst.Repeated));
    }

    [Fact]
    public void The_rule_can_be_set()
    {
        var recording = new StatementRecording(new RepeatRule(3, TimeSpan.FromSeconds(1)));

        recording.Apply(Executed(Lines, "LinqQuery", 1, at: 0));
        recording.Apply(Executed(Lines, "LinqQuery", 1, at: 400));
        recording.Apply(Executed(Lines, "LinqQuery", 1, at: 800));

        Assert.True(Assert.Single(recording.Snapshot().Statements).Repeated);
    }

    [Fact]
    public void Reset_forgets_everything_and_changes_the_version()
    {
        var recording = new StatementRecording();
        recording.Apply(Executed(Orders, "LinqQuery", 1, id: "a"));
        recording.Apply(Lazy("Positionen"));
        var before = recording.Snapshot().Version;

        recording.Reset();
        recording.Apply(ReaderClosed("a", 3, 1)); // a reader of before the reset
        recording.Apply(Executed(Lines, "LinqQuery", 1, at: 500)); // the lazy load of before is forgotten

        var snapshot = recording.Snapshot();
        Assert.True(snapshot.Version > before);
        Assert.Equal(StatementOrigin.Linq, Assert.Single(snapshot.Statements).Origin);
        Assert.Equal((1, T0.AddMilliseconds(500)), (snapshot.Count, snapshot.Since));
    }

    [Fact]
    public void Unknown_events_and_missing_sources_change_little()
    {
        var recording = new StatementRecording();

        Assert.False(recording.Apply(Event("Microsoft.EntityFrameworkCore.Something", 0)));
        Assert.False(recording.Apply(Event(EfEvents.CommandExecuted, 0, (EfEvents.Source, "LinqQuery")))); // no text
        Assert.False(recording.Apply(Lazy("Positionen"))); // only remembered
        Assert.True(recording.Apply(Event(EfEvents.CommandExecuted, 0, (EfEvents.Text, "SELECT 1 FROM DUAL"), (EfEvents.Context, "c:0")))); // EF 6: no source

        Assert.Equal(StatementOrigin.Unknown, Assert.Single(recording.Snapshot().Statements).Origin);
        Assert.Equal(1, recording.Snapshot().Version);
    }

    [Fact]
    public void Statements_beyond_the_limit_only_count_per_origin()
    {
        var recording = new StatementRecording();

        for (var i = 0; i <= StatementRecording.MaxStatements; i++)
        {
            recording.Apply(Executed($"SELECT {i} FROM DUAL", "ExecuteSqlRaw", 1, method: "ExecuteNonQuery", result: "0"));
        }

        var snapshot = recording.Snapshot();
        Assert.True(snapshot.Truncated);
        Assert.Equal(StatementRecording.MaxStatements, snapshot.Statements.Count);
        Assert.Equal(StatementRecording.MaxStatements + 1, Assert.Single(snapshot.Origins).Count);
    }

    [Theory]
    [InlineData("LinqQuery", StatementOrigin.Linq)]
    [InlineData("FromSqlQuery", StatementOrigin.FromSql)]
    [InlineData("ExecuteSqlRaw", StatementOrigin.ExecuteSql)]
    [InlineData("SaveChanges", StatementOrigin.SaveChanges)]
    [InlineData("BulkUpdate", StatementOrigin.ExecuteUpdate)]
    [InlineData("Migrations", StatementOrigin.Other)]
    [InlineData("ValueGenerator", StatementOrigin.Other)]
    [InlineData("Unknown", StatementOrigin.Unknown)]
    [InlineData(null, StatementOrigin.Unknown)]
    public void Command_sources_map_to_origins(string? source, StatementOrigin origin) =>
        Assert.Equal(origin, StatementOrigins.FromCommandSource(source));

    [Fact]
    public void The_payload_specs_never_ask_for_the_connection_string_or_parameters()
    {
        Assert.DoesNotContain("ConnectionString", EfEvents.PayloadSpecs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Parameters", EfEvents.PayloadSpecs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Entity=", EfEvents.PayloadSpecs, StringComparison.Ordinal); // an entity's ToString may print its values
        Assert.All(EfEvents.PayloadSpecs.Split('\n'), line => Assert.StartsWith(EfEvents.Listener + "/" + EfEvents.Listener + ".", line));
    }
}
