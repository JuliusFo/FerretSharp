using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// How a query result is shown (WP-48): a column traced back to a table column (<see cref="SqlResultSources"/>) gets the
/// table's C# name and type, enum members and FK badge, like in a table grid – under the name the query gave it. All other
/// columns stay as the driver reports them. No PK badge: grid.js pins key columns to the left, and the query's column
/// order counts. The form and the comparison of rows (WP-56) label and show the columns the same way.
/// </summary>
public sealed class SqlResultPresentation : IColumnPresentation
{
    private readonly IReadOnlyList<SqlColumn> _columns;
    private readonly ColumnInfo[] _infos;
    private readonly (TablePresentation Presentation, int Table)?[] _byColumn;
    private readonly bool[] _fk;
    private readonly bool[] _fkModel;

    /// <param name="present">The presentation of a table (<see cref="PresentationService.For"/>).</param>
    /// <param name="outgoing">The relationships starting at a table (<see cref="SchemaCache.OutgoingOf"/>).</param>
    public SqlResultPresentation(
        IReadOnlyList<SqlColumn> columns, SqlResultSources sources, Func<TableDetails, TablePresentation> present,
        Func<TableRef, IEnumerable<ForeignKeyInfo>> outgoing)
    {
        _columns = columns;
        _infos = [.. columns.Select(c => c.Column)];
        Sources = sources;
        _byColumn = new (TablePresentation, int)?[columns.Count];
        _fk = new bool[columns.Count];
        _fkModel = new bool[columns.Count];
        foreach (var source in sources.Sources)
        {
            var presentation = present(source.Details);
            // Only relationships a jump can follow: all their columns are in the result.
            var foreignKeys = outgoing(source.Table.Ref).Where(f => source.Shows(f.FromColumns)).ToList();
            foreach (var (result, table) in source.Columns)
            {
                var name = source.Details.Columns[table].Name;
                _byColumn[result] = (presentation, table);
                (_fk[result], _fkModel[result]) = ForeignKeyBadges.Of(foreignKeys, name); // as FerretGrid
            }
        }

        ShowsClrNames = _byColumn.Any(c => c is var (presentation, table) && presentation.ShowsClrNames && presentation.PropertyOf(table) is not null);
    }

    /// <summary>The result as the driver reports it: no sources, no C# names.</summary>
    public static SqlResultPresentation Plain(IReadOnlyList<SqlColumn> columns) => new(columns, SqlResultSources.None, TablePresentation.Plain, _ => []);

    public SqlResultSources Sources { get; }

    /// <summary>Some column carries C# names: the header has the line for them.</summary>
    public bool ShowsClrNames { get; }

    public int? HeaderHeight => ShowsClrNames ? GridOptions.ClrHeaderHeight : null;

    /// <summary>The result columns as the driver reports them (named as the query names them).</summary>
    IReadOnlyList<ColumnInfo> IColumnPresentation.Columns => _infos;

    public IReadOnlyList<GridColumn> Columns() => [.. _columns.Select((_, i) => ColumnOf(i))];

    /// <summary>
    /// A traced column under the name the query gave it, with the property name and type of its table column if C# names
    /// are shown; all other columns by that name alone.
    /// </summary>
    public ColumnLabel LabelOf(int column)
    {
        var name = _columns[column].Name;
        if (_byColumn[column] is not var (presentation, table) || !presentation.ShowsClrNames || presentation.PropertyOf(table) is not { } property)
        {
            return new ColumnLabel(name, null, null);
        }

        var type = TablePresentation.ClrTypeText(property);
        return presentation.Names == Core.Settings.ClrNameDisplay.Front
            ? new ColumnLabel(property.Name, name, type)
            : new ColumnLabel(name, property.Name, type);
    }

    public string? SearchNameOf(int column) => _byColumn[column] is var (presentation, table) ? presentation.SearchNameOf(table) : null;

    /// <summary>
    /// The tooltip of a traced column: the table column it holds (<c>Aus KUNDEN.NAME</c>) and that column's comment; null if
    /// it is not traced.
    /// </summary>
    public string? CommentOf(int column)
    {
        if (_byColumn[column] is not var (presentation, table))
        {
            return null;
        }

        var details = presentation.Details;
        var origin = TextFormat.Format(SqlEditorText.Result_ColumnOrigin, $"{details.Table.DisplayName}.{details.Columns[table].Name}");
        return details.Columns[table].Comment is { } comment ? origin + "\n" + comment : origin; // grid.js joins its lines with \n
    }

    private GridColumn ColumnOf(int i)
    {
        var column = _columns[i];
        var category = ColumnCategories.Of(column.Column);
        var plain = new GridColumn(GridColumn.IdOf(i), column.Name, column.Name, column.Column.DisplayType, category.ToString(), column.Column.Nullable)
        {
            Numeric = category == ColumnCategory.Number,
            ClrLine = ShowsClrNames,
        };
        if (_byColumn[i] is not var (presentation, table))
        {
            return plain;
        }

        var values = presentation.ValuesOf(table);
        var label = LabelOf(i);
        return plain with
        {
            Label = label.Name,
            Alternate = label.AlternateName,
            ClrType = label.ClrType,
            Fk = _fk[i],
            FkModel = _fkModel[i],
            Numeric = category == ColumnCategory.Number && values is null,
            Comment = CommentOf(i),
            UnknownText = values?.UnknownText,
        };
    }

    /// <summary>
    /// The columns of an Excel export (WP-53): a traced column as its table presents it (C# name, enum members), under the
    /// name the query gave it if C# names are off; all other columns as the driver reports them.
    /// </summary>
    public IReadOnlyList<ExportColumn> ExportColumns() =>
    [
        .. _columns.Select((column, i) => _byColumn[i] is var (presentation, table)
            ? PresentedExport.Column(presentation, table, column.Name, i) with { Column = column.Column }
            : new ExportColumn(column.Name, i, column.Column)),
    ];

    /// <summary>A cell's display text: enum members and bools by name for traced columns; null for NULL.</summary>
    public PresentedValue Present(int column, object? value) =>
        _byColumn[column] is var (presentation, table) ? presentation.Present(table, value) : new PresentedValue(CellFormatter.Format(_columns[column].Column, value));

    /// <summary>A row as grid.js gets it: cells by column id, <c>__u</c> values without enum member, <c>__t</c> tooltips (as <see cref="GridRows"/>).</summary>
    public Dictionary<string, object?> Row(IReadOnlyList<object?> values)
    {
        var cells = new GridRows.PresentedCells(new Dictionary<string, object?>(values.Count + 2));
        for (var i = 0; i < values.Count; i++)
        {
            cells.Add(GridColumn.IdOf(i), Present(i, values[i]));
        }

        return cells.Finish();
    }
}
