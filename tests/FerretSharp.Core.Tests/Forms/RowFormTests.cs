using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Forms;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Tests.ClrModel;

namespace FerretSharp.Core.Tests.Forms;

public sealed class RowFormTests
{
    private static readonly TableDetails Kunden = CodeGenerationModel.Kunden;

    private static readonly ForeignKeyInfo ToMandant = new(
        "FK_KUNDEN_MANDANT", Kunden.Table.Ref, ["MANDANT_ID"], new TableRef(CodeGenerationModel.Owner, "MANDANTEN"), ["ID"], FkSource.Declared);

    private static readonly ForeignKeyInfo FromAuftrag = new(
        "FK_AUFTRAG_KUNDE", new TableRef(CodeGenerationModel.Owner, "AUFTRAG"), ["KUNDE_ID"], Kunden.Table.Ref, ["KUNDE_ID"], FkSource.Declared);

    private static int I(string column) => CodeGenerationModel.Index(column);

    private static RowData Row(decimal id, string name, decimal? mandant = null, string? status = "AKTIV") =>
        new(new RowKey.PrimaryKey([id]),
            [id, name, new DateTime(2026, 10, 8), 1234.5m, "J", 2m, status, null, mandant, null, null, new LobValue("Notiz", 5)]);

    private static IReadOnlyList<FormField> Fields(
        TablePresentation presentation, FormRow row, bool writable = true, IReadOnlyList<string>? pinned = null, TableDetails? table = null) =>
        RowForm.Fields(table ?? Kunden, presentation, row, writable, [ToMandant], null, pinned ?? []);

    private static FormField Field(IReadOnlyList<FormField> fields, string column) => fields.Single(f => f.Info.Name == column);

    [Fact]
    public async Task Fields_follow_the_grid_order_with_labels_values_and_edit_texts()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();

        var fields = Fields(presentation, new FormRow(Row(8, "Meier"), null), pinned: ["UMSATZ"]);

        Assert.Equal(["KUNDE_ID", "UMSATZ", "NAME", "ERSTELLT_AM"], fields.Take(4).Select(f => f.Info.Name));
        Assert.Equal(Kunden.Columns.Count, fields.Count);
        var name = Field(fields, "NAME");
        Assert.Equal(new ColumnLabel("NAME", "Name", "string"), name.Label);
        Assert.Equal("Meier", name.Value.Text);
        Assert.Equal("Meier", name.EditText);
        Assert.True(Field(fields, "KUNDE_ID").IsPrimaryKey);
        Assert.Equal("1234,5", Field(fields, "UMSATZ").EditText);

        // Enum and converted bool: the member by name, the editor starts with the database value of the member.
        var art = Field(fields, "KUNDENART");
        Assert.Equal("Gewerbe (2)", art.Value.Text);
        Assert.Equal("2", art.EditText);
        Assert.NotNull(art.Options);
        Assert.Equal("true", Field(fields, "GESPERRT").Value.Text);
        Assert.Equal("J", Field(fields, "GESPERRT").EditText);

