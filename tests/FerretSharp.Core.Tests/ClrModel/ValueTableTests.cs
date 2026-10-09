using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class ValueTableTests
{
    private static readonly ColumnInfo Number2 = new("KUNDENART", "NUMBER", null, false, 2, 0, false, false, null, 1);
    private static readonly ColumnInfo Status = new("STATUS", "VARCHAR2", 20, true, null, null, false, false, null, 2);
    private static readonly ColumnInfo JaNein = new("GESPERRT", "CHAR", 1, false, null, null, false, false, null, 3);

    private static PropertyExport Enum(string type, params ValueMapping[] values) =>
        new("P", type, "Shop." + type, false, false, "C", null, null, null, false, values);

    private static readonly PropertyExport Kundenart = Enum("Kundenart",
        new("Privat", "1", "1"), new("Gewerbe", "2", "2"), new("Behoerde", "3", "3"));

    private static readonly PropertyExport AuftragStatus = Enum("AuftragStatus",
        new("Offen", "0", "OFFEN"), new("Versandt", "1", "VERSANDT")) with { Converter = "UpperCaseEnumConverter<AuftragStatus>" };

    private static readonly PropertyExport Gesperrt = new("Gesperrt", "bool", "System.Boolean", false, false, "GESPERRT", null,
        "JaNeinConverter", "string", false, [new("false", "False", "N"), new("true", "True", "J")]);

    [Fact]
    public void Enum_values_show_name_and_database_value_numbers_compare_by_value()
    {
        var table = ValueTable.For(Kundenart, Number2)!;

        Assert.Equal(new PresentedValue("Gewerbe (2)"), table.Present(2m));
        Assert.Equal("Gewerbe", table.Find(2.0m)?.Name);
        Assert.Equal(new PresentedValue("Privat (1)"), table.Present(1));
        Assert.Equal(new PresentedValue(null), table.Present(null));
        Assert.Equal(new PresentedValue("7", Unknown: true), table.Present(7m));
        Assert.Equal("Kein Wert von Kundenart", table.UnknownText);
    }

    [Fact]
    public void Options_carry_the_database_value_as_filter_and_edit_text()
    {
        Assert.Equal(
            [new ValueOption("Privat", "1", "Privat (1)"), new ValueOption("Gewerbe", "2", "Gewerbe (2)"), new ValueOption("Behoerde", "3", "Behoerde (3)")],
            ValueTable.For(Kundenart, Number2)!.Options);
        Assert.Equal(
            [new ValueOption("Offen", "OFFEN", "Offen (OFFEN)"), new ValueOption("Versandt", "VERSANDT", "Versandt (VERSANDT)")],
            ValueTable.For(AuftragStatus, Status)!.Options);
    }

    [Theory]
    [InlineData("2", "Gewerbe")]
    [InlineData("2,0", "Gewerbe")]
    [InlineData("4", null)]
    [InlineData("x", null)]
    public void Filter_texts_find_their_member(string text, string? expected) =>
        Assert.Equal(expected, ValueTable.For(Kundenart, Number2)!.FindOption(text)?.Name);

    [Fact]
    public void Converted_bools_show_true_and_false_char_padding_is_ignored()
    {
        var table = ValueTable.For(Gesperrt, JaNein)!;

        Assert.True(table.IsBool);
        Assert.Equal(new PresentedValue("true"), table.Present("J"));
        Assert.Equal(new PresentedValue("false"), table.Present("N "));
        Assert.Equal(new PresentedValue("X", Unknown: true), table.Present("X"));
        Assert.Equal([new ValueOption("false", "N", "false"), new ValueOption("true", "J", "true")], table.Options);
    }

    private static readonly PropertyExport Rechte = Enum("Rechte",
            new("Keine", "0", "0"), new("Lesen", "1", "1"), new("Schreiben", "2", "2"), new("LesenUndSchreiben", "3", "3"),
            new("Loeschen", "4", "4", "Löschen"), new("Alle", "7", "7"))
        with { IsFlagsEnum = true };

    [Fact]
    public void Flags_values_list_every_flag_set_also_for_a_value_with_a_member_of_its_own()
    {
        var table = ValueTable.For(Rechte, Number2)!;

        Assert.Equal(new PresentedValue("Lesen, Schreiben (3)", Tooltip: "Lesen (1)\nSchreiben (2)"), table.Present(3m));
        Assert.Equal(new PresentedValue("Lesen, Schreiben, Löschen (7)", Tooltip: "Lesen (1)\nSchreiben (2)\nLöschen (4) · Rechte.Loeschen"),
            table.Present(7m));
        Assert.Equal(new PresentedValue("Schreiben (2)", Tooltip: "Schreiben (2)"), table.Present(2m));
        Assert.Equal(new PresentedValue("Keine (0)"), table.Present(0m));
    }

    [Fact]
    public void Flags_bits_without_a_member_are_listed_and_marked()
    {
        var table = ValueTable.For(Rechte, Number2)!;

        Assert.Equal(new PresentedValue("Lesen, 8 (9)", Unknown: true, Tooltip: "Lesen (1)\n8 (kein Member)"), table.Present(9m));
        Assert.Equal(new PresentedValue("8", Unknown: true), table.Present(8m));
        Assert.Equal(new PresentedValue("-1", Unknown: true), table.Present(-1m));
        Assert.True(table.Present(1.5m).Unknown);
    }

    [Fact]
    public void Flags_are_the_members_that_combine_no_others()
    {
        var table = ValueTable.For(Rechte, Number2)!;

        Assert.Equal(["Lesen", "Schreiben", "Löschen"], table.Flags!.Select(f => f.Name));
        Assert.Equal(["1", "2", "4"], table.Flags!.Select(f => f.Value));
        Assert.Equal(["Lesen", "Löschen"], table.FlagsOfText("5")!.Flags.Select(f => f.Name));
        Assert.Equal(0, table.FlagsOfText("5,0")!.Rest);
        Assert.Null(table.FlagsOfText("x"));
        // C# literals still take a member of its own (Rechte.Alle).
        Assert.Equal(["Alle"], table.FlagMembers(7m));
    }

    [Theory]
    [InlineData("1", "2", true, "3")]
    [InlineData("3", "2", false, "1")]
    [InlineData("", "4", true, "4")]
    [InlineData("9", "8", false, "1")]
    public void Ticking_a_flag_changes_the_edit_text(string text, string flag, bool set, string expected) =>
        Assert.Equal(expected, ValueTable.For(Rechte, Number2)!.WithFlag(text, flag, set));

    [Fact]
    public void Flags_without_a_member_for_zero_show_zero_as_valid()
    {
        var table = ValueTable.For(Enum("Optionen", new("A", "1", "1"), new("B", "2", "2")) with { IsFlagsEnum = true }, Number2)!;

        Assert.Equal(new PresentedValue("0"), table.Present(0m));
        Assert.Equal(new PresentedValue("A, B (3)", Tooltip: "A (1)\nB (2)"), table.Present(3m));
    }

    [Fact]
    public void Flags_stored_through_a_converter_or_as_text_are_no_flags()
    {
        var converted = Enum("Rechte", new("Lesen", "1", "L"), new("Schreiben", "2", "S")) with { IsFlagsEnum = true };
        Assert.Null(ValueTable.For(converted, Status)!.Flags);
        Assert.Null(ValueTable.For(Rechte, Status)!.Flags);
        Assert.Null(ValueTable.For(Kundenart, Number2)!.Flags);
    }

    [Fact]
    public void Properties_without_values_have_no_table()
    {
        Assert.Null(ValueTable.For(null, Number2));
        Assert.Null(ValueTable.For(Enum("int"), Number2));
        // A member the converter maps to NULL cannot be picked; with no member left there is no table.
        Assert.Null(ValueTable.For(Enum("Leer", new ValueMapping("Nichts", "0", null)), Number2));
    }
}

