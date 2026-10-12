using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The columns a trigger references (ALL_TRIGGER_COLS, WP-26): an audit trigger is outdated when the table has columns
/// it does not reference. Each test builds its own table, because some change it afterwards. Also pins what Oracle
/// itself keeps in the view for invalid triggers (<see cref="DictionaryColumnsAsync"/>), the reason the reader only
/// answers for valid ones.
/// </summary>
public sealed class TriggerColumnsTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private OracleSession? _session;
    private string _owner = "";
    private string _connectionString = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OracleSchemaReader Reader => new(_session!);

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        _connectionString = oracle.RequireConnectionString();
        _owner = profile.EffectiveSchema;
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Trigger column tests"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_row_trigger_lists_the_columns_it_reads_through_new_and_old()
    {
        await ExecuteAsync(
            """CREATE TABLE TC_KUNDE (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50), BETRAG NUMBER, "Notiz" VARCHAR2(100), UNBENUTZT DATE)""",
            "CREATE TABLE TC_KUNDE_LOG (ID NUMBER, NAME VARCHAR2(50), BETRAG NUMBER, NOTIZ VARCHAR2(100))",
            """
            CREATE OR REPLACE TRIGGER TC_KUNDE_AUDIT AFTER INSERT OR UPDATE OR DELETE ON TC_KUNDE FOR EACH ROW
            BEGIN
              INSERT INTO TC_KUNDE_LOG (ID, NAME, BETRAG, NOTIZ) VALUES (NVL(:NEW.ID, :OLD.ID), :NEW.NAME, :OLD.BETRAG, :NEW."Notiz");
            END;
            """);

        Assert.Equal("VALID", await StatusAsync("TC_KUNDE_AUDIT"));
        Assert.Equal(["BETRAG", "ID", "NAME", "Notiz"], await Reader.GetTriggerColumnsAsync(_owner, "TC_KUNDE_AUDIT", Ct));
    }

    [Fact]
    public async Task Update_of_counts_as_a_reference_the_when_clause_does_not()
    {
        await ExecuteAsync(
            "CREATE TABLE TC_ARTIKEL (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50), PREIS NUMBER, GEAENDERT DATE, REST VARCHAR2(10))",
            """
            CREATE OR REPLACE TRIGGER TC_ARTIKEL_BU BEFORE UPDATE OF NAME ON TC_ARTIKEL FOR EACH ROW
            WHEN (NEW.PREIS > 0 AND OLD.REST IS NULL)
            BEGIN
              :NEW.GEAENDERT := SYSDATE;
            END;
            """);

        Assert.Equal("VALID", await StatusAsync("TC_ARTIKEL_BU"));
        Assert.Equal(["GEAENDERT", "NAME"], await Reader.GetTriggerColumnsAsync(_owner, "TC_ARTIKEL_BU", Ct));
    }

    [Fact]
    public async Task A_trigger_without_column_references_and_an_unknown_trigger_have_none()
    {
        await ExecuteAsync(
            "CREATE TABLE TC_LEER (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50))",
            "CREATE OR REPLACE TRIGGER TC_LEER_AD AFTER DELETE ON TC_LEER BEGIN NULL; END;",
            "CREATE OR REPLACE TRIGGER TC_LEER_ROW BEFORE INSERT ON TC_LEER FOR EACH ROW BEGIN NULL; END;");

        Assert.Empty(await Reader.GetTriggerColumnsAsync(_owner, "TC_LEER_AD", Ct));
        Assert.Empty(await Reader.GetTriggerColumnsAsync(_owner, "TC_LEER_ROW", Ct));
        Assert.Empty(await Reader.GetTriggerColumnsAsync(_owner, "TC_GIBT_ES_NICHT", Ct));
        Assert.Empty(await Reader.GetTriggerColumnsAsync("NO_SUCH_OWNER", "TC_LEER_AD", Ct));
    }

    [Fact]
    public async Task A_trigger_that_does_not_compile_is_created_without_error_and_lists_no_columns()
    {
        // Unlike a package body (ORA-24344), ODP.NET reports nothing for a trigger with compile errors.
        await ExecuteAsync(
            "CREATE TABLE TC_KAPUTT (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50), BETRAG NUMBER, REST VARCHAR2(10))",
            """
            CREATE OR REPLACE TRIGGER TC_KAPUTT_BIU BEFORE INSERT OR UPDATE OF BETRAG ON TC_KAPUTT FOR EACH ROW
            BEGIN
              :NEW.NAME := X_UNBEKANNT(:OLD.ID);
            END;
            """);

        Assert.Equal("INVALID", await StatusAsync("TC_KAPUTT_BIU"));
        Assert.Empty(await Reader.GetTriggerColumnsAsync(_owner, "TC_KAPUTT_BIU", Ct));
        // The dictionary keeps what the compiler resolved before the error, without usage.
        Assert.Equal([("BETRAG", "NONE"), ("ID", "NONE"), ("NAME", "NONE")], await DictionaryColumnsAsync("TC_KAPUTT_BIU"));
    }

    [Fact]
    public async Task A_column_added_to_the_table_later_leaves_the_trigger_valid_and_unreferenced()
    {
        await ExecuteAsync(
            "CREATE TABLE TC_WACHSEN (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50))",
            """
            CREATE OR REPLACE TRIGGER TC_WACHSEN_BIU BEFORE INSERT OR UPDATE ON TC_WACHSEN FOR EACH ROW
            BEGIN
              :NEW.NAME := UPPER(:NEW.NAME) || :OLD.ID;
            END;
            """,
            "ALTER TABLE TC_WACHSEN ADD (NEU VARCHAR2(10))");

        Assert.Equal("VALID", await StatusAsync("TC_WACHSEN_BIU"));
        Assert.Equal(["ID", "NAME"], await Reader.GetTriggerColumnsAsync(_owner, "TC_WACHSEN_BIU", Ct));
    }

    [Fact]
    public async Task Dropping_any_column_invalidates_the_trigger_and_leaves_stale_dictionary_entries()
    {
        await ExecuteAsync(
            "CREATE TABLE TC_SCHRUMPFEN (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50), FREI VARCHAR2(10), BETRAG NUMBER, SPAETER DATE)",
            """
            CREATE OR REPLACE TRIGGER TC_SCHRUMPFEN_BIU BEFORE INSERT OR UPDATE ON TC_SCHRUMPFEN FOR EACH ROW
            BEGIN
              :NEW.NAME := :OLD.ID || :NEW.BETRAG;
            END;
            """);
        Assert.Equal(["BETRAG", "ID", "NAME"], await Reader.GetTriggerColumnsAsync(_owner, "TC_SCHRUMPFEN_BIU", Ct));

        // A column the trigger does not reference: the entries keep their column positions, BETRAG's is now SPAETER's.
        await ExecuteAsync("ALTER TABLE TC_SCHRUMPFEN DROP COLUMN FREI");
        Assert.Equal("INVALID", await StatusAsync("TC_SCHRUMPFEN_BIU"));
        Assert.Equal([("ID", "OLD IN"), ("NAME", "NEW OUT"), ("SPAETER", "NEW IN")], await DictionaryColumnsAsync("TC_SCHRUMPFEN_BIU"));
        Assert.Empty(await Reader.GetTriggerColumnsAsync(_owner, "TC_SCHRUMPFEN_BIU", Ct));

        // The next DML compiles it again, and the entries are right again.
        await ExecuteAsync("INSERT INTO TC_SCHRUMPFEN (ID, NAME, BETRAG) VALUES (1, 'x', 2)");
        Assert.Equal("VALID", await StatusAsync("TC_SCHRUMPFEN_BIU"));
        Assert.Equal(["BETRAG", "ID", "NAME"], await Reader.GetTriggerColumnsAsync(_owner, "TC_SCHRUMPFEN_BIU", Ct));

        // A column it references: it cannot compile again.
        await ExecuteAsync("ALTER TABLE TC_SCHRUMPFEN DROP COLUMN NAME");
        Assert.Equal("INVALID", await StatusAsync("TC_SCHRUMPFEN_BIU"));
        Assert.Equal([("BETRAG", "NEW OUT"), ("ID", "OLD IN"), ("SPAETER", "NEW IN")], await DictionaryColumnsAsync("TC_SCHRUMPFEN_BIU"));
        Assert.Empty(await Reader.GetTriggerColumnsAsync(_owner, "TC_SCHRUMPFEN_BIU", Ct));
    }

    private async Task<string> StatusAsync(string trigger) =>
        Assert.Single(await Reader.GetPlSqlObjectsAsync(_owner, Ct), o => o.Name == trigger && o.Kind == PlSqlKind.Trigger).Status;

    /// <summary>What ALL_TRIGGER_COLS itself holds for the trigger, whatever its status: column and usage, by name.</summary>
    private async Task<List<(string Column, string Usage)>> DictionaryColumnsAsync(string trigger)
    {
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = """
            SELECT column_name, column_usage FROM all_trigger_cols
             WHERE trigger_owner = :owner AND trigger_name = :name
             ORDER BY NLSSORT(column_name, 'NLS_SORT=BINARY')
            """;
        command.Parameters.Add("owner", _owner);
        command.Parameters.Add("name", trigger);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new List<(string, string)>();
        while (await reader.ReadAsync(Ct))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private async Task ExecuteAsync(params string[] statements)
    {
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync(Ct);
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
