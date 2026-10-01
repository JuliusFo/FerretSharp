using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Creates a small schema with the awkward cases once per test run (DDL is fine in tests; the app itself never writes).
/// </summary>
public static class SampleSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    /// <summary>False if the test user lacks CREATE MATERIALIZED VIEW; MView assertions are skipped then.</summary>
    public static bool HasMaterializedView { get; private set; }

    private static readonly string[] Statements =
    [
        """
        CREATE TABLE KUNDEN (
            KUNDE_ID    NUMBER(10) CONSTRAINT PK_KUNDEN PRIMARY KEY,
            NAME        VARCHAR2(100 CHAR) NOT NULL,
            KUERZEL     CHAR(3 BYTE),
            ERSTELLT_AM DATE DEFAULT SYSDATE NOT NULL,
            UMSATZ      NUMBER(12,2),
            ANZAHL      INTEGER,
            CONSTRAINT UK_KUNDEN_KUERZEL UNIQUE (KUERZEL))
        """,
        """
        CREATE TABLE AUFTRAG (
            AUFTRAG_ID NUMBER GENERATED ALWAYS AS IDENTITY CONSTRAINT PK_AUFTRAG PRIMARY KEY,
            KUNDE_ID   NUMBER(10) NOT NULL CONSTRAINT FK_AUFTRAG_KUNDE REFERENCES KUNDEN,
            NOTIZ      CLOB)
        """,
        """
        CREATE TABLE AUFTRAG_POSITION (
            AUFTRAG_ID NUMBER NOT NULL CONSTRAINT FK_POS_AUFTRAG REFERENCES AUFTRAG,
            POS_NR     NUMBER(4) NOT NULL,
            MENGE      NUMBER,
            CONSTRAINT PK_AUFTRAG_POSITION PRIMARY KEY (AUFTRAG_ID, POS_NR))
        """,
        """
        CREATE TABLE LIEFERUNG (
            LIEFERUNG_ID NUMBER PRIMARY KEY,
            POS_NR       NUMBER(4),
            AUFTRAG_ID   NUMBER,
            CONSTRAINT FK_LIEF_POS FOREIGN KEY (AUFTRAG_ID, POS_NR) REFERENCES AUFTRAG_POSITION (AUFTRAG_ID, POS_NR))
        """,
        """
        CREATE TABLE "MixedCase" (
            "Id"      NUMBER CONSTRAINT "Pk_Mixed" PRIMARY KEY,
            "Wert"    NVARCHAR2(20),
            "raw col" RAW(16))
        """,
        "CREATE TABLE LAND_IOT (CODE VARCHAR2(2) PRIMARY KEY, NAME VARCHAR2(50)) ORGANIZATION INDEX OVERFLOW",
        """
        CREATE VIEW V_KUNDEN_AUFTRAEGE AS
        SELECT k.KUNDE_ID, k.NAME, COUNT(a.AUFTRAG_ID) AS ANZAHL
          FROM KUNDEN k LEFT JOIN AUFTRAG a ON a.KUNDE_ID = k.KUNDE_ID
         GROUP BY k.KUNDE_ID, k.NAME
        """,
        "CREATE TABLE WEG_DAMIT (ID NUMBER)",
        "DROP TABLE WEG_DAMIT",
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
                await ExecuteAsync(connection, sql, cancellationToken);
            }

            try
            {
                await ExecuteAsync(connection, "CREATE MATERIALIZED VIEW MV_UMSATZ AS SELECT KUNDE_ID, SUM(UMSATZ) AS SUMME FROM KUNDEN GROUP BY KUNDE_ID", cancellationToken);
                HasMaterializedView = true;
            }
            catch (OracleException ex) when (ex.Number == 1031) // insufficient privileges
            {
                HasMaterializedView = false;
            }

            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