public sealed class TablePresentationTests
{
    private const string Owner = "APP";

    private static readonly TableDetails Kunden = new(new TableSummary(Owner, "KUNDEN", TableKind.Table),
        [
            new("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
            new("KUNDENART", "NUMBER", null, false, 2, 0, false, false, null, 2),
            new("ANZAHL", "NUMBER", null, false, null, 0, true, false, null, 3),
        ],
        ["KUNDE_ID"], [], false);

    private static async Task<ClrModelMapping> MappingAsync()
    {
        var reader = NSubstitute.Substitute.For<ISchemaReader>();
        NSubstitute.SubstituteExtensions.Returns(reader.GetTablesAsync(Owner, NSubstitute.Arg.Any<CancellationToken>()),
            (IReadOnlyList<TableSummary>)[Kunden.Table]);
        NSubstitute.SubstituteExtensions.Returns(reader.GetSynonymTargetsAsync(Owner, NSubstitute.Arg.Any<CancellationToken>()),
            FerretSharp.Core.Schema.SynonymTargets.None);
        NSubstitute.SubstituteExtensions.Returns(reader.GetForeignKeysAsync(Owner, NSubstitute.Arg.Any<CancellationToken>()),
            (IReadOnlyList<ForeignKeyInfo>)[]);
        NSubstitute.SubstituteExtensions.Returns(reader.GetColumnsAsync(Owner, NSubstitute.Arg.Any<CancellationToken>()),
            (IReadOnlyDictionary<string, IReadOnlyList<ColumnInfo>>)new Dictionary<string, IReadOnlyList<ColumnInfo>> { ["KUNDEN"] = Kunden.Columns });
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(TestContext.Current.CancellationToken);
        var entity = new EntityExport("Shop.Kunde", "Shop.Kunde", false, null, "KUNDEN", null, null, null,
            [
                new("KundeId", "int", "System.Int32", false, false, "KUNDE_ID", null, null, null, false, null),
                new("Kundenart", "Kundenart", "Shop.Kundenart", true, false, "KUNDENART", null, null, "int", false,
                    [new("Privat", "1", "1"), new("Gewerbe", "2", "2")]),
            ],
            ["KundeId"], []);
        return await ClrModelMapping.BuildAsync(new ModelExport(1, "8.0.0", "Shop.Ctx", "options", null, [entity]), schema, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Labels_follow_the_setting()
    {
        var mapping = await MappingAsync();

        var beside = TablePresentation.Create(Kunden, mapping, ClrNameDisplay.Beside);
        Assert.Equal("Kunde", beside.EntityName);
        Assert.Equal(new ColumnLabel("KUNDE_ID", "KundeId", "int"), beside.LabelOf(0));
        Assert.Equal(new ColumnLabel("KUNDENART", "Kundenart", "Kundenart?"), beside.LabelOf(1));
        Assert.Equal(new ColumnLabel("ANZAHL", null, null), beside.LabelOf(2)); // no property
        Assert.Equal("KundeId", beside.SearchNameOf(0));

        Assert.Equal(new ColumnLabel("KundeId", "KUNDE_ID", "int"), TablePresentation.Create(Kunden, mapping, ClrNameDisplay.Front).LabelOf(0));

        var off = TablePresentation.Create(Kunden, mapping, ClrNameDisplay.Off);
        Assert.False(off.ShowsClrNames);
        Assert.Equal(new ColumnLabel("KUNDE_ID", null, null), off.LabelOf(0));
        Assert.Null(off.SearchNameOf(0));
    }

    [Fact]
    public async Task Values_show_enum_members_whatever_the_name_setting()
    {
        var mapping = await MappingAsync();

        foreach (var names in Enum.GetValues<ClrNameDisplay>())
        {
            var presentation = TablePresentation.Create(Kunden, mapping, names);
            Assert.Equal("Gewerbe (2)", presentation.Present(1, 2m).Text);
            Assert.Equal("4.711", presentation.Present(0, 4711m).Text);
            Assert.Equal("Gewerbe", presentation.FilterValueText("KUNDENART", "2"));
            Assert.Equal("4711", presentation.FilterValueText("KUNDE_ID", "4711"));
        }
    }

    [Fact]
    public void Without_a_model_everything_is_the_database_view()
    {
        var plain = TablePresentation.Plain(Kunden);

        Assert.Null(plain.EntityName);
        Assert.Equal(new ColumnLabel("KUNDENART", null, null), plain.LabelOf(1));
        Assert.Equal(new PresentedValue("2"), plain.Present(1, 2m));
        Assert.Null(plain.ValuesOf(1));
        Assert.Equal(CellFormatter.Format(Kunden.Columns[0], 4711m), plain.Present(0, 4711m).Text);
    }
}

public sealed class ValueTableDisplayNameTests
{
    private static readonly ColumnInfo Art = new("ART", "NUMBER", null, false, 2, 0, false, false, null, 1);

    private static readonly PropertyExport Auftragsart = new("Art", "Auftragsart", "Shop.Auftragsart", false, false, "ART", null, null, null, false,
        [new("ProductionOrder", "0", "0", "Fertigungsauftrag"), new("Service", "1", "1")]);

    [Fact]
    public void The_display_text_replaces_the_member_name_which_stays_in_the_tooltip()
    {
        var table = ValueTable.For(Auftragsart, Art)!;

        Assert.Equal(new PresentedValue("Fertigungsauftrag (0)", Tooltip: "Auftragsart.ProductionOrder"), table.Present(0m));
        Assert.Equal(new PresentedValue("Service (1)"), table.Present(1m)); // no [Display]: the member name, no tooltip
    }

    [Fact]
    public void Options_show_the_display_text_and_keep_the_member_and_the_stored_value()
    {
        var table = ValueTable.For(Auftragsart, Art)!;

        Assert.Equal(
            [new ValueOption("Fertigungsauftrag", "0", "Fertigungsauftrag (0)", "ProductionOrder"), new ValueOption("Service", "1", "Service (1)")],
            table.Options);
        Assert.Equal("Fertigungsauftrag", table.FindOption("0")?.Name);
        Assert.Equal("0", table.OptionOf(0m)?.Value);
        Assert.Null(table.OptionOf(5m));
    }
}
