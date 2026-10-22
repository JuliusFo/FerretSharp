using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Source and target of a copy (WP-42): the CP_ tables in the app user's schema hold the data, the same tables in a schema
/// of their own (FERRET_COPY, created as SYSTEM once per run) are the target – a second connection in the app. If SYSTEM is
/// not available, <see cref="Available"/> is false and the tests skip.
/// </summary>
public static class CopySchema
{
    public const string TargetOwner = "FERRET_COPY";
    public const string TargetPassword = "Copy_2026";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _done;

    public static bool Available { get; private set; }

    public static string? UnavailableReason { get; private set; }

    /// <summary>The tables, in both schemas; <c>{0}</c> is the schema.</summary>
    private static readonly string[] Tables =
    [
        "CREATE TABLE {0}.CP_KUNDE (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50) NOT NULL, NOTIZ CLOB, BILD BLOB)",
        "CREATE TABLE {0}.CP_MITARBEITER (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50), VORGESETZTER_ID NUMBER(10) CONSTRAINT FK_CP_MA_CHEF REFERENCES {0}.CP_MITARBEITER)",
        """
        CREATE TABLE {0}.CP_AUFTRAG (
            ID NUMBER(10) PRIMARY KEY,
            KUNDE_ID NUMBER(10) NOT NULL CONSTRAINT FK_CP_AUFTRAG_KUNDE REFERENCES {0}.CP_KUNDE,
            BEARBEITER_ID NUMBER(10) CONSTRAINT FK_CP_AUFTRAG_MA REFERENCES {0}.CP_MITARBEITER,
            DATUM DATE, BETRAG NUMBER(12,2) CONSTRAINT CK_CP_BETRAG CHECK (BETRAG < 100000), KZ CHAR(3), TS TIMESTAMP(6))
        """,
        """
        CREATE TABLE {0}.CP_POSITION (
            AUFTRAG_ID NUMBER(10) CONSTRAINT FK_CP_POS_AUFTRAG REFERENCES {0}.CP_AUFTRAG, POS_NR NUMBER(4), ARTIKEL NVARCHAR2(20),
            CONSTRAINT PK_CP_POSITION PRIMARY KEY (AUFTRAG_ID, POS_NR))
        """,
        """
        CREATE TABLE {0}.CP_LIEFERUNG (
            ID NUMBER(10) PRIMARY KEY, AUFTRAG_ID NUMBER(10), POS_NR NUMBER(4),
            CONSTRAINT FK_CP_LIEF_POS FOREIGN KEY (AUFTRAG_ID, POS_NR) REFERENCES {0}.CP_POSITION)
        """,
    ];

    /// <summary>Data of the source (the app user's schema).</summary>
    private static readonly string[] SourceRows =
    [
        "INSERT INTO CP_KUNDE VALUES (1, 'Peters Pizza', TO_CLOB(RPAD('lang', 4000, '.')) || RPAD('x', 4000, 'x'), HEXTORAW('CAFE01'))",
        "INSERT INTO CP_KUNDE VALUES (2, 'Anna Apfel', NULL, NULL)",
        "INSERT INTO CP_MITARBEITER VALUES (10, 'Chefin', NULL)",
        "INSERT INTO CP_MITARBEITER VALUES (11, 'Abteilungsleiter', 10)",
        "INSERT INTO CP_MITARBEITER VALUES (12, 'Sachbearbeiter', 11)",
        "INSERT INTO CP_AUFTRAG VALUES (100, 1, 12, DATE '2026-10-01', 1234.5, 'AB', TIMESTAMP '2026-10-01 12:34:56.123456')",
        "INSERT INTO CP_AUFTRAG VALUES (101, 1, NULL, NULL, NULL, NULL, NULL)",
        "INSERT INTO CP_AUFTRAG VALUES (102, 2, 10, NULL, 5, NULL, NULL)",
        "INSERT INTO CP_POSITION VALUES (100, 1, N'Käse – €')",
        "INSERT INTO CP_POSITION VALUES (100, 2, N'Brot')",
        "INSERT INTO CP_POSITION VALUES (101, 1, NULL)",
        "INSERT INTO CP_LIEFERUNG VALUES (1000, 100, 2)",
        "INSERT INTO CP_LIEFERUNG VALUES (1001, 101, 1)",
        "COMMIT",
    ];

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
            var app = new OracleConnectionStringBuilder(appConnectionString);
            var system = new OracleConnectionStringBuilder(appConnectionString) { UserID = "system" };
            await using var connection = new OracleConnection(system.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
            }
            catch (OracleException ex)
            {
                UnavailableReason = $"SYSTEM login not possible: {ex.Message}";
                return;
            }

            List<string> statements =
            [
                $"CREATE USER {TargetOwner} IDENTIFIED BY \"{TargetPassword}\" QUOTA UNLIMITED ON users",
                $"GRANT CREATE SESSION TO {TargetOwner}",
                .. Tables.Select(t => string.Format(System.Globalization.CultureInfo.InvariantCulture, t, app.UserID.ToUpperInvariant())),
                .. Tables.Select(t => string.Format(System.Globalization.CultureInfo.InvariantCulture, t, TargetOwner)),
                // In the target: a customer that exists already, an audit trigger, an identity GENERATED ALWAYS.
                $"INSERT INTO {TargetOwner}.CP_KUNDE (ID, NAME) VALUES (1, 'Peters Pizza (Test)')",
                $"CREATE TABLE {TargetOwner}.CP_AUFTRAG_LOG (AUFTRAG_ID NUMBER(10))",
                $"""
                 CREATE TRIGGER {TargetOwner}.TRG_CP_AUFTRAG_LOG AFTER INSERT ON {TargetOwner}.CP_AUFTRAG FOR EACH ROW
                 BEGIN INSERT INTO {TargetOwner}.CP_AUFTRAG_LOG VALUES (:NEW.ID); END;
                 """,
                $"CREATE TABLE {TargetOwner}.CP_IDENTITY (ID NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, NAME VARCHAR2(10))",
                $"CREATE TABLE {TargetOwner}.CP_IDENTITY_DEF (LFD NUMBER GENERATED BY DEFAULT AS IDENTITY, NAME VARCHAR2(10))",
                "COMMIT",
            ];
            foreach (var sql in statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var appConnection = new OracleConnection(appConnectionString);
            await appConnection.OpenAsync(cancellationToken);
            foreach (var sql in SourceRows)
            {
                await using var command = appConnection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            Available = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The connection string of the target schema's own user.</summary>
    public static string TargetConnectionString(string appConnectionString) =>
        new OracleConnectionStringBuilder(appConnectionString) { UserID = TargetOwner, Password = TargetPassword }.ConnectionString;
}
