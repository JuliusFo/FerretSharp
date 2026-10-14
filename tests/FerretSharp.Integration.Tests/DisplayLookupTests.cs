using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Display columns against Oracle (WP-31): the links found in the dictionary, looking names up with filled-up IN lists,
/// CHAR keys with their padding, values without a row.
/// </summary>
public sealed class DisplayLookupTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static bool _created;

    private static readonly string[] Setup =
    [
        """
        CREATE TABLE DSP_KUNDE (
            ID   NUMBER(10) PRIMARY KEY,
            NAME VARCHAR2(100),
            KZ   CHAR(5) CONSTRAINT UK_DSP_KUNDE_KZ UNIQUE)
        """,
        """
        CREATE TABLE DSP_AUFTRAG (
            ID           NUMBER(10) PRIMARY KEY,
            KUNDE_ID     NUMBER(10) CONSTRAINT FK_DSP_AUFTRAG_KUNDE REFERENCES DSP_KUNDE (ID),
            KUNDE_KZ     CHAR(5) CONSTRAINT FK_DSP_AUFTRAG_KZ REFERENCES DSP_KUNDE (KZ),
            VERTRETER_ID NUMBER(10))
        """,
        "INSERT INTO DSP_KUNDE VALUES (1, 'PetersPizza', 'PP')",
        "INSERT INTO DSP_KUNDE VALUES (2, 'Müller & Söhne', 'MS')",
        "INSERT INTO DSP_KUNDE VALUES (3, NULL, NULL)",
        "INSERT INTO DSP_AUFTRAG VALUES (10, 1, 'PP', 2)",
        "INSERT INTO DSP_AUFTRAG VALUES (11, 2, 'MS', 999)",
        "INSERT INTO DSP_AUFTRAG VALUES (12, 3, NULL, NULL)",
        "CREATE TABLE DSP_LAND (CODE NCHAR(3) PRIMARY KEY, NAME NVARCHAR2(50))",
        "CREATE TABLE DSP_STADT (ID NUMBER(10) PRIMARY KEY, LAND NCHAR(3) CONSTRAINT FK_DSP_STADT_LAND REFERENCES DSP_LAND (CODE))",
        "INSERT INTO DSP_LAND VALUES (N'PL', N'Polska')",
        "INSERT INTO DSP_LAND VALUES (N'DE', N'Deutschland')",
        "INSERT INTO DSP_STADT VALUES (1, N'PL')",
        "INSERT INTO DSP_STADT VALUES (2, N'DE')",
        "COMMIT",
    ];

    private OracleSession? _session;
    private SchemaCache _schema = null!;
    private OracleDataAccess _data = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DisplayColumns Columns = DisplayColumns.Empty.With("DSP_KUNDE", "NAME");

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        await SetupGate.WaitAsync(Ct);
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
            SetupGate.Release();
        }

        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Display tests"), Ct);
        _data = new OracleDataAccess(_session);
        _schema = new SchemaCache(new OracleSchemaReader(_session), profile.EffectiveSchema);
        await _schema.LoadAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    private async Task<TableDetails> DetailsAsync(string table) =>
        await _schema.GetDetailsAsync(_schema.Find(new TableRef(_schema.Owner, table))!, Ct);

    /// <summary>Reads the orders and looks up every name they need, as the grid does after a block.</summary>
    private async Task<(DisplayNames Names, IReadOnlyList<RowData> Rows, TableDetails Auftrag)> LookUpAsync(IReadOnlyList<DisplayLink>? links = null)
    {
        var auftrag = await DetailsAsync("DSP_AUFTRAG");
        links ??= await DisplayLinks.PlanAsync(auftrag, _schema, Columns, Ct);
        var names = new DisplayNames(links, (link, value) => CellFormatter.Format(link.Display, value));
        var rows = (await _data.ReadPageAsync(auftrag, [], [], new PageSpec(0, 100), Ct)).Rows;
        foreach (var lookup in names.Missing(rows.Select(r => r.Values)))
        {
            names.Found(lookup, (await DisplayNames.LookupAsync(_data, lookup, DisplayNames.LookupTimeout, Ct))!);
        }

        return (names, rows, auftrag);
    }

    [Fact]
    public async Task Foreign_keys_to_the_primary_key_and_to_a_unique_char_key_get_names()
    {
        var (names, rows, auftrag) = await LookUpAsync();

        Assert.Equal(["KUNDE_ID", "KUNDE_KZ"], names.Links.Select(l => auftrag.Columns[l.Column].Name));
        var kundeId = auftrag.IndexOf("KUNDE_ID");
        var kz = auftrag.IndexOf("KUNDE_KZ");
        Assert.Equal(
            ["PetersPizza", "Müller & Söhne", null],
            rows.Select(r => names.NameOf(kundeId, r.Values[kundeId])?.Text));
        Assert.Equal(new LookedUpName(null, false), names.NameOf(kundeId, rows[2].Values[kundeId])); // found, name is NULL
        Assert.Equal("PetersPizza", names.NameOf(kz, rows[0].Values[kz])?.Text); // CHAR(5): 'PP   '
        Assert.Equal("Müller & Söhne", names.NameOf(kz, "MS")?.Text);
    }

    [Fact]
    public async Task A_value_without_a_row_is_not_found()
    {
        var auftrag = await DetailsAsync("DSP_AUFTRAG");
        var kunde = await DetailsAsync("DSP_KUNDE");
        // A relationship of the C# model without a constraint: 999 has no customer.
        var fk = new ForeignKeyInfo("Auftrag.Vertreter", auftrag.Table.Ref, ["VERTRETER_ID"], kunde.Table.Ref, ["ID"], FkSource.ClrModel);
        var link = DisplayLinks.Resolve(auftrag, auftrag.IndexOf("VERTRETER_ID"), fk, kunde, Columns)!;

        var (names, rows, _) = await LookUpAsync([link]);

        Assert.Equal([new LookedUpName("Müller & Söhne", false), new LookedUpName(null, true), null],
            rows.Select(r => names.NameOf(link.Column, r.Values[link.Column])));
    }

    /// <summary>
    /// R5b: an NCHAR key gets names. WP-31 left it out because its keys were bound as NVARCHAR2 (no blank padding: shorter
    /// values never matched); since A18 they bind as NCHAR.
    /// </summary>
    [Fact]
    public async Task A_foreign_key_to_an_nchar_key_gets_names()
    {
        var stadt = await DetailsAsync("DSP_STADT");
        var links = await DisplayLinks.PlanAsync(stadt, _schema, DisplayColumns.Empty.With("DSP_LAND", "NAME"), Ct);
        var link = Assert.Single(links);
        var names = new DisplayNames(links, (l, value) => CellFormatter.Format(l.Display, value));
        var rows = (await _data.ReadPageAsync(stadt, [], [], new PageSpec(0, 100), Ct)).Rows;
        foreach (var lookup in names.Missing(rows.Select(r => r.Values)))
        {
            names.Found(lookup, (await DisplayNames.LookupAsync(_data, lookup, DisplayNames.LookupTimeout, Ct))!);
        }

        Assert.Equal(["Polska", "Deutschland"], rows.Select(r => names.NameOf(link.Column, r.Values[link.Column])?.Text));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(60)]
    [InlineData(500)]
    public async Task Filled_up_in_lists_return_each_row_once(int keys)
    {
        var kunde = await DetailsAsync("DSP_KUNDE");

        var rows = await _data.LookupAsync(kunde, 0, 1, Enumerable.Range(1, keys).Select(i => (object)(decimal)i).ToList(), Ct);

        Assert.Equal(Math.Min(keys, 3), rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
    }
}
