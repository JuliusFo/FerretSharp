using System.Text;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// PL/SQL units for WP-28, created once per run: a package with overloads and a body, standalone procedure and
/// function, a body that does not compile, triggers, a wrapped procedure and a large package. With SYSTEM (as for
/// <see cref="SynonymSchema"/>) also a package in the other schema reached through a synonym.
/// </summary>
public static class PlSqlSchema
{
    /// <summary>Lines of the large package's specification.</summary>
    public const int LargeLines = 3000;

    /// <summary>Line of the error in the body of PL_KAPUTT (see <see cref="BrokenBody"/>); its column points at X_UNBEKANNT.</summary>
    public const int BrokenLine = 4;

    private const string BrokenBody = """
        CREATE OR REPLACE PACKAGE BODY PL_KAPUTT AS
          PROCEDURE LAUF IS
          BEGIN
            NULL; X_UNBEKANNT := 1;
          END;
        END PL_KAPUTT;
        """;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    public static bool OtherSchemaAvailable { get; private set; }

    private static readonly string[] Statements =
    [
        "CREATE TABLE PL_KUNDE (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50), GEAENDERT DATE)",
        """
        CREATE OR REPLACE PACKAGE PL_RECHNUNG AS
          -- Größe ändern: Umlaute bleiben erhalten
          TYPE T_POSTEN IS RECORD (ID NUMBER, BETRAG NUMBER);
          FUNCTION BERECHNE(P_BETRAG NUMBER) RETURN NUMBER;
          FUNCTION BERECHNE(P_BETRAG NUMBER, P_WAEHRUNG VARCHAR2 DEFAULT 'EUR') RETURN NUMBER;
          PROCEDURE BUCHE(P_ID IN NUMBER, P_ERGEBNIS OUT VARCHAR2, P_ZAEHLER IN OUT PLS_INTEGER);
          PROCEDURE AUFRAEUMEN;
          PROCEDURE LIES(P_KUNDE IN PL_KUNDE%ROWTYPE, P_POSTEN IN T_POSTEN, P_CURSOR OUT SYS_REFCURSOR, P_OK OUT BOOLEAN);
        END PL_RECHNUNG;
        """,
        """
        CREATE OR REPLACE PACKAGE BODY PL_RECHNUNG AS
          FUNCTION BERECHNE(P_BETRAG NUMBER) RETURN NUMBER IS BEGIN RETURN P_BETRAG * 1.19; END;
          FUNCTION BERECHNE(P_BETRAG NUMBER, P_WAEHRUNG VARCHAR2 DEFAULT 'EUR') RETURN NUMBER IS BEGIN RETURN P_BETRAG; END;
          PROCEDURE BUCHE(P_ID IN NUMBER, P_ERGEBNIS OUT VARCHAR2, P_ZAEHLER IN OUT PLS_INTEGER) IS
          BEGIN
            P_ERGEBNIS := 'OK'; P_ZAEHLER := P_ZAEHLER + 1;
          END;
          PROCEDURE AUFRAEUMEN IS BEGIN DELETE FROM PL_KUNDE WHERE NAME IS NULL; END;
          PROCEDURE LIES(P_KUNDE IN PL_KUNDE%ROWTYPE, P_POSTEN IN T_POSTEN, P_CURSOR OUT SYS_REFCURSOR, P_OK OUT BOOLEAN) IS
          BEGIN
            OPEN P_CURSOR FOR SELECT * FROM PL_KUNDE; P_OK := TRUE;
          END;
        END PL_RECHNUNG;
        """,
        "CREATE OR REPLACE PACKAGE PL_KAPUTT AS PROCEDURE LAUF; END PL_KAPUTT;",
        BrokenBody,
        "CREATE OR REPLACE FUNCTION PL_HEUTE RETURN DATE IS BEGIN RETURN TRUNC(SYSDATE); END;",
        "CREATE OR REPLACE PROCEDURE PL_LEER IS BEGIN PL_RECHNUNG.AUFRAEUMEN; END;",
        """
        CREATE OR REPLACE TRIGGER PL_KUNDE_BIU BEFORE INSERT OR UPDATE ON PL_KUNDE FOR EACH ROW
        WHEN (NEW.NAME IS NOT NULL)
        BEGIN
          :NEW.GEAENDERT := SYSDATE;
        END;
        """,
        "CREATE OR REPLACE TRIGGER PL_KUNDE_AUS AFTER DELETE ON PL_KUNDE BEGIN NULL; END;",
        "ALTER TRIGGER PL_KUNDE_AUS DISABLE",
        "BEGIN DBMS_DDL.CREATE_WRAPPED('CREATE OR REPLACE PROCEDURE PL_GEHEIM IS BEGIN NULL; END;'); END;",
        LargePackage(),
    ];

    private static string LargePackage()
    {
        var text = new StringBuilder("CREATE OR REPLACE PACKAGE PL_GROSS AS\n");
        for (var i = 2; i < LargeLines; i++)
        {
            text.Append("  C").Append(i).Append(" CONSTANT NUMBER := ").Append(i).Append(";\n");
        }

        return text.Append("END PL_GROSS;").ToString();
    }

    public static async Task EnsureCreatedAsync(string connectionString, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_created)
            {
                return;
            }

            await using (var connection = new OracleConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                foreach (var sql in Statements)
                {
                    await ExecuteAsync(connection, sql, cancellationToken);
                }
            }

            await SynonymSchema.EnsureCreatedAsync(connectionString, cancellationToken);
            if (SynonymSchema.Available)
            {
                await CreateOtherSchemaAsync(connectionString, cancellationToken);
                OtherSchemaAvailable = true;
            }

            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A package of FERRET_OTHER with EXECUTE for the app user and a private synonym; one without grant.</summary>
    private static async Task CreateOtherSchemaAsync(string appConnectionString, CancellationToken cancellationToken)
    {
        var appUser = new OracleConnectionStringBuilder(appConnectionString).UserID.ToUpperInvariant();
        var system = new OracleConnectionStringBuilder(appConnectionString) { UserID = "system" };
        const string other = SynonymSchema.OtherOwner;
        string[] statements =
        [
            $"CREATE OR REPLACE PACKAGE {other}.PL_DRUCK AS PROCEDURE DRUCKE(P_TEXT VARCHAR2); END PL_DRUCK;",
            $"CREATE OR REPLACE PACKAGE BODY {other}.PL_DRUCK AS PROCEDURE DRUCKE(P_TEXT VARCHAR2) IS BEGIN NULL; END; END PL_DRUCK;",
            $"CREATE OR REPLACE PROCEDURE {other}.PL_INTERN IS BEGIN NULL; END;",
            $"GRANT EXECUTE ON {other}.PL_DRUCK TO {appUser}",
            $"CREATE OR REPLACE SYNONYM {appUser}.S_DRUCK FOR {other}.PL_DRUCK",
            $"CREATE OR REPLACE PUBLIC SYNONYM FERRET_PL_INTERN FOR {other}.PL_INTERN", // no grant → invisible
        ];

        await using var connection = new OracleConnection(system.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var sql in statements)
        {
            await ExecuteAsync(connection, sql, cancellationToken);
        }
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == 24344 && sql == BrokenBody)
        {
            // "success with compilation error": PL_KAPUTT's body is meant to be invalid.
        }
    }
}
