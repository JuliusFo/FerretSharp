using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// WP-48 spike: what ODP.NET reports as the origin of a result column (<c>BaseSchemaName</c>, <c>BaseTableName</c>,
/// <c>BaseColumnName</c> of <c>GetSchemaTable()</c>) without <see cref="CommandBehavior.KeyInfo"/>. ODP.NET parses the
/// statement on the client: the table is the name as written (synonym, view), the schema only if written, and a quoted
/// alias (EF Core's <c>"a"</c>) or one after CROSS APPLY comes back as the alias. Set operators report the first branch.
/// The origin is a hint to resolve against the schema, never a fact (protocol: docs/work-packages/wp-48-sql-result-origin.md).
/// </summary>
public sealed class SqlResultOriginDriverTests(OracleContainerFixture oracle)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string[]> Origins => new()
    {
        { "SELECT KUNDE_ID, NAME FROM KUNDEN", ["∅.KUNDEN.KUNDE_ID", "∅.KUNDEN.NAME"] },
        { "SELECT k.* FROM KUNDEN k WHERE ROWNUM = 1", ["∅.KUNDEN.KUNDE_ID", "∅.KUNDEN.NAME", "∅.KUNDEN.KUERZEL", "∅.KUNDEN.ERSTELLT_AM", "∅.KUNDEN.UMSATZ", "∅.KUNDEN.ANZAHL"] },
        { "SELECT KUNDE_ID AS ID, NAME AS KUERZEL FROM KUNDEN", ["∅.KUNDEN.KUNDE_ID", "∅.KUNDEN.NAME"] },
        { "select kunde_id from kunden", ["∅.KUNDEN.KUNDE_ID"] },
        { "SELECT a.AUFTRAG_ID, k.NAME FROM AUFTRAG a JOIN KUNDEN k ON k.KUNDE_ID = a.KUNDE_ID", ["∅.AUFTRAG.AUFTRAG_ID", "∅.KUNDEN.NAME"] },
        { "SELECT a.AUFTRAG_ID, k.NAME FROM AUFTRAG a, KUNDEN k WHERE k.KUNDE_ID = a.KUNDE_ID", ["∅.AUFTRAG.AUFTRAG_ID", "∅.KUNDEN.NAME"] },
        { "SELECT KUNDEN.AUFTRAG_ID FROM AUFTRAG KUNDEN", ["∅.AUFTRAG.AUFTRAG_ID"] },
        { "SELECT ROWID, KUNDE_ID FROM KUNDEN", ["∅.KUNDEN.ROWID", "∅.KUNDEN.KUNDE_ID"] },
        { """SELECT "Id" FROM "MixedCase" """, ["∅.MixedCase.Id"] },
        { "SELECT KUNDE_ID, ANZAHL FROM V_KUNDEN_AUFTRAEGE", ["∅.V_KUNDEN_AUFTRAEGE.KUNDE_ID", "∅.V_KUNDEN_AUFTRAEGE.ANZAHL"] },
        // The schema only if written.
        { "SELECT k.KUNDE_ID FROM {user}.KUNDEN k", ["{user}.KUNDEN.KUNDE_ID"] },
        // Expressions, aggregates, scalar subqueries and CASE have no origin.
        { "SELECT KUNDE_ID + 0 AS X, COUNT(*) OVER () AS C, (SELECT 1 FROM DUAL) AS S FROM KUNDEN", ["∅.∅.∅", "∅.∅.∅", "∅.∅.∅"] },
        { "SELECT KUNDE_ID, COUNT(*) AS N FROM AUFTRAG GROUP BY KUNDE_ID", ["∅.AUFTRAG.KUNDE_ID", "∅.∅.∅"] },
        // EF Core quotes its aliases: ODP.NET gives the alias, upper or lower case, also for a subquery.
        { """SELECT "a"."AUFTRAG_ID", "k"."KUNDE_ID" FROM "AUFTRAG" "a" INNER JOIN "KUNDEN" "k" ON "a"."KUNDE_ID" = "k"."KUNDE_ID" """, ["∅.a.AUFTRAG_ID", "∅.k.KUNDE_ID"] },
        { """SELECT "A"."KUNDE_ID" FROM "AUFTRAG" "A" """, ["∅.A.KUNDE_ID"] },
        { """SELECT a.KUNDE_ID FROM "AUFTRAG" a""", ["∅.AUFTRAG.KUNDE_ID"] },
        { """SELECT KUNDE_ID FROM "AUFTRAG" "a" """, ["∅.AUFTRAG.KUNDE_ID"] },
        { """SELECT "t"."KUNDE_ID" FROM (SELECT "k"."KUNDE_ID" FROM "KUNDEN" "k" FETCH FIRST 5 ROWS ONLY) "t" """, ["∅.t.KUNDE_ID"] },
        // An inline view and CROSS APPLY: the (upper-cased) alias.
        { "SELECT x.N FROM (SELECT NAME AS N FROM KUNDEN) x", ["∅.X.N"] },
        { "SELECT k.KUNDE_ID, x.AUFTRAG_ID FROM KUNDEN k CROSS APPLY (SELECT a.AUFTRAG_ID FROM AUFTRAG a WHERE a.KUNDE_ID = k.KUNDE_ID) x", ["∅.K.KUNDE_ID", "∅.X.AUFTRAG_ID"] },
        // WITH and JOIN … USING: no table.
        { "WITH k AS (SELECT KUNDE_ID FROM KUNDEN) SELECT k.KUNDE_ID FROM k", ["∅.∅.∅"] },
        { "SELECT AUFTRAG_ID, NAME FROM AUFTRAG JOIN KUNDEN USING (KUNDE_ID)", ["∅.∅.AUFTRAG_ID", "∅.∅.NAME"] },
        // Set operators: the first branch, although the rows come from both – wrong as an origin.
        { "SELECT AUFTRAG_ID FROM AUFTRAG UNION SELECT KUNDE_ID FROM KUNDEN", ["∅.AUFTRAG.AUFTRAG_ID"] },
        { "SELECT KUNDE_ID FROM AUFTRAG MINUS SELECT KUNDE_ID FROM KUNDEN", ["∅.AUFTRAG.KUNDE_ID"] },
    };

    [Theory]
    [MemberData(nameof(Origins))]
    public async Task The_driver_reports_the_origin_as_written(string sql, string[] expected)
    {
        var connectionString = oracle.RequireConnectionString();
        await SampleSchema.EnsureCreatedAsync(connectionString, Ct);
        var user = new OracleConnectionStringBuilder(connectionString).UserID.ToUpperInvariant();

        Assert.Equal(expected.Select(e => e.Replace("{user}", user, StringComparison.Ordinal)), await OriginsAsync(connectionString, sql.Replace("{user}", user, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_synonym_comes_back_as_the_synonym()
    {
        var connectionString = oracle.RequireConnectionString();
        await SampleSchema.EnsureCreatedAsync(connectionString, Ct);
        await SynonymSchema.EnsureCreatedAsync(connectionString, Ct);
        Assert.SkipUnless(SynonymSchema.Available, SynonymSchema.UnavailableReason ?? "");

        Assert.Equal(["∅.S_PRODUKT.ID"], await OriginsAsync(connectionString, "SELECT ID FROM S_PRODUKT"));
        Assert.Equal(["∅.FERRET_KATEGORIE.ID"], await OriginsAsync(connectionString, "SELECT ID FROM FERRET_KATEGORIE"));
        Assert.Equal([$"{SynonymSchema.OtherOwner}.PRODUKT.ID"], await OriginsAsync(connectionString, $"SELECT p.ID FROM {SynonymSchema.OtherOwner}.PRODUKT p"));
    }

    private static async Task<string[]> OriginsAsync(string connectionString, string sql)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return [.. reader.GetSchemaTable()!.Rows.Cast<DataRow>().Select(r => $"{Show(r["BaseSchemaName"])}.{Show(r["BaseTableName"])}.{Show(r["BaseColumnName"])}")];
    }

    private static string Show(object value) => value is string { Length: > 0 } text ? text : "∅";
}
