using System.Globalization;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// WP-69: a query built in the relationship diagram finds the same rows as LINQ in the console of the sample project (EF
/// Core 8 with the Oracle provider) and as the SQL FerretSharp writes – a required parent (INNER JOIN), a condition with
/// "≠" on a nullable column of the parent, a chosen child with its condition and order, also when no child fits. The code
/// is the generator's as the console gets it (its <c>using</c> lines first); the sample project has no LinqKit, so the
/// values stand in the projection as whole paths – the expressions LinqKit expands are the same paths. The query that reads
/// the child once runs too. Rows of our own (<c>QD_…</c>), so the sample's stay as they are.
/// </summary>
public sealed class QueryDesignTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static readonly (string Sample, string Own)[] Tables = [("KUNDEN", "QD_KUNDEN"), ("AUFTRAG", "QD_AUFTRAG"), ("AUFTRAG_POSITION", "QD_POSITION")];

    private static readonly string[] Setup =
    [
        "CREATE TABLE QD_KUNDEN (KUNDE_ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(100 CHAR) NOT NULL, KUERZEL CHAR(3 BYTE), ERSTELLT_AM DATE DEFAULT SYSDATE NOT NULL, UMSATZ NUMBER(12,2), ANZAHL INTEGER)",
        "CREATE TABLE QD_AUFTRAG (AUFTRAG_ID NUMBER PRIMARY KEY, KUNDE_ID NUMBER(10) NOT NULL REFERENCES QD_KUNDEN, NOTIZ CLOB)",
        "CREATE TABLE QD_POSITION (AUFTRAG_ID NUMBER NOT NULL REFERENCES QD_AUFTRAG, POS_NR NUMBER(4) NOT NULL, MENGE NUMBER, PRIMARY KEY (AUFTRAG_ID, POS_NR))",
        "INSERT INTO QD_KUNDEN (KUNDE_ID, NAME, UMSATZ) VALUES (1, 'Anna', 100)",
        "INSERT INTO QD_KUNDEN (KUNDE_ID, NAME, UMSATZ) VALUES (2, 'Bert', NULL)",
        "INSERT INTO QD_KUNDEN (KUNDE_ID, NAME, UMSATZ) VALUES (3, 'Carl', 500)",
        "INSERT INTO QD_AUFTRAG (AUFTRAG_ID, KUNDE_ID) VALUES (10, 1)",
        "INSERT INTO QD_AUFTRAG (AUFTRAG_ID, KUNDE_ID) VALUES (11, 1)",
        "INSERT INTO QD_AUFTRAG (AUFTRAG_ID, KUNDE_ID) VALUES (12, 2)",
        "INSERT INTO QD_AUFTRAG (AUFTRAG_ID, KUNDE_ID) VALUES (13, 3)",
        "INSERT INTO QD_POSITION VALUES (10, 1, 5)",
        "INSERT INTO QD_POSITION VALUES (10, 2, 10)",
        "INSERT INTO QD_POSITION VALUES (11, 1, NULL)",
        "INSERT INTO QD_POSITION VALUES (12, 1, 0)",
        "INSERT INTO QD_POSITION VALUES (12, 2, 3)",
        "COMMIT",
    ];

    private OracleSession? _session;
    private OracleDataAccess _data = null!;
    private SchemaCache _schema = null!;
    private ClrModelMapping _mapping = null!;
    private ILinqConsole? _console;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        var link = ModelHostTests.SampleLink(); // skips without the .NET 8 runtime
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        await Gate.WaitAsync(Ct);
        try
        {
            if (!_created)
            {
                await using var connection = new OracleConnection(oracle.RequireConnectionString());
                await connection.OpenAsync(Ct);
                foreach (var statement in Setup)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(Ct);
                }

                _created = true;
            }
        }
        finally
        {
            Gate.Release();
        }

        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Query design"), Ct);
        _data = new OracleDataAccess(_session);
        _schema = new SchemaCache(new OracleSchemaReader(_session), profile.EffectiveSchema);
        await _schema.LoadAsync(Ct);

        var model = (await ModelHostTests.Runner().ReadModelAsync(link, BuildOutputLocator.Find(link), Ct)).Model!;
        _mapping = await ClrModelMapping.BuildAsync(model, _schema, Ct);
        _console = await ModelHostTests.Runner().StartConsoleAsync(link, BuildOutputLocator.Find(link), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_console is not null)
        {
            await _console.DisposeAsync();
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    private TableRef Ref(string name) => new(_schema.Owner, name);

    private QueryStep Step(string relation, bool toChild) =>
        new(RelationGraph.KeyOf(_schema.ForeignKeys.Single(f => f.Name == relation && f.Source == FkSource.Declared)), toChild);

    /// <summary>The statement on our own tables: the sample's names (as the model maps them) replaced by ours.</summary>
    private static string OnOwnTables(string sql) =>
        Tables.Aggregate(sql, (text, t) => text.Replace($"\"{t.Sample}\"", $"\"{t.Own}\"", StringComparison.Ordinal));

    private static List<string> Rows(SqlPage page) =>
        [.. page.Rows.Select(r => string.Join("|", r.Select(v => v is null or DBNull ? "NULL" : Convert.ToString(v, CultureInfo.InvariantCulture)))).Order(StringComparer.Ordinal)];

    private async Task<List<string>> LinqRowsAsync(string script)
    {
        var result = await _console!.RunAsync(script, "", Ct);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics) + "\n" + script);
        Assert.Null(result.Exception);
        var query = LinqStatements.ToQuery(Assert.Single(result.Commands));
        return Rows(await _data.ReadSqlAsync(query with { Sql = OnOwnTables(query.Sql) }, 0, 100, Ct));
    }

    [Fact]
    public async Task Linq_and_sql_of_a_query_from_the_diagram_find_the_same_rows()
    {
        var kunde = Step("FK_AUFTRAG_KUNDE", toChild: false);
        var position = Step("FK_POS_AUFTRAG", toChild: true);
        var state = QueryDesignState.Empty
            .Update([], n => n with { Columns = [new QueryColumn("AUFTRAG_ID")] })
            .Add([], kunde).Update([kunde], n => n with
            {
                Columns = [new QueryColumn("NAME")],
                Filters = [new QueryFilter(FilterCondition.Of("UMSATZ", FilterOperator.NotEquals, "500"))],
            })
            .Add([], position).Update([position], n => n with
            {
                Columns = [new QueryColumn("POS_NR"), new QueryColumn("MENGE")],
                Filters = [new QueryFilter(FilterCondition.Of("MENGE", FilterOperator.Gt, "0"))],
                Sorts = [new SortSpec("MENGE", true)],
            });
        var design = QueryDesign.Resolve(Ref("AUFTRAG"), state, RelationGraph.Of(_schema));
        var details = new Dictionary<TableRef, TableDetails>();
        foreach (var table in design.Nodes.Select(n => n.Table).Distinct())
        {
            details[table] = await _schema.GetDetailsAsync(_schema.Find(table)!, Ct);
        }

        var sql = QueryBuilder.BuildDesign(design, t => details[t], DesignSqlMode.Statement);
        var sqlRows = Rows(await _data.ReadSqlAsync(new QuerySpec(OnOwnTables(sql.Sql), sql.Parameters), 0, 100, Ct));

        // Carl (500) is left out; Bert's NULL is "≠ 500" as in C#; order 11 has no position with a quantity, 12 takes its 3 over its 0.
        Assert.Equal(["10|Anna|2|10", "11|Anna|NULL|NULL", "12|Bert|2|3"], sqlRows);

        var code = QueryCodeBuilder.Build(design, _mapping, t => details[t], QueryCodeOptions.For("Auftrag") with { LinqKit = false });
        Assert.Empty(code.Notes);
        Assert.Equal(sqlRows, await LinqRowsAsync(code.Script));

        var once = code.Script[..code.Script.LastIndexOf(code.Usage, StringComparison.Ordinal)] + code.SingleRead;
        Assert.Equal(sqlRows, await LinqRowsAsync(once));
    }
}
