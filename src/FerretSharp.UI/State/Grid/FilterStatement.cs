using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>What "SQL" in a table's filter bar shows and opens in the SQL editor, as the setting says.</summary>
public static class FilterStatement
{
    /// <summary>The grid's own statement for its first page, or <c>SELECT t.*</c> with the filter and the sorting.</summary>
    /// <param name="asOf">The SCN of a time travel tab (WP-78): the statement reads what the tab shows.</param>
    /// <exception cref="QueryValidationException">A filter or sort is invalid.</exception>
    public static QuerySpec Of(
        TableDetails table, RowFilter filters, IReadOnlyList<SortSpec> sorts, FilterSqlColumns columns, long? asOf = null)
    {
        if (columns == FilterSqlColumns.Star)
        {
            return QueryBuilder.BuildSelectStar(table, filters, sorts, asOf);
        }

        var page = QueryBuilder.BuildSelect(table, filters, sorts, new PageSpec(0, IDataAccess.BlockSize), asOf);
        return new QuerySpec(page.Sql, page.Parameters);
    }
}
