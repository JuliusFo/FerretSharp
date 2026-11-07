using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ServerLoad;

/// <summary>The columns of the top list, in the order of the page and the Excel file.</summary>
public enum StatementColumn
{
    SqlId,
    Schema,
    Module,
    Executions,
    Cpu,
    CpuPerExecution,
    Elapsed,
    ElapsedPerExecution,
    BufferGets,
    BufferGetsPerExecution,
    DiskReads,
    Rows,
    UserIoWait,
    ConcurrencyWait,
    ApplicationWait,
    Plans,
    LastActive,
    Text,
}

/// <summary>
/// The top list as an Excel file (WP-75), through the export of the grid (<see cref="XlsxExport"/>): times in seconds, per
/// execution in milliseconds, numbers as numbers, the text as text.
/// </summary>
public static class ServerLoadExport
{
    /// <param name="header">The heading of a column, in the language of the page.</param>
    public static async Task<ExportSummary> WriteXlsxAsync(
        Stream stream, IReadOnlyList<StatementRow> rows, Func<StatementColumn, string> header, string sheetName, CancellationToken cancellationToken)
    {
        var columns = Enum.GetValues<StatementColumn>();
        var exported = columns.Select((c, i) => new ExportColumn(header(c), i, ColumnOf(c, i))).ToList();
        await using var writer = await XlsxExport.CreateWriterAsync(stream, exported, sheetName, rows.Count, cancellationToken);
        await writer.WriteAsync([.. rows.Select(r => new RowData(RowKey.None.Instance, [.. columns.Select(c => ValueOf(r, c))]))], cancellationToken);
        return await writer.FinishAsync(cancellationToken);
    }

    /// <summary>The value of a cell; times as decimal seconds (per execution: milliseconds), rounded to what is worth reading.</summary>
    public static object? ValueOf(StatementRow row, StatementColumn column)
    {
        var f = row.Figures;
        return column switch
        {
            StatementColumn.SqlId => row.SqlId,
            StatementColumn.Schema => row.Info?.Schema,
            StatementColumn.Module => row.Info?.Module,
            StatementColumn.Executions => f.Executions,
            StatementColumn.Cpu => Seconds(f.CpuTime),
            StatementColumn.CpuPerExecution => Milliseconds(f.PerExecution(f.CpuTime)),
            StatementColumn.Elapsed => Seconds(f.ElapsedTime),
            StatementColumn.ElapsedPerExecution => Milliseconds(f.PerExecution(f.ElapsedTime)),
            StatementColumn.BufferGets => f.BufferGets,
            StatementColumn.BufferGetsPerExecution => Math.Round((decimal)f.PerExecution(f.BufferGets), 1),
            StatementColumn.DiskReads => f.DiskReads,
            StatementColumn.Rows => f.Rows,
            StatementColumn.UserIoWait => Seconds(f.UserIoWait),
            StatementColumn.ConcurrencyWait => Seconds(f.ConcurrencyWait),
            StatementColumn.ApplicationWait => Seconds(f.ApplicationWait),
            StatementColumn.Plans => row.Plans,
            StatementColumn.LastActive => row.LastActive,
            StatementColumn.Text => row.Info?.Text,
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
        };
    }

    private static decimal Seconds(long micros) => Math.Round(micros / 1_000_000m, 3);

    private static decimal Milliseconds(double micros) => Math.Round((decimal)micros / 1000m, 3);

    private static ColumnInfo ColumnOf(StatementColumn column, int position) => column switch
    {
        StatementColumn.SqlId or StatementColumn.Schema or StatementColumn.Module or StatementColumn.Text =>
            new(column.ToString(), "VARCHAR2", 4000, true, null, null, true, false, null, position),
        StatementColumn.LastActive => new(column.ToString(), "DATE", null, false, null, null, true, false, null, position),
        _ => new(column.ToString(), "NUMBER", null, false, null, null, true, false, null, position),
    };
}
