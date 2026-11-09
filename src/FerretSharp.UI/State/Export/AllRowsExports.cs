using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>The columns of an Excel export of all rows, and the names they write – looked up while reading.</summary>
/// <param name="Names">Null if the columns show no names.</param>
public sealed record AllRowsExcel(IReadOnlyList<ExportColumn> Columns, DisplayNames? Names);

/// <summary>
/// All rows of a grid's query for the export menu (WP-54): read again, block by block, in the workspace's session.
/// </summary>
/// <param name="Heading">Heading of the menu group ("All rows of the filter · 12,345 rows").</param>
/// <param name="Read">Reads the rows (<see cref="IDataAccess.ReadAllAsync"/>, <see cref="IDataAccess.ReadSqlAllAsync"/>).</param>
/// <param name="Total">Rows expected, if counted.</param>
/// <param name="Excel">
/// The Excel columns, built when the export is chosen (on the UI thread: it takes the presentations as they are then) – not
/// on every opening of the menu.
/// </param>
/// <param name="WorkspaceId">The workspace whose session reads the rows (<see cref="BackgroundRead.WorkspaceId"/>).</param>
public sealed record AllRowsExport(string Heading, RowReader Read, long? Total, Func<AllRowsExcel> Excel, Guid WorkspaceId);

/// <summary>What "all rows" of a table tab and of a SQL editor result read and write (WP-54).</summary>
public static class AllRowsExports
{
    /// <summary>
    /// All rows of a table tab's query as it stands when the menu opens: its applied filters and sort order (not the filter
    /// rows being typed). Its names beside foreign keys are looked up anew while reading – with every link of the grid, also
    /// one the grid switched off – and formatted with the targets' presentation as it is when the export starts.
    /// </summary>
    /// <param name="presentationOf">The presentation of a target table (<c>PresentationService.For</c>).</param>
    /// <param name="dataOf">The data access of a workspace (<c>WorkspaceManager.GetDataAsync</c>).</param>
    public static AllRowsExport ForTable(
        TableTab tab, TableDetails details, TablePresentation presentation, DisplayNames gridNames,
        Func<TableDetails, TablePresentation> presentationOf, Func<Guid, CancellationToken, Task<IDataAccess>> dataOf)
    {
        var (filters, sorts, workspaceId) = (tab.ReadFilter, tab.Sorts, tab.WorkspaceId);
        var total = TotalOf(tab);
        return new AllRowsExport(
            Heading(GridText.GridMenu_AllRowsGroup, total),
            async (onBlock, ct) => await (await dataOf(workspaceId, ct)).ReadAllAsync(details, filters, sorts, onBlock, ct),
            total,
            () =>
            {
                var names = FreshNames(gridNames, presentationOf);
                return new AllRowsExcel(PresentedExport.Columns(presentation, names ?? DisplayNames.None), names);
            },
            workspaceId);
    }

    /// <summary>
    /// New names over the grid's links for an export that looks them up while reading (with every link of the grid, also
    /// one the grid switched off); null if the grid has none. The export formats off the UI thread: the targets'
    /// presentations (one level deeper too, WP-65) are taken now, not looked up from there.
    /// </summary>
    private static DisplayNames? FreshNames(DisplayNames gridNames, Func<TableDetails, TablePresentation> presentationOf)
    {
        if (gridNames.IsEmpty)
        {
            return null;
        }

        var targets = gridNames.Links
            .SelectMany(l => l.Parts.Where(p => p.Via is not null).Select(p => p.Via!.Target).Prepend(l.Target))
            .DistinctBy(t => t.Table.Ref).ToDictionary(t => t.Table.Ref, presentationOf);
        return gridNames.Fresh((target, column, value) => targets[target.Table.Ref].Present(column, value).Text);
    }

    /// <summary>
    /// All rows of a SQL editor result: the query runs again – refused if it now returns other columns than
    /// <paramref name="columns"/>. Names beside foreign keys (WP-73) are looked up anew while reading, as for a table.
    /// </summary>
    /// <param name="total">The result's size if all its rows were loaded.</param>
    /// <param name="xlsxColumns">The Excel columns with the names they write (<see cref="SqlResultPresentation.ExportColumns"/>).</param>
    /// <param name="gridNames">The names of the result grid; <see cref="DisplayNames.None"/> writes none.</param>
    /// <param name="presentationOf">The presentation of a target table (<c>PresentationService.For</c>).</param>
    public static AllRowsExport ForResult(
        QuerySpec query, IReadOnlyList<SqlColumn> columns, long? total, Func<DisplayNames?, IReadOnlyList<ExportColumn>> xlsxColumns,
        DisplayNames gridNames, Func<TableDetails, TablePresentation> presentationOf, Guid workspaceId,
        Func<Guid, CancellationToken, Task<IDataAccess>> dataOf)
    {
        var names = columns.Select(c => c.Name).ToList();
        return new AllRowsExport(
            Heading(GridText.GridMenu_AllResultRowsGroup, total),
            async (onBlock, ct) => await (await dataOf(workspaceId, ct)).ReadSqlAllAsync(query, names, onBlock, ct),
            total,
            () =>
            {
                var names = FreshNames(gridNames, presentationOf);
                return new AllRowsExcel(xlsxColumns(names), names);
            },
            workspaceId);
    }

    /// <summary>The rows of a table tab's query, if known: counted, or all loaded.</summary>
    internal static long? TotalOf(TableTab tab) => tab.TotalCount ?? (tab.AllRowsLoaded ? tab.RowsLoaded : null);

    /// <summary>A heading of the menu group for all rows, with the number of rows if it is known.</summary>
    internal static string Heading(string format, long? total) =>
        TextFormat.Format(format, total is { } rows ? " · " + ExportActions.RowsText(rows) : "");
}
