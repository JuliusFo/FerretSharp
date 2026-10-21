using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.UI.State;
using Microsoft.JSInterop;
using NSubstitute;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// A13 and WP-54: an export asks where first, then writes in the background – shown in the status bar, cancellable, never a
/// half file.
/// </summary>
public sealed class ExportActionsTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly ShellState _shell = new();
    private readonly IFileSaveService _files = Substitute.For<IFileSaveService>();

    private ExportActions Actions(int missingRows = 0) =>
        new(_shell, new DomModule(Substitute.For<IJSRuntime>()), _files, missingRows, () => Task.CompletedTask, new ListLogger(), null);

    private string Target(string name)
    {
        var path = _folder.Combine(name);
        _files.AskPathAsync(name, "CSV").Returns(path);
        return path;
    }

    private static async Task<ExportSummary> WriteAsync(Stream stream, string text, CancellationToken ct, params string[] warnings)
    {
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), ct);
        return new ExportSummary(3, warnings);
    }

    [Fact]
    public async Task Saves_after_the_dialog_in_the_background_shown_while_it_runs()
    {
        var path = Target("KUNDEN.csv");
        FileExport? running = null;

        await Actions(missingRows: 2).SaveRowsAsync("KUNDEN.csv", "CSV", 3, (stream, _, ct) =>
        {
            running = Assert.Single(_shell.Exports.Running);
            return WriteAsync(stream, "a;b", ct, "LOB nur Vorschau");
        });

        Assert.Equal("KUNDEN.csv", running!.Label);
        Assert.Equal(3, running.Total);
        Assert.Empty(_shell.Exports.Running);
        Assert.Equal("a;b", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal($"3 Zeilen gespeichert: {path}", _shell.Notice!.Text);
        Assert.Equal(["LOB nur Vorschau", "2 Zeilen der Auswahl nicht mehr geladen – nicht exportiert."], _shell.Notice.Warnings);
    }

    [Fact]
    public async Task All_rows_do_not_mention_selected_rows_no_longer_loaded()
    {
        Target("KUNDEN.csv");

        await Actions(missingRows: 2).SaveAllRowsAsync("KUNDEN.csv", "CSV", null, Guid.NewGuid(), (stream, _, ct) => WriteAsync(stream, "a;b", ct));

        Assert.Empty(_shell.Notice!.Warnings);
    }

    [Fact]
    public async Task A_cancelled_dialog_writes_nothing()
    {
        var written = false;
        _files.AskPathAsync(default!, default!).ReturnsForAnyArgs((string?)null);

        await Actions().SaveAllRowsAsync("KUNDEN.csv", "CSV", null, Guid.NewGuid(), (stream, _, ct) =>
        {
            written = true;
            return WriteAsync(stream, "a;b", ct);
        });

        Assert.False(written);
        Assert.Null(_shell.Notice);
        Assert.Empty(_shell.Exports.Running);
    }

    [Fact]
    public async Task Cancelling_keeps_the_existing_file_and_says_so()
    {
        var path = Target("KUNDEN.csv");
        await File.WriteAllTextAsync(path, "alt", TestContext.Current.CancellationToken);

        await Actions().SaveAllRowsAsync("KUNDEN.csv", "CSV", null, Guid.NewGuid(), async (stream, _, ct) =>
        {
            await stream.WriteAsync("halb"u8.ToArray(), ct);
            _shell.Exports.Running[0].Cancel(); // the status bar's button
            ct.ThrowIfCancellationRequested();
            return new ExportSummary(0, []);
        });

        Assert.Equal("alt", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_folder.Path, "*.tmp"));
        Assert.Equal("Export abgebrochen – KUNDEN.csv wurde nicht geschrieben.", _shell.Notice!.Text);
        Assert.Empty(_shell.Exports.Running);
    }

    [Fact]
    public async Task A_database_error_opens_the_error_dialog_and_leaves_no_file()
    {
        var path = Target("KUNDEN.csv");

        await Actions().SaveAllRowsAsync("KUNDEN.csv", "CSV", null, Guid.NewGuid(), (_, _, _) =>
            throw new DatabaseException("Snapshot zu alt", "ORA-01555"));

        Assert.Equal("ORA-01555", _shell.ErrorDetails!.ErrorCode);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_folder.Path, "*.tmp"));
        Assert.Empty(_shell.Exports.Running);
    }

    [Fact]
    public async Task A_refusal_is_a_notice()
    {
        Target("SQL 1.csv");

        await Actions().SaveAllRowsAsync("SQL 1.csv", "CSV", null, Guid.NewGuid(), (_, _, _) => throw new RefusedException("Andere Spalten"));

        Assert.Equal("Andere Spalten", _shell.Notice!.Text);
        Assert.Null(_shell.ErrorDetails);
    }

    [Fact]
    public void Progress_is_reported_until_the_export_ends()
    {
        var exports = new FileExports();
        var changes = 0;
        exports.Changed += () => changes++;

        var export = exports.Start("KUNDEN.xlsx", 1200);
        exports.Report(export, 500);
        exports.Report(export, 500); // nothing new
        exports.End(export);
        exports.Report(export, 1000); // a report posted before the end, arriving after it

        Assert.Equal(500, export.Rows);
        Assert.Equal(3, changes);
        Assert.Empty(exports.Running);
    }

    public void Dispose() => _folder.Dispose();
}
