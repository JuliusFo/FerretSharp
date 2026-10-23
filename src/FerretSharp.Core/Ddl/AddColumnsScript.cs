using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Ddl;

/// <summary>
/// The script that adds columns of a table to the same table on another database (the columns view's menu): one
/// <c>ALTER TABLE … ADD</c> per column in schema order, then their comments – the steps of <see cref="ColumnChange"/>, so
/// the warnings (NOT NULL without default) are those of the schema comparison. Shown and opened in a SQL tab, never run
/// from here.
/// </summary>
public static class AddColumnsScript
{
    /// <param name="source">The table the columns come from, named in the header.</param>
    /// <param name="target">The table the statements change: on another connection the matching table there (the owner may differ).</param>
    /// <param name="columns">The columns to add; their order does not matter (schema order, so a virtual column follows its base columns).</param>
    public static string Write(TableRef source, TableRef target, IEnumerable<ColumnInfo> columns)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(columns);
        var steps = columns.OrderBy(c => c.Position).SelectMany(c => ColumnChange.Steps(target, null, c, ChangeIntent.Known));
        return DdlScript.Render(DdlScript.Order(steps), ["-- " + TextFormat.Format(CompareText.AddColumnsHeader, source)]);
    }
}
