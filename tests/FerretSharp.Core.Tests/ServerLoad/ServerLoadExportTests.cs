using System.IO.Compression;
using FerretSharp.Core.ServerLoad;

namespace FerretSharp.Core.Tests.ServerLoad;

public sealed class ServerLoadExportTests
{
    private static readonly StatementRow Row = new("abc123", 2,
        new StatementFigures(Executions: 4, CpuTime: 2_500_000, ElapsedTime: 3_000_000, BufferGets: 10, DiskReads: 1, Rows: 8, ParseCalls: 4, UserIoWait: 1_000, ConcurrencyWait: 0, ApplicationWait: 0),
        new StatementInfo("abc123", "SELECT 1 FROM dual", "Shop.Api", "SHOP"), new DateTime(2026, 11, 7, 10, 0, 0));

    [Theory]
    [InlineData(StatementColumn.Cpu, 2.5)] // seconds
    [InlineData(StatementColumn.CpuPerExecution, 625.0)] // milliseconds per execution
    [InlineData(StatementColumn.BufferGetsPerExecution, 2.5)]
    [InlineData(StatementColumn.UserIoWait, 0.001)]
    public void Times_are_seconds_and_per_execution_milliseconds(StatementColumn column, double expected) =>
        Assert.Equal((decimal)expected, ServerLoadExport.ValueOf(Row, column));

    [Theory]
    [InlineData('x', 1000, true)]
    [InlineData('x', 996, false)]
    [InlineData('ä', 511, true)] // measured: SQL_TEXT keeps 1000 bytes, 511 characters with umlauts in AL32UTF8
    [InlineData('ä', 400, false)]
    [InlineData('€', 333, true)] // three bytes each
    public void A_text_may_be_cut_from_a_thousand_characters_or_nearly_a_thousand_bytes(char letter, int count, bool mayBeCut) =>
        Assert.Equal(mayBeCut, StatementInfo.MayBeCutText(new string(letter, count)));

    [Fact]
    public void A_text_that_may_be_cut_ends_in_an_ellipsis() =>
        Assert.Equal(new string('x', 1000) + " …",
            ServerLoadExport.ValueOf(Row with { Info = Row.Info! with { Text = new string('x', 1000) } }, StatementColumn.Text));

    [Fact]
    public async Task The_file_has_a_sheet_with_a_heading_per_column_and_a_row_per_statement()
    {
        using var stream = new MemoryStream();

        var summary = await ServerLoadExport.WriteXlsxAsync(stream, [Row, Row with { SqlId = "def456", Info = null }], c => $"H-{c}", "Top statements",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.Rows);
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.All(Enum.GetValues<StatementColumn>(), c => Assert.Contains($"H-{c}", sheet, StringComparison.Ordinal));
        Assert.Contains("SELECT 1 FROM dual", sheet, StringComparison.Ordinal);
        Assert.Contains("def456", sheet, StringComparison.Ordinal);
    }
}
