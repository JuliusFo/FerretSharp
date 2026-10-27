using System.Collections;
using System.Text;
using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Display;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>WP-54 (and A13): exports written block by block – the writers, the rows from memory or a read, names looked up per block.</summary>
public class RowExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ColumnInfo Col(string name, string type) => new(name, type, null, false, null, null, true, false, null, 0);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2"), Col("MANDANT_ID", "NUMBER")],
        ["ID"], [], false);

    private static readonly TableDetails Mandant = new(
        new TableSummary("APP", "MANDANT", TableKind.Table), [Col("ID", "NUMBER"), Col("NAME", "VARCHAR2")], ["ID"], [], false);

    private static readonly DisplayLink MandantLink = new(2,
        new ForeignKeyInfo("FK_MANDANT", Kunden.Table.Ref, ["MANDANT_ID"], Mandant.Table.Ref, ["ID"], FkSource.Declared), Mandant, 0, [new DisplayPart(1)]);

    private static RowData Row(params object?[] values) => new(RowKey.None.Instance, values);

    private static List<RowData> Rows(int count) => [.. Enumerable.Range(1, count).Select(i => Row((decimal)i, $"Kunde {i}", 7m))];

    [Fact]
    public async Task Csv_written_block_by_block_is_the_csv_export_with_byte_order_mark()
    {
        var rows = Rows(3);
        using var stream = new MemoryStream();

        var summary = await RowExport.WriteAsync(Blocks(rows[..2], rows[2..]), DelimitedExport.CreateWriter(stream, Kunden), null, null, Ct);

        Assert.Equal(3, summary.Rows);
        Assert.Equal([0xEF, 0xBB, 0xBF], stream.ToArray()[..3]);
        Assert.Equal(DelimitedExport.Build(Kunden, rows, DelimitedExport.CsvSeparator).Text, Encoding.UTF8.GetString(stream.ToArray()[3..]));
    }

    [Fact]
    public async Task Csv_without_rows_is_the_header()
    {
        using var stream = new MemoryStream();

        var summary = await RowExport.WriteAsync(RowExport.FromRows([]), DelimitedExport.CreateWriter(stream, Kunden), null, null, Ct);

        Assert.Equal(0, summary.Rows);
        Assert.Equal("ID;NAME;MANDANT_ID\r\n", Encoding.UTF8.GetString(stream.ToArray()[3..]));
    }

    [Fact]
    public async Task Excel_written_while_reading_filters_from_the_header_and_takes_widths_from_the_first_rows()
    {
        var columns = ExportColumn.Raw(Kunden);
        var late = Row(9999m, new string('x', 200), 7m); // after the sample: written, but no wider column
        using var stream = new MemoryStream();

        var summary = await RowExport.WriteAsync(Blocks(Rows(XlsxExport.WidthSample), [late]),
            await XlsxExport.CreateWriterAsync(stream, columns, "KUNDEN", null, Ct), null, null, Ct);

        var sheet = XlsxSheet.Read(stream.ToArray());
        Assert.Equal(XlsxExport.WidthSample + 1, summary.Rows);
        Assert.Equal("A1:C1", sheet.AutoFilter); // Excel extends it to the rows below when it opens the file
        Assert.Equal("A2", sheet.FrozenAt);
        Assert.Equal(new string('x', 200), sheet["B" + (XlsxExport.WidthSample + 2)].Value);
        Assert.True(sheet.Widths[1] < XlsxExport.MaxWidth);
    }

    [Fact]
    public async Task Excel_with_known_rows_filters_over_them()
    {
        using var stream = new MemoryStream();

        await RowExport.WriteAsync(RowExport.FromRows(Rows(3)), await XlsxExport.CreateWriterAsync(stream, ExportColumn.Raw(Kunden), "KUNDEN", 3, Ct), null, null, Ct);

        Assert.Equal("A1:C4", XlsxSheet.Read(stream.ToArray()).AutoFilter);
    }

    [Fact]
    public async Task A_full_sheet_stops_the_read_with_a_warning()
    {
        var reads = 0;
        RowReader read = async (onBlock, ct) =>
        {
            // One block of more rows than a sheet holds, then one that must not be asked for.
            foreach (var block in new IReadOnlyList<RowData>[] { new Repeated(Row(1m), XlsxExport.MaxSheetRows + 10), [Row(2m)] })
            {
                reads++;
                if (!await onBlock(block, NoLookup.Instance, ct))
                {
                    return;
                }
            }
        };
        var columns = new[] { new ExportColumn("ID", 0, Col("ID", "NUMBER")) };
        using var stream = new MemoryStream();

        var summary = await RowExport.WriteAsync(read, await XlsxExport.CreateWriterAsync(stream, columns, "X", null, Ct), null, null, Ct);

        Assert.Equal(1, reads);
        Assert.Equal(XlsxExport.MaxSheetRows - 1, summary.Rows);
        Assert.Equal($"Nur die ersten {XlsxExport.MaxSheetRows - 1:N0} Zeilen exportiert – mehr fasst ein Excel-Blatt nicht.", Assert.Single(summary.Warnings));
    }

    [Fact]
    public async Task Rows_in_memory_go_in_blocks_with_progress_and_the_writer_is_disposed()
    {
        var writer = new RecordingWriter();
        var progress = new List<long>();

        var summary = await RowExport.WriteAsync(RowExport.FromRows(Rows(1200)), writer, null, new SyncProgress<long>(progress.Add), Ct);

        Assert.Equal([500, 500, 200], writer.Blocks);
        Assert.Equal([500, 1000, 1200], progress);
        Assert.Equal(1200, summary.Rows);
        Assert.True(writer.Disposed);
    }

    [Fact]
    public async Task A_cancelled_export_stops_between_blocks()
    {
        using var cts = new CancellationTokenSource();
        var writer = new RecordingWriter { AfterWrite = cts.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RowExport.WriteAsync(RowExport.FromRows(Rows(1200)), writer, null, null, cts.Token));

        Assert.Equal([500], writer.Blocks);
        Assert.True(writer.Disposed);
    }

    [Fact]
    public async Task Names_are_looked_up_per_block_before_it_is_written()
    {
        var lookup = new FakeLookup(keys => [.. keys.Select(k => new LookupRow(k, [$"Mandant {k}"]))]);
        var names = new DisplayNames([MandantLink], (_, _, value) => value as string).Fresh((_, _, value) => value as string);
        var columns = PresentedExport.Columns(TablePresentation.Plain(Kunden), names);
        using var stream = new MemoryStream();

        var summary = await RowExport.WriteAsync(Read(lookup, [Row(1m, "A", 7m), Row(2m, "B", 8m)], [Row(3m, "C", 7m)]),
            await XlsxExport.CreateWriterAsync(stream, columns, "KUNDEN", null, Ct), names, null, Ct);

        var sheet = XlsxSheet.Read(stream.ToArray());
        Assert.Empty(summary.Warnings);
        Assert.Equal("MANDANT_ID · NAME", sheet["D1"].Value);
        Assert.Equal(["Mandant 7", "Mandant 8", "Mandant 7"], [sheet["D2"].Value, sheet["D3"].Value, sheet["D4"].Value]);
        Assert.Equal([[7m, 8m]], lookup.Asked); // 7 was known from the first block
    }

    [Fact]
    public async Task A_failing_lookup_leaves_its_names_out_with_a_warning()
    {
        var lookup = new FakeLookup(_ => throw new DatabaseException("Tabelle oder View nicht vorhanden", "ORA-00942"));
        var names = new DisplayNames([MandantLink], (_, _, value) => value as string).Fresh((_, _, value) => value as string);
        var columns = PresentedExport.Columns(TablePresentation.Plain(Kunden), names);
        using var stream = new MemoryStream();

        var summary = await RowExport.WriteAsync(Read(lookup, [Row(1m, "A", 7m)], [Row(2m, "B", 8m)]),
            await XlsxExport.CreateWriterAsync(stream, columns, "KUNDEN", null, Ct), names, null, Ct);

        var sheet = XlsxSheet.Read(stream.ToArray());
        Assert.Equal("Namen aus MANDANT.NAME fehlen: ORA-00942: Tabelle oder View nicht vorhanden", Assert.Single(summary.Warnings));
        Assert.Single(lookup.Asked); // switched off after the first failure
        Assert.False(sheet.Cells.TryGetValue("D2", out var name) && name.Value is { Length: > 0 });
        Assert.Equal(2, summary.Rows);
    }

    [Fact]
    public void A_copy_of_the_names_keeps_what_was_looked_up_and_a_fresh_one_starts_over()
    {
        var names = new DisplayNames([MandantLink], (_, _, value) => value as string);
        var lookups = names.Missing([[1m, "A", 7m], [2m, "B", 8m]]);
        names.Found(lookups[0], [new LookupRow(7m, ["Hauptmandant"])]);

        var copy = names.Copy();
        names.Clear();
        var fresh = copy.Fresh((_, _, _) => null);

        Assert.Equal(new LookedUpName("Hauptmandant", false), copy.NameOf(2, 7m));
        Assert.Equal(new LookedUpName(null, true), copy.NameOf(2, 8m));
        Assert.Null(names.NameOf(2, 7m));
        Assert.Null(fresh.NameOf(2, 7m));
        Assert.True(fresh.Shows(2));
    }

    /// <summary>Hands the blocks to the export as a read would, with the lookup of the read.</summary>
    private static RowReader Read(IRowLookup lookup, params IReadOnlyList<RowData>[] blocks) => async (onBlock, ct) =>
    {
        foreach (var block in blocks)
        {
            if (!await onBlock(block, lookup, ct))
            {
                return;
            }
        }
    };

    private static RowReader Blocks(params IReadOnlyList<RowData>[] blocks) => Read(NoLookup.Instance, blocks);

    private sealed class NoLookup : IRowLookup
    {
        public static readonly NoLookup Instance = new();

        public Task<IReadOnlyList<LookupRow>> LookupAsync(TableDetails target, int keyColumn, IReadOnlyList<int> displayColumns, IReadOnlyList<object> keys, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no lookup expected");
    }

    private sealed class FakeLookup(Func<IReadOnlyList<object>, IReadOnlyList<LookupRow>> answer) : IRowLookup
    {
        public List<IReadOnlyList<object>> Asked { get; } = [];

        public Task<IReadOnlyList<LookupRow>> LookupAsync(TableDetails target, int keyColumn, IReadOnlyList<int> displayColumns, IReadOnlyList<object> keys, CancellationToken cancellationToken)
        {
            Asked.Add([.. keys]);
            return Task.FromResult(answer(keys));
        }
    }

    private sealed class RecordingWriter : IExportWriter
    {
        public List<int> Blocks { get; } = [];

        public Action? AfterWrite { get; init; }

        public bool Disposed { get; private set; }

        public long Rows { get; private set; }

        public ValueTask<bool> WriteAsync(IReadOnlyList<RowData> rows, CancellationToken cancellationToken)
        {
            Blocks.Add(rows.Count);
            Rows += rows.Count;
            AfterWrite?.Invoke();
            return ValueTask.FromResult(true);
        }

        public ValueTask<ExportSummary> FinishAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ExportSummary(Rows, []));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The same row many times, without holding them.</summary>
    private sealed class Repeated(RowData row, int count) : IReadOnlyList<RowData>
    {
        public RowData this[int index] => row;

        public int Count => count;

        public IEnumerator<RowData> GetEnumerator() => Enumerable.Repeat(row, count).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
