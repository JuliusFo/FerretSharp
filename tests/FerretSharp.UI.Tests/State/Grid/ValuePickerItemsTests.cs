using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-12: the entries of the value picker in the filter bar, also for values the C# model does not know.</summary>
public class ValuePickerItemsTests
{
    private static readonly ColumnInfo Number2 = new("TASKTYP", "NUMBER", null, false, 2, 0, false, false, null, 1);
    private static readonly ColumnInfo Status = new("STATUS", "VARCHAR2", 20, true, null, null, false, false, null, 2);
    private static readonly ColumnInfo JaNein = new("GESPERRT", "CHAR", 1, false, null, null, false, false, null, 3);

    private static PropertyExport Enum(string type, params ValueMapping[] values) =>
        new("P", type, false, false, "C", null, null, null, false, values);

    private static readonly ValueTable TaskTyp = ValueTable.For(Enum("TaskTyp",
        new("Unknown", "0", "0"), new("GoodsMovement", "2", "2"), new("HandlingUnit", "3", "3", "Handling unit"), new("QueueAbort", "13", "13")), Number2)!;

    private static readonly ValueTable AuftragStatus = ValueTable.For(Enum("AuftragStatus",
        new("Offen", "0", "OFFEN"), new("Versandt", "1", "VERSANDT")) with { Converter = "UpperCaseEnumConverter<AuftragStatus>" }, Status)!;

    private static readonly ValueTable Gesperrt = ValueTable.For(new PropertyExport("Gesperrt", "bool", false, false, "GESPERRT", null,
        "JaNeinConverter", "string", false, [new("false", "False", "N"), new("true", "True", "J")]), JaNein)!;

    private static List<string> Values(IReadOnlyList<ValuePickerItem> items) => [.. items.Select(i => i.Value)];

    [Fact]
    public void Without_text_the_members_come_first_then_selected_values_without_a_member()
    {
        var items = ValuePickerItems.Of(TaskTyp, ["2", "7"], "");

        Assert.Equal(["0", "2", "3", "13", "7"], Values(items));
        Assert.Equal(new ValuePickerItem("7", "7", "kein Member", true), items[^1]);
        Assert.Equal("HandlingUnit · 3", items[2].Detail);
    }

    [Fact]
    public void Text_filters_by_name_member_and_value_ignoring_case()
    {
        Assert.Equal(["2"], Values(ValuePickerItems.Of(TaskTyp, [], "MOVE")));
        Assert.Equal(["3"], Values(ValuePickerItems.Of(TaskTyp, [], "unit")));
        Assert.Equal(["3"], Values(ValuePickerItems.Of(TaskTyp, [], "handlingunit")));
        Assert.Equal(["13"], Values(ValuePickerItems.Of(TaskTyp, [], "13")));
    }

    [Fact]
    public void A_typed_number_without_a_member_comes_first_as_its_own_entry()
    {
        var items = ValuePickerItems.Of(TaskTyp, [], "1");

        Assert.Equal(new ValuePickerItem("1", "1", "kein Member", true), items[0]);
        Assert.Equal(["1", "13"], Values(items));
        Assert.Equal(["7"], Values(ValuePickerItems.Of(TaskTyp, [], "7,0")));
    }

    [Theory]
    [InlineData("2")] // a member's value
    [InlineData("x")] // no number
    [InlineData("queueabort")] // a member's name
    public void Typed_text_that_is_no_value_of_its_own_gets_no_entry(string typed) =>
        Assert.DoesNotContain(ValuePickerItems.Of(TaskTyp, [], typed), i => i.Unknown);

    [Fact]
    public void A_typed_value_already_selected_is_not_offered_twice()
    {
        var items = ValuePickerItems.Of(TaskTyp, ["1"], "1");

        Assert.Single(items, i => i.Value == "1");
    }

    [Fact]
    public void Text_columns_take_any_value_but_a_member_name_means_the_member()
    {
        Assert.Equal(["STORNIERT"], Values(ValuePickerItems.Of(AuftragStatus, [], "STORNIERT")));
        Assert.Equal(["OFFEN"], Values(ValuePickerItems.Of(AuftragStatus, [], "offen")));
    }

    [Fact]
    public void In_a_text_column_the_members_found_come_before_the_typed_text()
    {
        // Review: Enter took the highlighted first entry, the search text "vers", and the filter found nothing.
        var items = ValuePickerItems.Of(AuftragStatus, [], "vers");

        Assert.Equal(["VERSANDT", "vers"], Values(items));
        Assert.True(items[1].Unknown);
    }

    [Fact]
    public void A_members_value_in_another_case_comes_first_the_typed_value_stays_pickable_after_it()
    {
        // Review N1: "j" in a J/N column is a value of its own (red in the grid) and must stay filterable.
        var items = ValuePickerItems.Of(Gesperrt, [], "j");

        Assert.Equal(["J", "j"], Values(items));
        Assert.True(items[1].Unknown);
        Assert.Equal(["J"], Values(ValuePickerItems.Of(Gesperrt, [], "J")));
        Assert.Equal(["VERSANDT"], Values(ValuePickerItems.Of(AuftragStatus, [], "versandt"))); // a member's name
    }

    [Fact]
    public void A_value_written_another_way_finds_its_entry()
    {
        Assert.Equal(["2"], Values(ValuePickerItems.Of(TaskTyp, [], "2,0")));
        Assert.Equal(["1"], Values(ValuePickerItems.Of(TaskTyp, ["1"], "1,0")));
    }

    [Fact]
    public void A_typed_text_with_the_list_separator_gets_no_entry() =>
        Assert.DoesNotContain(ValuePickerItems.Of(AuftragStatus, [], "A;B"), i => i.Unknown);
}
