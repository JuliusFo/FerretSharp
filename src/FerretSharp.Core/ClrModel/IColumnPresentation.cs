using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// How the columns of a row are labelled and its values shown: a table's (<see cref="TablePresentation"/>) or a query
/// result's, whose columns may come from several tables (WP-56). What the form view and the comparison of rows need.
/// </summary>
public interface IColumnPresentation
{
    /// <summary>The columns as the database reports them, by index.</summary>
    IReadOnlyList<ColumnInfo> Columns { get; }

    ColumnLabel LabelOf(int column);

    /// <summary>A cell's display text: enum members and bools by name where known; null for NULL.</summary>
    PresentedValue Present(int column, object? value);

    /// <summary>The property name of a column if C# names are shown (for the column search); null otherwise.</summary>
    string? SearchNameOf(int column);
}
