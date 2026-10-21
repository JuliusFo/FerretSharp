using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Forms;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Tests.ClrModel;

namespace FerretSharp.Core.Tests.Forms;

public sealed class RowComparisonTests
{
    private static readonly TableDetails Kunden = CodeGenerationModel.Kunden;

    private static RowData Row(decimal id, string name, decimal art, byte[]? extern_ = null, string? notiz = "Notiz") =>
        new(new RowKey.PrimaryKey([id]),
            [id, name, new DateTime(2026, 10, 8), null, "J", art, null, null, null, extern_, null, notiz is null ? null : new LobValue(notiz, notiz.Length)]);

    private static CompareField Field(IReadOnlyList<CompareField> fields, string column) => fields.Single(f => f.Info.Name == column);

    [Fact]
    public async Task Columns_differ_when_any_row_holds_another_raw_value()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();
        FormRow[] rows =
        [
            new(Row(8, "Meier", 2, [1, 2]), null),
            new(Row(15, "Meier", 1, [1, 2]), null),
            new(Row(22, "Meyer", 2, [1, 2]), null),
        ];

        var fields = RowComparison.Compare(Kunden, presentation, rows, []);

        Assert.True(Field(fields, "KUNDE_ID").Differs);
        Assert.True(Field(fields, "NAME").Differs);
        Assert.True(Field(fields, "KUNDENART").Differs);
        Assert.Equal(["Gewerbe (2)", "Privat (1)", "Gewerbe (2)"], Field(fields, "KUNDENART").Cells.Select(c => c.Value.Text));
        // Only the odd one out is marked; all different: everything but the first row.
        Assert.Equal([false, true, false], Field(fields, "KUNDENART").Cells.Select(c => c.Deviates));
        Assert.Equal([false, false, true], Field(fields, "NAME").Cells.Select(c => c.Deviates));
        Assert.Equal([false, true, true], Field(fields, "KUNDE_ID").Cells.Select(c => c.Deviates));
        Assert.All(Field(fields, "ERSTELLT_AM").Cells, c => Assert.False(c.Deviates));
        Assert.False(Field(fields, "ERSTELLT_AM").Differs);
        Assert.False(Field(fields, "EXTERN_ID").Differs); // RAW by content, every read is a new array
        Assert.False(Field(fields, "NOTIZ").Differs); // LOBs by preview and length
        Assert.False(Field(fields, "STATUS").Differs); // NULL equals NULL here
        Assert.True(Field(fields, "STATUS").AllNull);
    }

    [Fact]
    public void Pending_changes_are_compared_as_the_grid_shows_them()
    {
        var tracker = new ChangeTracker(Kunden);
        var first = Row(8, "Meier", 2);
        var second = Row(15, "Meier", 2);
        tracker.SetValue(second, CodeGenerationModel.Index("NAME"), "Meyer");

        var name = RowComparison.Compare(Kunden, TablePresentation.Plain(Kunden), [new(first, null), new(second, tracker.Find(second.Key))], [])
            .Single(f => f.Info.Name == "NAME");

        Assert.True(name.Differs);
        Assert.Equal([null, ChangeStage.Pending], name.Cells.Select(c => c.Stage));
        Assert.Equal("Meyer", name.Cells[1].Value.Text);
    }

    [Fact]
    public void Result_rows_are_compared_in_the_given_column_order()
    {
        // WP-56: a query result keeps its own column order (no key, nothing pinned).
        var result = new TableDetails(new TableSummary("", "Ergebnis", TableKind.View), [Kunden.Columns[1], Kunden.Columns[0]], [], [], false);
        FormRow[] rows = [new(new RowData(RowKey.None.Instance, ["Meier", 8m]), null), new(new RowData(RowKey.None.Instance, ["Meier", 15m]), null)];

        var fields = RowComparison.Compare(TablePresentation.Plain(result), rows, [1, 0]);

        Assert.Equal(["KUNDE_ID", "NAME"], fields.Select(f => f.Info.Name));
        Assert.Equal([true, false], fields.Select(f => f.Differs));
        Assert.Equal([false, true], fields[0].Cells.Select(c => c.Deviates));
    }

    [Fact]
    public async Task Only_differences_and_hiding_columns_null_everywhere()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();
        var fields = RowComparison.Compare(Kunden, presentation, [new(Row(8, "Meier", 2, notiz: null), null), new(Row(15, "Meyer", 2), null)], []);

        var differences = RowComparison.Visible(fields, presentation, new CompareFilter(OnlyDifferences: true));
        Assert.Equal(["KUNDE_ID", "NAME", "NOTIZ"], differences.Fields.Select(f => f.Info.Name));

        // NULL in both rows: UMSATZ, STATUS, LEGACY_CODE, MANDANT_ID, EXTERN_ID, GEPRUEFT. NOTIZ is NULL in one row only.
        var withoutEmpty = RowComparison.Visible(fields, presentation, new CompareFilter(HideEmpty: true));
        Assert.Equal(6, withoutEmpty.EmptyHidden);
        Assert.Contains(withoutEmpty.Fields, f => f.Info.Name == "NOTIZ");

        Assert.Equal(["NAME"], RowComparison.Visible(fields, presentation, new CompareFilter("name", OnlyDifferences: true)).Fields.Select(f => f.Info.Name));
    }
}
