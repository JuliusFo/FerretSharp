using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Schemas of their own for the schema snapshot (WP-20), created as SYSTEM once per run: <see cref="Owner"/> with varied
/// objects, <see cref="RefOwner"/> with a table it references, and <see cref="EmptyOwner"/> without objects. If SYSTEM is
/// not available, <see cref="Available"/> is false and the tests skip.
/// </summary>
public static class SnapshotSchema
{
    public const string Owner = "SNAP_MAIN";
    public const string RefOwner = "SNAP_REF";
    public const string EmptyOwner = "SNAP_EMPTY";
    public const string Password = "Snap_2026";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _done;

    public static bool Available { get; private set; }

    public static string? UnavailableReason { get; private set; }

    /// <summary>The image may come without the partitioning option; then the partitioned table is missing.</summary>
    public static bool HasPartitionedTable { get; private set; }

    /// <summary>Connection string of <see cref="Owner"/>.</summary>
    public static string ConnectionString(string appConnectionString) =>
        new OracleConnectionStringBuilder(appConnectionString) { UserID = Owner, Password = Password }.ConnectionString;

    // As SYSTEM: the users, the referenced table in another schema and the grant to reference it.
    private static readonly string[] SystemStatements =
    [
        $"CREATE USER {Owner} IDENTIFIED BY \"{Password}\" QUOTA UNLIMITED ON users",
        $"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE MATERIALIZED VIEW TO {Owner}",
        $"CREATE USER {RefOwner} IDENTIFIED BY \"{Password}\" QUOTA UNLIMITED ON users",
        $"CREATE TABLE {RefOwner}.KATEGORIE (ID NUMBER CONSTRAINT PK_KATEGORIE PRIMARY KEY, NAME VARCHAR2(50))",
        $"GRANT REFERENCES ON {RefOwner}.KATEGORIE TO {Owner}",
        $"CREATE USER {EmptyOwner} IDENTIFIED BY \"{Password}\"",
    ];

    // As the owner.
    private static readonly string[] OwnerStatements =
    [
        // quoted mixed-case names, system-named unique and check, named check, DEFAULT ON NULL, virtual column
        """
        CREATE TABLE "Kunde" (
            "Id"       NUMBER(10) CONSTRAINT "PK_Kunde" PRIMARY KEY,
            NAME       VARCHAR2(50 CHAR) NOT NULL,
            "eMail"    VARCHAR2(200) UNIQUE,
            LAND       CHAR(2) DEFAULT ON NULL 'DE',
            STATUS     VARCHAR2(1) DEFAULT 'A' CHECK (STATUS IN ('A', 'I')),
            RABATT     NUMBER(5,2) CONSTRAINT CK_KUNDE_RABATT CHECK (RABATT BETWEEN 0 AND 50) DISABLE,
            NAME_GROSS GENERATED ALWAYS AS (UPPER(NAME)) VIRTUAL)
        """,
        "COMMENT ON COLUMN \"Kunde\".\"eMail\" IS 'Adresse für Rechnungen'",
        // identity, composite PK, FK with ON DELETE CASCADE, FK to another schema, deferrable unique, CLOB (LOB index)
        $"""
        CREATE TABLE AUFTRAG (
            AUFTRAG_ID   NUMBER GENERATED ALWAYS AS IDENTITY,
            POS_NR       NUMBER(3),
            KUNDE_ID     NUMBER(10) CONSTRAINT FK_AUFTRAG_KUNDE REFERENCES "Kunde" ON DELETE CASCADE,
            KATEGORIE_ID NUMBER CONSTRAINT FK_AUFTRAG_KATEGORIE REFERENCES {RefOwner}.KATEGORIE ON DELETE SET NULL,
            MENGE        NUMBER(12,2) CONSTRAINT CK_AUFTRAG_MENGE CHECK (MENGE > 0),
            NOTIZ        CLOB,
            CONSTRAINT PK_AUFTRAG PRIMARY KEY (AUFTRAG_ID, POS_NR),
            CONSTRAINT UQ_AUFTRAG_KUNDE_POS UNIQUE (KUNDE_ID, POS_NR) DEFERRABLE INITIALLY DEFERRED)
        """,
        "CREATE INDEX IX_AUFTRAG_KUNDE ON AUFTRAG (KUNDE_ID DESC, AUFTRAG_ID)",
        "CREATE INDEX IX_AUFTRAG_MENGE ON AUFTRAG (ROUND(MENGE), POS_NR)",
        "CREATE INDEX \"ix_Kunde_Name\" ON \"Kunde\" (UPPER(NAME))",
        // IOT with overflow segment (SYS_IOT_OVER_… must not appear)
        "CREATE TABLE LAND_IOT (CODE CHAR(2) PRIMARY KEY, NAME VARCHAR2(50), BESCHREIBUNG VARCHAR2(4000)) ORGANIZATION INDEX INCLUDING NAME OVERFLOW",
        "CREATE GLOBAL TEMPORARY TABLE TMP_IMPORT (ID NUMBER NOT NULL, WERT VARCHAR2(10)) ON COMMIT PRESERVE ROWS",
        "CREATE VIEW V_KUNDE AS SELECT \"Id\", NAME FROM \"Kunde\" WITH READ ONLY",
        "CREATE MATERIALIZED VIEW MV_MENGE AS SELECT KUNDE_ID, SUM(MENGE) AS MENGE FROM AUFTRAG GROUP BY KUNDE_ID",
        // recycle bin: BIN$… with columns, constraints and indexes
        "CREATE TABLE WEG (ID NUMBER CONSTRAINT PK_WEG PRIMARY KEY, X NUMBER CHECK (X > 0))",
        "DROP TABLE WEG",
    ];

    private const string PartitionedTable = """
        CREATE TABLE PROTOKOLL (ID NUMBER PRIMARY KEY, AM DATE NOT NULL)
        PARTITION BY RANGE (AM) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) (PARTITION P0 VALUES LESS THAN (DATE '2026-01-01'))
        """;

    public static async Task EnsureCreatedAsync(string appConnectionString, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_done)
            {
                return;
            }

            _done = true;
            var system = new OracleConnectionStringBuilder(appConnectionString) { UserID = "system" };
            await using (var connection = new OracleConnection(system.ConnectionString))
            {
                try
                {
                    await connection.OpenAsync(cancellationToken);
                }
                catch (OracleException ex)
                {
                    UnavailableReason = $"SYSTEM login not possible: {ex.Message}";
                    return;
                }

                await ExecuteAsync(connection, SystemStatements, cancellationToken);
            }

            await using (var connection = new OracleConnection(ConnectionString(appConnectionString)))
            {
                await connection.OpenAsync(cancellationToken);
                await ExecuteAsync(connection, OwnerStatements, cancellationToken);
                try
                {
                    await ExecuteAsync(connection, [PartitionedTable], cancellationToken);
                    HasPartitionedTable = true;
                }
                catch (InvalidOperationException ex) when (ex.InnerException is OracleException)
                {
                    // ORA-00439: feature not enabled: Partitioning
                }
            }

            Available = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ExecuteAsync(OracleConnection connection, IEnumerable<string> statements, CancellationToken cancellationToken)
    {
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (OracleException ex)
            {
                throw new InvalidOperationException($"{ex.Message} – {sql}", ex);
            }
        }
    }
}
