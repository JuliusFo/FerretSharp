using System.Data.Common;
using System.Globalization;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>
/// Reading result rows of the data dictionary and the plan views: typed getters for nullable columns (Oracle returns
/// numbers as decimal or <c>OracleDecimal</c>-backed values, hence the conversions) and a query read into a list.
/// </summary>
internal static class OracleReading
{
    /// <summary>The cap for <see cref="FetchManyRows"/>.</summary>
    private const long MaxFetchBytes = 16 * 1024 * 1024;

    public static string? Text(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? Long(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static int? Int(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    public static DateTime? Date(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    /// <summary>
    /// Thousands of rows: fetch them in a few round trips, not in the default 128 KB portions (slow over a VPN). Capped,
    /// because a LONG column counts with its full fetch size (InitialLONGFetchSize) in the row size: about 32 KB a row
    /// for the columns with DATA_DEFAULT, which would make 5000 rows a 160 MB buffer.
    /// </summary>
    public static void FetchManyRows(DbDataReader reader)
    {
        if (reader is OracleDataReader oracle && oracle.RowSize > 0)
        {
            oracle.FetchSize = Math.Min(oracle.RowSize * 5000L, MaxFetchBytes);
        }
    }

    /// <summary>All rows of a query, each mapped by <paramref name="map"/>.</summary>
    /// <param name="many">Thousands of rows expected (a whole schema): see <see cref="FetchManyRows"/>.</param>
    public static Task<List<T>> ReadListAsync<T>(
        this OracleSession session, string sql, IReadOnlyList<QueryParameter> parameters, Func<DbDataReader, T> map,
        CancellationToken cancellationToken, bool many = false) =>
        session.ExecuteReaderAsync(sql, parameters, async (reader, ct) =>
        {
            if (many)
            {
                FetchManyRows(reader);
            }

            var result = new List<T>();
            while (await reader.ReadAsync(ct))
            {
                result.Add(map(reader));
            }

            return result;
        }, cancellationToken);
}
