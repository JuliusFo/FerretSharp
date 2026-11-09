using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// The errors of a data diff (WP-44) the user can act on, explained: no right to read another owner's table as of then, undo
/// that does not reach back to the start, DDL on the table since.
/// </summary>
public static class DataDiffErrors
{
    /// <summary>ORA-01031: insufficient privileges – <c>AS OF</c> on another owner's table needs <c>FLASHBACK</c> on it.</summary>
    private const int NoFlashbackRight = 1031;

    /// <summary>
    /// The undo does not reach back: snapshot too old (ORA-01555), no valid SCN (ORA-08181), no snapshot for the time
    /// (ORA-08180).
    /// </summary>
    private static readonly IReadOnlySet<int> UndoTooShort = new HashSet<int> { 1555, 8180, 8181 };

    /// <summary>The message to show for <paramref name="error"/>; null if it is none of those, then the error's own.</summary>
    /// <param name="startedAt">When the start point was set (or the time a tab travels to), named in the message about the undo.</param>
    /// <param name="timeTravel">The error of a time travel tab or of setting rows back (WP-78), not of a comparison.</param>
    public static string? Explain(DatabaseException error, TableRef table, DateTimeOffset startedAt, bool timeTravel = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(table);
        if (error.IsAny(new HashSet<int> { NoFlashbackRight }))
        {
            return TextFormat.Format(timeTravel ? DataText.TimeTravelNoFlashbackRight : DataText.DiffNoFlashbackRight, OracleIdentifier.Qualify(table.Owner, table.Name));
        }

        if (error.IsAny(UndoTooShort))
        {
            return TextFormat.Format(timeTravel ? DataText.TimeTravelUndoTooShort : DataText.DiffUndoTooShort, startedAt.LocalDateTime);
        }

        return error.IsAny(new HashSet<int> { OracleErrorCodes.DefinitionChanged })
            ? timeTravel ? DataText.TimeTravelTableChanged : DataText.DiffTableChanged
            : null;
    }

    /// <summary>
    /// The message for a time that cannot be turned into an SCN (WP-78): too far back for the database's mapping (ORA-08180);
    /// null for any other error.
    /// </summary>
    public static string? ExplainPoint(DatabaseException error, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.IsAny(UndoTooShort) ? TextFormat.Format(DataText.TimeTooFarBack, at.LocalDateTime) : null;
    }
}
