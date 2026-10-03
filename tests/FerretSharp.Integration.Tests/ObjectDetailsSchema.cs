using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Objects for the detail views (constraints, indexes, dependencies, comments, an invalid view), created once per
/// test run next to <see cref="SampleSchema"/>.
/// </summary>
public static class ObjectDetailsSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static readonly string[] Statements =
    [
        """
        CREATE TABLE OD_KUNDE (
            ID         NUMBER(10) CONSTRAINT PK_OD_KUNDE PRIMARY KEY,
            NAME       VARCHAR2(50) NOT NULL,
            LAND       VARCHAR2(2) DEFAULT ON NULL 'DE',
            MENGE      NUMBER CONSTRAINT CK_OD_MENGE CHECK (MENGE >= 0),
            RABATT     NUMBER CONSTRAINT CK_OD_RABATT CHECK (RABATT <= 50) DISABLE,
            NAME_GROSS GENERATED ALWAYS AS (UPPER(NAME)) VIRTUAL)
        """,
        "COMMENT ON TABLE OD_KUNDE IS 'Kunden für Detailtests'",
        "COMMENT ON COLUMN OD_KUNDE.NAME IS 'Anzeigename'",
        "CREATE INDEX IX_OD_KUNDE_NAME ON OD_KUNDE (UPPER(NAME))",
        "CREATE INDEX IX_OD_KUNDE_LAND ON OD_KUNDE (LAND DESC, MENGE)",
        """
        CREATE TABLE OD_AUFTRAG (
            ID            NUMBER PRIMARY KEY,
            KUNDE_ID      NUMBER(10) CONSTRAINT FK_OD_AUFTRAG_KUNDE REFERENCES OD_KUNDE ON DELETE CASCADE,
            VERKAEUFER_ID NUMBER(10) CONSTRAINT FK_OD_AUFTRAG_VERK REFERENCES OD_KUNDE,
            NOTIZ         CLOB)
        """,
        "CREATE INDEX IX_OD_AUFTRAG_KUNDE ON OD_AUFTRAG (KUNDE_ID)",
        "CREATE VIEW OD_V_KUNDE AS SELECT ID, NAME FROM OD_KUNDE",
        "CREATE TABLE OD_TEMP (ID NUMBER, X NUMBER)",
        "CREATE VIEW OD_V_KAPUTT AS SELECT ID, X FROM OD_TEMP",
        "ALTER TABLE OD_TEMP DROP COLUMN X", // the view is INVALID from now on
    ];

    public static async Task EnsureCreatedAsync(string connectionString, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_created)
            {
                return;
            }

            await using var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            foreach (var sql in Statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
