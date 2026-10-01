using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>FK navigation against Oracle: composite keys, RAW(16) keys (GUIDs as EF Core stores them), counts with timeout.</summary>
public sealed class FkNavigationTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static bool _created;

    private static readonly string[] Setup =
    [
        """
        CREATE TABLE NAV_KOPF (
            MANDANT NUMBER(4) NOT NULL,
            NR      NUMBER(10) NOT NULL,
            GUID    RAW(16) NOT NULL CONSTRAINT UK_NAV_KOPF_GUID UNIQUE,
            CONSTRAINT PK_NAV_KOPF PRIMARY KEY (MANDANT, NR))
        """,
        """
        CREATE TABLE NAV_POS (
            ID        NUMBER PRIMARY KEY,
            MANDANT   NUMBER(4),
            KOPF_NR   NUMBER(10),
            KOPF_GUID RAW(16),
            CONSTRAINT FK_NAV_POS_KOPF FOREIGN KEY (MANDANT, KOPF_NR) REFERENCES NAV_KOPF (MANDANT, NR),
            CONSTRAINT FK_NAV_POS_GUID FOREIGN KEY (KOPF_GUID) REFERENCES NAV_KOPF (GUID))
        """,
        "INSERT INTO NAV_KOPF VALUES (1, 4711, HEXTORAW('0102030405060708090A0B0C0D0E0F10'))",
        "INSERT INTO NAV_KOPF VALUES (2, 4711, HEXTORAW('A1A2A3A4A5A6A7A8A9AAABACADAEAFB0'))",
        "INSERT INTO NAV_POS VALUES (1, 1, 4711, HEXTORAW('0102030405060708090A0B0C0D0E0F10'))",
        "INSERT INTO NAV_POS VALUES (2, 1, 4711, HEXTORAW('0102030405060708090A0B0C0D0E0F10'))",
        "INSERT INTO NAV_POS VALUES (3, 1, 4711, NULL)",
        "INSERT INTO NAV_POS VALUES (4, 2, 4711, HEXTORAW('A1A2A3A4A5A6A7A8A9AAABACADAEAFB0'))",
        "COMMIT",
        // K = -1 cannot be pushed into either side of the cross join: counting has to build the whole product.
        "CREATE VIEW NAV_SLOW AS SELECT a.OBJECT_ID + b.OBJECT_ID AS K FROM ALL_OBJECTS a CROSS JOIN ALL_OBJECTS b",
    ];

    private OracleSession? _session;
    private SchemaCache _schema = null!;
    private OracleDataAccess _data = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Navigation tests"), Ct);
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

    private async Task<(TableDetails Details, RowData Row)> RowAsync(string table, FilterCondition filter)
    {
        var details = await DetailsAsync(table);
        var page = await _data.ReadPageAsync(details, [filter], [], new PageSpec(0, 10), Ct);
        return (details, Assert.Single(page.Rows));
    }

    private IReadOnlyList<FkJump> Jumps(TableDetails details, RowData row) =>
        FkNavigation.JumpsFor(details, row, _schema.OutgoingOf(details.Table.Ref), _schema.IncomingOf(details.Table.Ref));

    private async Task<IReadOnlyList<RowData>> FollowAsync(FkJump jump)
    {
        var target = await DetailsAsync(jump.Table.Name);
        return (await _data.ReadPageAsync(target, jump.Filters, [], new PageSpec(0, 100), Ct)).Rows;
    }

    [Fact]
    public async Task Outgoing_jumps_over_composite_and_raw_keys_find_the_referenced_row()
    {
        var (details, row) = await RowAsync("NAV_POS", FilterCondition.Of("ID", FilterOperator.Equals, "4"));

        var jumps = Jumps(details, row).Where(j => j.Direction == JumpDirection.Outgoing).ToDictionary(j => j.ForeignKey.Name);

        Assert.Equal("MANDANT = 2, NR = 4711", jumps["FK_NAV_POS_KOPF"].Condition);
        Assert.Equal("GUID = A1A2A3A4A5A6A7A8A9AAABACADAEAFB0", jumps["FK_NAV_POS_GUID"].Condition);
        foreach (var jump in jumps.Values)
        {
            var target = Assert.Single(await FollowAsync(jump));
            Assert.Equal(new RowKey.PrimaryKey([2m, 4711m]), target.Key);
        }
    }

    [Fact]
    public async Task Null_fk_has_no_outgoing_jump()
    {
        var (details, row) = await RowAsync("NAV_POS", FilterCondition.Of("ID", FilterOperator.Equals, "3"));

        var guidJump = Jumps(details, row).Single(j => j.ForeignKey.Name == "FK_NAV_POS_GUID");

        Assert.Equal("KOPF_GUID ist NULL.", guidJump.Unavailable);
    }

    [Fact]
    public async Task Incoming_jumps_count_and_find_the_referencing_rows()
    {
        var (details, row) = await RowAsync("NAV_KOPF", FilterCondition.Of("MANDANT", FilterOperator.Equals, "1"));
        var incoming = Jumps(details, row).Where(j => j.Direction == JumpDirection.Incoming).ToDictionary(j => j.ForeignKey.Name);
        var positions = await DetailsAsync("NAV_POS");

        var byKey = await FkNavigation.CountAsync(_data, positions, incoming["FK_NAV_POS_KOPF"], FkNavigation.CountTimeout, Ct);
        var byGuid = await FkNavigation.CountAsync(_data, positions, incoming["FK_NAV_POS_GUID"], FkNavigation.CountTimeout, Ct);

        Assert.Equal(3, byKey);
        Assert.Equal(2, byGuid);
        Assert.Equal([1m, 2m, 3m], (await FollowAsync(incoming["FK_NAV_POS_KOPF"])).Select(r => r.Values[0]));
    }

    [Fact]
    public async Task Slow_count_times_out_and_leaves_the_session_usable()
    {
        var slow = await DetailsAsync("NAV_SLOW");
        var fk = new ForeignKeyInfo("FK_SLOW", slow.Table.Ref, ["K"], new TableRef(_schema.Owner, "NAV_KOPF"), ["NR"], FkSource.Declared);
        var jump = new FkJump(fk, JumpDirection.Incoming, slow.Table.Ref, [FilterCondition.Of("K", FilterOperator.Equals, "-1")], null);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var count = await FkNavigation.CountAsync(_data, slow, jump, TimeSpan.FromSeconds(1), Ct);
        var timedOutAfter = stopwatch.Elapsed;

        Assert.Null(count);
        Assert.True(timedOutAfter < TimeSpan.FromSeconds(3), $"Timeout took {timedOutAfter}.");
        Assert.True(await _session!.PingAsync(Ct) > TimeSpan.Zero);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Session blocked until {stopwatch.Elapsed}.");
    }
}
