using FerretSharp.Core.Display;

namespace FerretSharp.Core.Data;

/// <summary>What an export wrote.</summary>
/// <param name="Warnings">Values that could not be exported completely (LOBs only loaded as preview, texts shortened …).</param>
public sealed record ExportSummary(long Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes an export file block by block (WP-54): CSV (<see cref="DelimitedExport.CreateWriter"/>) or Excel
/// (<see cref="XlsxExport.CreateWriterAsync"/>). Nothing is complete before <see cref="FinishAsync"/>.
/// </summary>
public interface IExportWriter : IAsyncDisposable
{
    /// <summary>Rows written so far.</summary>
    long Rows { get; }

    /// <summary>Writes rows; false if the file is full (an Excel sheet) – the rest is left out, with a warning.</summary>
    ValueTask<bool> WriteAsync(IReadOnlyList<RowData> rows, CancellationToken cancellationToken);

    /// <summary>Completes the file.</summary>
    ValueTask<ExportSummary> FinishAsync(CancellationToken cancellationToken);
}

/// <summary>Hands rows to <paramref name="onBlock"/> block by block: a query read again (<see cref="IDataAccess.ReadAllAsync"/>) or rows in memory.</summary>
public delegate Task RowReader(RowBlockHandler onBlock, CancellationToken cancellationToken);

/// <summary>
/// An export from its rows to its writer (WP-54): the selected rows of a grid, or all rows of its query read again –
/// with the names beside foreign keys looked up block by block before the block is written.
/// </summary>
public static class RowExport
{
    /// <summary>Rows already in memory (the selected rows), in blocks of <see cref="IDataAccess.BlockSize"/> – for progress and cancelling.</summary>
    public static RowReader FromRows(IReadOnlyList<RowData> rows) => async (onBlock, cancellationToken) =>
    {
        foreach (var block in rows.Chunk(IDataAccess.BlockSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await onBlock(block, NoLookup.Instance, cancellationToken))
            {
                return;
            }
        }
    };

    /// <param name="names">
    /// Names to look up for each block before it is written (the Excel columns beside foreign keys read them); null if the
    /// columns need none or have them already (the selected rows: the grid looked them up).
    /// </param>
    /// <param name="writer">Disposed at the end.</param>
    /// <param name="progress">Rows written so far, after each block.</param>
    public static async Task<ExportSummary> WriteAsync(
        RowReader read, IExportWriter writer, DisplayNames? names, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await using (writer)
        {
            var notes = new List<string>();
            await read(async (block, lookup, ct) =>
            {
                if (names is { IsEmpty: false })
                {
                    notes.AddRange(await names.FillAsync(lookup, block.Select(r => r.Values), ct));
                }

                var more = await writer.WriteAsync(block, ct);
                progress?.Report(writer.Rows);
                return more;
            }, cancellationToken);

            var summary = await writer.FinishAsync(cancellationToken);
            return notes.Count == 0 ? summary : summary with { Warnings = [.. summary.Warnings, .. notes] };
        }
    }

    /// <summary>Rows in memory have no session to look anything up in.</summary>
    private sealed class NoLookup : IRowLookup
    {
        public static readonly NoLookup Instance = new();

        public Task<IReadOnlyList<LookupRow>> LookupAsync(
            Schema.TableDetails target, int keyColumn, int displayColumn, IReadOnlyList<object> keys, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Rows in memory are exported with the names the grid looked up.");
    }
}
