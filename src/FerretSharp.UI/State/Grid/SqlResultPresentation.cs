using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// How a query result is shown (WP-48): a column traced back to a table column (<see cref="SqlResultSources"/>) gets the
/// table's C# name and type, enum members and FK badge, like in a table grid – under the name the query gave it. All other
/// columns stay as the driver reports them. No PK badge: grid.js pins key columns to the left, and the query's column
/// order counts. The form and the comparison of rows (WP-56) label and show the columns the same way. Names beside foreign
/// key values (WP-73) come from the grid's <see cref="DisplayNames"/>, planned over the same sources.
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

    public int? HeaderHeight(ColumnHeaderLines header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.HeightWith(ShowsClrNames);
    }

    /// <summary>The result columns as the driver reports them (named as the query names them).</summary>
    IReadOnlyList<ColumnInfo> IColumnPresentation.Columns => _infos;

    /// <param name="header">Type and NOT NULL as the settings show them.</param>
    /// <param name="order">Which headers sort and why the others do not (<see cref="ResultOrder"/>); none without.</param>
    /// <param name="names">The names the grid looks up (WP-73): their columns are laid out for a name; null without.</param>
    public IReadOnlyList<GridColumn> Columns(ColumnHeaderLines header, ResultOrder? order = null, DisplayNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        return [.. _columns.Select((_, i) => ColumnOf(i, header, names?.Shows(i) == true) with { Sortable = order?.Sortable(i) ?? false, SortHint = order?.HintOf(i) })];
    }

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

    private GridColumn ColumnOf(int i, ColumnHeaderLines header, bool named)
    {
        var column = _columns[i];
        var category = ColumnCategories.Of(column.Column);
        var plain = new GridColumn(GridColumn.IdOf(i), column.Name, column.Name, header.TextOf(column.Column), category.ToString(), column.Column.Nullable)
        {
            TypeLine = header.TypeLine,
            TypeHint = header.HintOf(column.Column),
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
            Numeric = category == ColumnCategory.Number && values is null && !named,
            Named = named,
            Comment = CommentOf(i),
            UnknownText = values?.UnknownText,
        };
    }

    /// <summary>
    /// The columns of an Excel export (WP-53): a traced column as its table presents it (C# name, enum members), under the
    /// name the query gave it if C# names are off; all other columns as the driver reports them. Behind a column with names
    /// (WP-73) a column with them, as in a table's export.
    /// </summary>
    /// <param name="names">The names the grid has looked up; null adds no name columns.</param>
    public IReadOnlyList<ExportColumn> ExportColumns(DisplayNames? names = null)
    {
        var columns = new List<ExportColumn>();
        for (var i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            var export = _byColumn[i] is var (presentation, table)
                ? PresentedExport.Column(presentation, table, column.Name, i) with { Column = column.Column }
                : new ExportColumn(column.Name, i, column.Column);
            columns.Add(export);
            if (names is not null && names.Shows(i) && names.Links.FirstOrDefault(l => l.Column == i) is { } link)
            {
                var source = i;
                columns.Add(new ExportColumn($"{export.Header} · {link.Label}", i, link.Target.Columns[link.Parts[0].Column])
                {
                    Present = value => names.NameOf(source, value)?.Text,
                });
            }
        }

        return columns;
    }

    /// <summary>A cell's display text: enum members and bools by name for traced columns; null for NULL.</summary>
    public PresentedValue Present(int column, object? value) =>
        _byColumn[column] is var (presentation, table) ? presentation.Present(table, value) : new PresentedValue(CellFormatter.Format(_columns[column].Column, value));

    /// <summary>
    /// A row as grid.js gets it: cells by column id, <c>__u</c> values without enum member, <c>__t</c> tooltips, <c>__n</c>
    /// and <c>__nm</c> the names beside foreign key values (WP-73; as <see cref="GridRows"/>).
    /// </summary>
    /// <param name="names">The names the grid has looked up; null without.</param>
    public Dictionary<string, object?> Row(IReadOnlyList<object?> values, DisplayNames? names = null)
    {
        var all = new Dictionary<string, object?>(values.Count + 4);
        var cells = new GridRows.PresentedCells(all);
        var named = new GridRows.NamedCells(all, names);
        for (var i = 0; i < values.Count; i++)
        {
            var id = GridColumn.IdOf(i);
            cells.Add(id, Present(i, values[i]));
            named.Add(i, id, values[i]);
        }

        cells.Finish();
        return named.Finish();
    }
}
