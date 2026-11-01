using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// WP-71: objects of the app user with grants to a second user, a role and PUBLIC, and a table of another schema with grants
/// the app user takes part in and one it does not – created as SYSTEM once per run. If SYSTEM is not available,
/// <see cref="Available"/> is false and the tests skip.
/// </summary>
public static class GrantSchema
{
    /// <summary>A user the grants go to.</summary>
    public const string Grantee = "FERRET_GRANTEE";

    /// <summary>A user the tests grant to and revoke from (nothing to start with).</summary>
    public const string Scratch = "FERRET_GRANTEE2";

    /// <summary>A role granted to the app user (enabled in its sessions).</summary>
    public const string Role = "FERRET_GRANT_ROLE";

    /// <summary>A role the app user does not have.</summary>
    public const string OtherRole = "FERRET_GRANT_ROLE2";

    /// <summary>Owner of <c>T_SHARED</c> (SELECT with grant option to the app user) and <c>T_PLAIN</c> (SELECT without).</summary>
    public const string OtherOwner = "FERRET_GRANT_OTHER";

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
            var appUser = new OracleConnectionStringBuilder(appConnectionString).UserID.ToUpperInvariant();
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
                $"CREATE USER {Grantee} IDENTIFIED BY \"Grantee_2026\" QUOTA UNLIMITED ON users",
                $"CREATE USER {Scratch} IDENTIFIED BY \"Grantee_2026\"",
                $"CREATE USER {OtherOwner} IDENTIFIED BY \"Other_2026\" QUOTA UNLIMITED ON users",
                $"CREATE ROLE {Role}",
                $"CREATE ROLE {OtherRole}",
                $"GRANT {Role} TO {appUser}",

                $"CREATE TABLE {appUser}.GR_TABLE (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50), NOTE VARCHAR2(50))",
                $"CREATE SEQUENCE {appUser}.GR_SEQ",
                $"CREATE VIEW {appUser}.GR_VIEW AS SELECT ID, NAME FROM {appUser}.GR_TABLE",
                $"CREATE PROCEDURE {appUser}.GR_PROC AS BEGIN NULL; END;",
                $"GRANT SELECT, INSERT ON {appUser}.GR_TABLE TO {Grantee}",
                $"GRANT UPDATE (NAME, NOTE) ON {appUser}.GR_TABLE TO {Grantee} WITH GRANT OPTION",
                $"GRANT SELECT ON {appUser}.GR_TABLE TO PUBLIC",
                $"GRANT SELECT ON {appUser}.GR_VIEW TO {OtherRole}",
                $"GRANT SELECT ON {appUser}.GR_SEQ TO {Grantee}",
                // a foreign key of the grantee through REFERENCES (ORA-01981 on revoking it without CASCADE CONSTRAINTS)
                $"GRANT REFERENCES (ID) ON {appUser}.GR_TABLE TO {Grantee}",
                $"CREATE TABLE {Grantee}.GR_CHILD (ID NUMBER, TABLE_ID NUMBER CONSTRAINT FK_GR_CHILD REFERENCES {appUser}.GR_TABLE (ID))",

                $"CREATE TABLE {OtherOwner}.T_SHARED (ID NUMBER PRIMARY KEY, NAME VARCHAR2(50))",
                $"CREATE TABLE {OtherOwner}.T_PLAIN (ID NUMBER PRIMARY KEY)",
                $"GRANT SELECT ON {OtherOwner}.T_SHARED TO {appUser} WITH GRANT OPTION",
                $"GRANT SELECT ON {OtherOwner}.T_PLAIN TO {appUser}",
                $"GRANT INSERT ON {OtherOwner}.T_SHARED TO {Role}",
                $"GRANT SELECT ON {OtherOwner}.T_SHARED TO {Grantee}",          // the app user takes no part: hidden
                $"GRANT UPDATE (NAME) ON {OtherOwner}.T_SHARED TO {Grantee}",   // hidden as well
                $"GRANT DELETE ON {OtherOwner}.T_SHARED TO {OtherRole}",        // a role the app user does not have: hidden

                // a view over a table the app user may not pass on (ORA-01720 on granting it)
                $"CREATE VIEW {appUser}.GR_VIEW_PLAIN AS SELECT ID FROM {OtherOwner}.T_PLAIN",
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
