using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>What "SQL" in a table's filter bar shows and opens in the SQL editor, as the setting says.</summary>
public static class FilterStatement
{
    /// <summary>The grid's own statement for its first page, or <c>SELECT t.*</c> with the filter and the sorting.</summary>
    /// <exception cref="QueryValidationException">A filter or sort is invalid.</exception>
    public static QuerySpec Of(TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, FilterSqlColumns columns)
    {
        if (columns == FilterSqlColumns.Star)
        {
            return QueryBuilder.BuildSelectStar(table, filters, sorts);
        }

        var page = QueryBuilder.BuildSelect(table, filters, sorts, new PageSpec(0, IDataAccess.BlockSize));
        return new QuerySpec(page.Sql, page.Parameters);
    }
}
