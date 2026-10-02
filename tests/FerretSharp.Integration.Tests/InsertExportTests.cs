using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The INSERT export reproduces the rows exactly: the test (not the app – v1 never executes it) runs the script against
/// a copy of the table and compares both tables value by value.
/// </summary>
public sealed class InsertExportTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static bool _created;

    private const string Columns = """
        ID      NUMBER(10) PRIMARY KEY,
        BIG     NUMBER(38),
        BETRAG  NUMBER(12,2),
        TEXT    VARCHAR2(100 CHAR),
        NTEXT   NVARCHAR2(50),
        KZ      CHAR(3),
        DATUM   DATE,
        TS      TIMESTAMP(6),
        TSTZ    TIMESTAMP(6) WITH TIME ZONE,
        DAUER   INTERVAL DAY(2) TO SECOND(6),
        LAUFZEIT INTERVAL YEAR(2) TO MONTH,
        BIN     RAW(16),
        DBL     BINARY_DOUBLE,
        FLAG    BOOLEAN,
        NOTIZ   CLOB,
        BILD    BLOB
        """;

    private static readonly string[] Setup =
    [
        $"CREATE TABLE EXPORT_SRC ({Columns})",
        $"CREATE TABLE EXPORT_DST ({Columns})",
        """
        INSERT INTO EXPORT_SRC VALUES (1, 12345678901234567890123456789012345678, -1234.56, 'O''Brien & Söhne; "Ltd"' || CHR(10) || 'Zeile 2',
            N'Grüße ✓', 'AB', TO_DATE('2026-10-01 13:45:07', 'YYYY-MM-DD HH24:MI:SS'),
            TIMESTAMP '2026-10-01 13:45:07.123456', TIMESTAMP '2026-10-01 13:45:07.5 +02:00',
            INTERVAL '1 02:03:04.5' DAY TO SECOND, INTERVAL '3-7' YEAR TO MONTH,
            HEXTORAW('0102030405060708090A0B0C0D0E0F10'), 1.5, TRUE, 'kurze Notiz', HEXTORAW('CAFE'))
        """,
        "INSERT INTO EXPORT_SRC (ID, DBL, FLAG, NOTIZ) VALUES (2, BINARY_DOUBLE_INFINITY, FALSE, EMPTY_CLOB())",
        "INSERT INTO EXPORT_SRC (ID) VALUES (3)",
        "COMMIT",
    ];

    private OracleSession? _session;
    private OracleDataAccess _data = null!;
    private OracleSchemaReader _reader = null!;
    private string _owner = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        _owner = profile.EffectiveSchema;
        await SetupGate.WaitAsync(Ct);
        try
        {
            if (!_created)
            {
                await ExecuteAsync(Setup);
                _created = true;
            }
        }
        finally
        {
            SetupGate.Release();
        }

        _session = await OracleSession.OpenAsync(OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Export tests"), Ct);
        _data = new OracleDataAccess(_session);
        _reader = new OracleSchemaReader(_session);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    private async Task ExecuteAsync(IEnumerable<string> statements)
    {
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private async Task<(TableDetails Details, IReadOnlyList<RowData> Rows)> ReadAsync(string table)
    {
        var details = await _reader.GetDetailsAsync(new TableSummary(_owner, table, TableKind.Table), Ct);
        var page = await _data.ReadPageAsync(details, [], [], new PageSpec(0, 100), Ct);
        return (details, page.Rows);
    }

    [Fact]
    public async Task Executed_script_reproduces_every_value_except_lobs_that_were_only_previewed()
    {
        var (details, rows) = await ReadAsync("EXPORT_SRC");

        var export = InsertExport.Build(details, rows);
        var statements = export.Text.Split("\r\n")
            .Where(l => l.StartsWith("INSERT", StringComparison.Ordinal))
            .Select(l => l.Replace("\"EXPORT_SRC\"", "\"EXPORT_DST\"", StringComparison.Ordinal).TrimEnd(';'))
            .Append("COMMIT");
        await ExecuteAsync(statements);
        var (_, copied) = await ReadAsync("EXPORT_DST");

        Assert.Equal(["BILD (BLOB): 1 Wert nicht exportiert – nur die Länge geladen."], export.Warnings);
        Assert.Equal(rows.Count, copied.Count);
        var bild = details.Columns.ToList().FindIndex(c => c.Name == "BILD");
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < details.Columns.Count; c++)
            {
                var expected = c == bild ? null : rows[r].Values[c];
                Assert.True(Equals(expected, copied[r].Values[c]) || expected is byte[] e && copied[r].Values[c] is byte[] a && e.SequenceEqual(a),
                    $"Row {r + 1}, {details.Columns[c].Name}: {Show(expected)} ≠ {Show(copied[r].Values[c])}");
            }
        }
    }

    private static string Show(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => $"{value} ({value.GetType().Name})",
    };
}
