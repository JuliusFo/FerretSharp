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
/// order counts.
/// </summary>
public sealed class SqlResultPresentation
{
    private readonly IReadOnlyList<SqlColumn> _columns;
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
                _fk[result] = foreignKeys.Any(f => f.Source == FkSource.Declared && f.FromColumns.Contains(name));
                _fkModel[result] = foreignKeys.Any(f => f.Source == FkSource.ClrModel && f.FromColumns.Contains(name)); // as FerretGrid
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

    public IReadOnlyList<GridColumn> Columns() => [.. _columns.Select((_, i) => ColumnOf(i))];

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

        var details = presentation.Details;
        var values = presentation.ValuesOf(table);
        var property = presentation.ShowsClrNames ? presentation.PropertyOf(table) : null;
        var (label, alternate) = property is null ? (column.Name, (string?)null)
            : presentation.Names == Core.Settings.ClrNameDisplay.Front ? (property.Name, column.Name)
            : (column.Name, property.Name);
        var origin = TextFormat.Format(SqlEditorText.Result_ColumnOrigin, $"{details.Table.DisplayName}.{details.Columns[table].Name}");
        return plain with
        {
            Label = label,
            Alternate = alternate,
            ClrType = property is null ? null : TablePresentation.ClrTypeText(property),
            Fk = _fk[i],
            FkModel = _fkModel[i],
            Numeric = category == ColumnCategory.Number && values is null,
            Comment = details.Columns[table].Comment is { } comment ? origin + "\n" + comment : origin, // grid.js joins its lines with \n
            UnknownText = values?.UnknownText,
        };
    }

    /// <summary>A cell's display text: enum members and bools by name for traced columns; null for NULL.</summary>
    public PresentedValue Present(int column, object? value) =>
        _byColumn[column] is var (presentation, table) ? presentation.Present(table, value) : new PresentedValue(CellFormatter.Format(_columns[column].Column, value));

    /// <summary>A row as grid.js gets it: cells by column id, <c>__u</c> values without enum member, <c>__t</c> tooltips (as <see cref="GridRows"/>).</summary>
    public Dictionary<string, object?> Row(IReadOnlyList<object?> values)
    {
        var cells = new Dictionary<string, object?>(values.Count + 2);
        List<string>? unknown = null;
        Dictionary<string, string>? tooltips = null;
        for (var i = 0; i < values.Count; i++)
        {
            var id = GridColumn.IdOf(i);
            var presented = Present(i, values[i]);
            cells[id] = presented.Text;
            if (presented.Unknown)
            {
                (unknown ??= []).Add(id);
            }

            if (presented.Tooltip is { } tooltip)
            {
                (tooltips ??= [])[id] = tooltip;
            }
        }

        if (unknown is not null)
        {
            cells["__u"] = unknown;
        }

        if (tooltips is not null)
        {
            cells["__t"] = tooltips;
        }

        return cells;
    }
}
