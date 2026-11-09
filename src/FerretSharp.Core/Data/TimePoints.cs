using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Data;

/// <summary>
/// Rules for points in the past (WP-78): a chosen time, and whether a saved SCN still belongs to the database the connection
/// reaches now.
/// </summary>
public static class TimePoints
{
    /// <summary>How long before <paramref name="now"/> the chosen time lies; why it cannot be used (in the future) as error.</summary>
    public static (TimeSpan Back, string? Error) Back(DateTimeOffset at, DateTimeOffset now) =>
        at > now + TimeSpan.FromSeconds(1) ? (TimeSpan.Zero, DataText.TimeInFuture) : (now > at ? now - at : TimeSpan.Zero, null);

    /// <summary>
    /// Why a saved point (<paramref name="scn"/> of <paramref name="database"/>) means nothing on the database the connection
    /// reaches now (<paramref name="now"/>); null if it still belongs there. Another database by name – the profile was switched
    /// to another one –, or an SCN ahead of the database's – one set up anew under the same name. A point without a database
    /// (saved by an older version) is only checked by its SCN.
    /// </summary>
    public static string? Mismatch(long scn, string? database, DatabasePoint now)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (database is { Length: > 0 } saved && now.Database.Length > 0 && !string.Equals(saved, now.Database, StringComparison.Ordinal))
        {
            return TextFormat.Format(DataText.PointOtherDatabase, saved, now.Database);
        }

        return scn > now.Scn ? DataText.PointAheadOfDatabase : null;
    }
}
