using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// A second schema (FERRET_OTHER) reached through synonyms, created as SYSTEM once per run. If SYSTEM is not
/// available (other image/credentials), <see cref="Available"/> is false and the tests skip.
/// </summary>
public static class SynonymSchema
{
    public const string OtherOwner = "FERRET_OTHER";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _done;

    public static bool Available { get; private set; }

    public static string? UnavailableReason { get; private set; }

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
            var appUser = app.UserID.ToUpperInvariant();
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

            string[] statements =
            [
                $"CREATE USER {OtherOwner} IDENTIFIED BY \"Other_2026\" QUOTA UNLIMITED ON users",
                $"CREATE TABLE {OtherOwner}.KATEGORIE (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50))",
                $"CREATE TABLE {OtherOwner}.PRODUKT (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50), KATEGORIE_ID NUMBER CONSTRAINT FK_PRODUKT_KATEGORIE REFERENCES {OtherOwner}.KATEGORIE)",
                $"CREATE VIEW {OtherOwner}.V_PRODUKT AS SELECT p.ID, p.NAME, k.NAME AS KATEGORIE FROM {OtherOwner}.PRODUKT p JOIN {OtherOwner}.KATEGORIE k ON k.ID = p.KATEGORIE_ID",
                $"CREATE TABLE {OtherOwner}.GEHEIM (ID NUMBER)",
                $"INSERT INTO {OtherOwner}.KATEGORIE VALUES (1, 'Werkzeug')",
                $"INSERT INTO {OtherOwner}.PRODUKT VALUES (10, 'Hammer', 1)",
                "COMMIT",
                $"GRANT SELECT ON {OtherOwner}.KATEGORIE TO {appUser}",
                $"GRANT SELECT ON {OtherOwner}.PRODUKT TO {appUser}",
                $"GRANT SELECT ON {OtherOwner}.V_PRODUKT TO {appUser}",
                // private synonym of the app user
                $"CREATE SYNONYM {appUser}.S_PRODUKT FOR {OtherOwner}.PRODUKT",
                // public synonym to another schema
                $"CREATE PUBLIC SYNONYM FERRET_KATEGORIE FOR {OtherOwner}.KATEGORIE",
                // private and public synonym to the same view → one entry, the private one
                $"CREATE SYNONYM {appUser}.S_V_PRODUKT FOR {OtherOwner}.V_PRODUKT",
                $"CREATE PUBLIC SYNONYM FERRET_V_PRODUKT FOR {OtherOwner}.V_PRODUKT",
                // public synonym to the app user's own table → no duplicate
                $"CREATE PUBLIC SYNONYM FERRET_KUNDEN FOR {appUser}.KUNDEN",
                // no SELECT grant → invisible
                $"CREATE PUBLIC SYNONYM FERRET_GEHEIM FOR {OtherOwner}.GEHEIM",
            ];

            foreach (var sql in statements)
            {
                await using var command = connection.CreateCommand();
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
}