        var legacy = Field(fields, "LEGACY_CODE");
        Assert.True(legacy.IsNull);
        Assert.Null(legacy.Value.Text);
        Assert.Equal("", legacy.EditText);
        Assert.True(Field(fields, "NOTIZ").IsLob);
        Assert.Null(Field(fields, "NOTIZ").ReadOnlyReason); // changed in the LOB editor
        Assert.All(fields, f => Assert.Empty(f.Mismatches)); // no model given
    }

    [Fact]
    public void Without_a_model_the_plain_database_view_is_shown()
    {
        var fields = Fields(TablePresentation.Plain(Kunden), new FormRow(Row(8, "Meier"), null));

        Assert.Equal(new ColumnLabel("KUNDENART", null, null), Field(fields, "KUNDENART").Label);
        Assert.Equal("2", Field(fields, "KUNDENART").Value.Text);
        Assert.Null(Field(fields, "KUNDENART").Options);
    }

    [Fact]
    public async Task Model_mismatches_come_from_the_mapping_per_column()
    {
        var mapping = await CodeGenerationModel.MappingAsync();
        var presentation = TablePresentation.Create(Kunden, mapping, ClrNameDisplay.Beside);

        var fields = RowForm.Fields(Kunden, presentation, new FormRow(Row(8, "Meier"), null), true, [], mapping, []);

        Assert.All(fields, f => Assert.Equal(mapping.MismatchesOf(Kunden.Table.Ref, f.Info.Name), f.Mismatches));
    }

    [Fact]
    public void Read_only_reasons_follow_the_grid()
    {
        var presentation = TablePresentation.Plain(Kunden);
        var row = Row(8, "Meier");

        var writable = Fields(presentation, new FormRow(row, null));
        Assert.Contains("Primärschlüssel", Field(writable, "KUNDE_ID").ReadOnlyReason);
        Assert.True(Field(writable, "NAME").IsEditable);

        var locked = Fields(presentation, new FormRow(row, null), writable: false);
        Assert.Contains("schreibgeschützt", Field(locked, "NAME").ReadOnlyReason);
        Assert.Contains("Primärschlüssel", Field(locked, "KUNDE_ID").ReadOnlyReason); // the table's own reason first

        var tracker = new ChangeTracker(Kunden);
        tracker.Delete(row);
        var deleted = Fields(presentation, new FormRow(row, tracker.Find(row.Key)));
        Assert.Equal("Die Zeile ist zum Löschen markiert.", Field(deleted, "NAME").ReadOnlyReason);

        var added = Fields(presentation, new FormRow(null, tracker.AddRow()));
        Assert.True(Field(added, "KUNDE_ID").IsEditable); // keys are filled in new rows
        Assert.True(Field(added, "KUNDE_ID").IsNull);

        var big = Row(8, "Meier") with { Values = [.. row.Values.Take(3), new BigNumber("1" + new string('0', 30)), .. row.Values.Skip(4)] };
        Assert.Contains("28 Stellen", Field(Fields(presentation, new FormRow(big, null)), "UMSATZ").ReadOnlyReason);

        var view = CodeGenerationModel.View;
        var viewField = Assert.Single(RowForm.Fields(view, TablePresentation.Plain(view), new FormRow(new RowData(RowKey.None.Instance, [8m]), null), true, [], null, []));
        Assert.Contains("Views", viewField.ReadOnlyReason);
    }

    [Fact]
    public void Changed_cells_show_their_current_value_and_stage()
    {
        var tracker = new ChangeTracker(Kunden);
        var row = Row(8, "Meier");
        Assert.True(tracker.SetValue(row, I("NAME"), "Müller").IsValid);

        var fields = Fields(TablePresentation.Plain(Kunden), new FormRow(row, tracker.Find(row.Key)));

        var name = Field(fields, "NAME");
        Assert.Equal(ChangeStage.Pending, name.Stage);
        Assert.Equal("Müller", name.Raw);
        Assert.Equal("Müller", name.Value.Text);
        Assert.Null(Field(fields, "UMSATZ").Stage);
    }

    [Fact]
    public void Foreign_keys_jump_from_the_current_value_also_a_pending_one()
    {
        var presentation = TablePresentation.Plain(Kunden);
        var row = Row(8, "Meier", mandant: 3);

        var jump = Assert.Single(Field(Fields(presentation, new FormRow(row, null)), "MANDANT_ID").Jumps);
        Assert.Equal(new TableRef(CodeGenerationModel.Owner, "MANDANTEN"), jump.Table);
        Assert.Equal("ID = 3", jump.Condition);
        Assert.Empty(Field(Fields(presentation, new FormRow(row, null)), "NAME").Jumps);

        var tracker = new ChangeTracker(Kunden);
        tracker.SetValue(row, I("MANDANT_ID"), "5");
        var changed = Assert.Single(Field(Fields(presentation, new FormRow(row, tracker.Find(row.Key))), "MANDANT_ID").Jumps);
        Assert.Equal("ID = 5", changed.Condition);

        var empty = Assert.Single(Field(Fields(presentation, new FormRow(Row(9, "Leer"), null)), "MANDANT_ID").Jumps);
        Assert.False(empty.IsAvailable);
        Assert.Equal("MANDANT_ID ist NULL.", empty.Unavailable);
    }

    [Fact]
    public void Incoming_jumps_exist_for_loaded_rows_only()
    {
        var tracker = new ChangeTracker(Kunden);

        var incoming = Assert.Single(RowForm.Incoming(Kunden, new FormRow(Row(8, "Meier"), null), [FromAuftrag]));
        Assert.Equal(JumpDirection.Incoming, incoming.Direction);
        Assert.Equal("KUNDE_ID = 8", incoming.Condition);
        Assert.Empty(RowForm.Incoming(Kunden, new FormRow(null, tracker.AddRow()), [FromAuftrag]));
    }

    [Fact]
    public void The_key_text_names_the_primary_key_values()
    {
        Assert.Equal("8", new FormRow(Row(8, "Meier"), null).KeyText(Kunden));
        Assert.Null(new FormRow(null, new ChangeTracker(Kunden).AddRow()).KeyText(Kunden));
        Assert.Null(new FormRow(new RowData(RowKey.None.Instance, [8m]), null).KeyText(CodeGenerationModel.View));
    }

    [Fact]
    public async Task Search_and_switches_choose_the_shown_fields()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();
        var tracker = new ChangeTracker(Kunden);
        var row = Row(8, "Meier", status: null);
        tracker.SetValue(row, I("UMSATZ"), "");
        var fields = Fields(presentation, new FormRow(row, tracker.Find(row.Key)));

        var all = RowForm.Visible(fields, presentation, new FormFilter());
        Assert.Equal(fields, all.Fields);
        Assert.Equal(0, all.EmptyHidden);

        var found = RowForm.Visible(fields, presentation, new FormFilter("erstellt"));
        Assert.Equal(["ERSTELLT_AM"], found.Fields.Select(f => f.Info.Name));
        Assert.Equal(Kunden.Columns.Count, found.Total);

        // NULL: STATUS, LEGACY_CODE, MANDANT_ID, EXTERN_ID, GEPRUEFT; UMSATZ was emptied by the user and stays.
        var withoutEmpty = RowForm.Visible(fields, presentation, new FormFilter(HideEmpty: true));
        Assert.Equal(5, withoutEmpty.EmptyHidden);
        Assert.Contains(withoutEmpty.Fields, f => f.Info.Name == "UMSATZ");
        Assert.DoesNotContain(withoutEmpty.Fields, f => f.Info.Name == "STATUS");

        var changed = RowForm.Visible(fields, presentation, new FormFilter(OnlyChanged: true));
        Assert.Equal(["UMSATZ"], changed.Fields.Select(f => f.Info.Name));

        Assert.Empty(RowForm.Visible(fields, presentation, new FormFilter("gibtsnicht")).Fields);
    }
}

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
