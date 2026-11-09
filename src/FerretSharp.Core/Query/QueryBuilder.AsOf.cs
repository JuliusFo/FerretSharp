using System.Globalization;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

// Reading a table as it was at an SCN (WP-78, ADR 0048): the time travel tab and restoring rows read through the same
// statements as the grid, with the table named AS OF SCN. Only the table of the statement: filters name no other table.
public static partial class QueryBuilder
{
    /// <summary>The bind of the SCN a statement reads its table at.</summary>
    internal const string AsOfBind = "p_asof";

    /// <summary>
    /// The SCN of a time <paramref name="back"/> before now on the database's clock (WP-78), with the database the SCN belongs
    /// to (<see cref="Data.DatabasePoint"/>). The time is an interval back from <c>SYSTIMESTAMP</c>, not a time bound with a zone:
    /// the server's time zone never matters (decision of the user, 2026-11-09). Too far back for Oracle's mapping: ORA-08180.
    /// </summary>
    public const string ScnBackSql =
        "SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP - NUMTODSINTERVAL(:p_back, 'SECOND')), " + DatabaseSelect + " FROM dual";

    /// <summary>
    /// The current SCN exactly (WP-78): needs SELECT on <c>V$DATABASE</c> (<c>SELECT_CATALOG_ROLE</c> or a grant), otherwise
    /// ORA-00942 and <see cref="CurrentScnSql"/>. No Diagnostics or Tuning Pack (ADR 0045).
    /// </summary>
    public const string ExactScnSql = "SELECT current_scn, " + DatabaseSelect + " FROM v$database";

    /// <summary>
    /// Which database an SCN belongs to (WP-78): its unique name, the container and the container's DBID – no rights needed
    /// (checked against Oracle 23: <c>CON_DBID</c> without any grant). A start point of another database (a profile switched to
    /// another one, a PDB of the same name elsewhere) means nothing there.
    /// </summary>
    private const string DatabaseSelect =
        "SYS_CONTEXT('USERENV', 'DB_UNIQUE_NAME') || '/' || SYS_CONTEXT('USERENV', 'CON_NAME') || '/' || SYS_CONTEXT('USERENV', 'CON_DBID')";

    /// <summary>
    /// How far back the undo reaches about (WP-78): <c>TUNED_UNDORETENTION</c> of the last interval, in seconds. A hint for
    /// choosing a time, no promise – long queries and a full undo tablespace shorten it. No pack needed (ADR 0045); without
    /// SELECT on <c>V$UNDOSTAT</c> ORA-00942, the hint is left out.
    /// </summary>
    public const string UndoReachSql = "SELECT MAX(tuned_undoretention) KEEP (DENSE_RANK LAST ORDER BY end_time) FROM v$undostat";

    /// <summary><c>"OWNER"."TABLE" AS OF SCN :p_asof t</c> – or without AS OF when <paramref name="asOf"/> is null.</summary>
    private static string From(TableDetails table, long? asOf, List<QueryParameter> parameters, string? alias = Alias)
    {
        var name = OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name);
        var at = "";
        if (asOf is { } scn)
        {
            parameters.Add(new QueryParameter(AsOfBind, scn, OracleTypeHint.Number));
            at = " AS OF SCN :" + AsOfBind;
        }

        return alias is null ? name + at : $"{name}{at} {alias}";
    }

    /// <summary>The SCN as text for a statement shown to the user (the filter as SQL, WP-78): a number, never a value of a row.</summary>
    private static string AsOfText(long? asOf) => asOf is { } scn ? " AS OF SCN " + scn.ToString(CultureInfo.InvariantCulture) : "";
}
