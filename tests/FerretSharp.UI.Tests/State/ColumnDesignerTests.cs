using FerretSharp.Core.Design;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The edit mode of the "Columns" view (WP-23): fields from and to the dictionary's columns, and the draft they make.</summary>
public sealed class ColumnDesignerTests
{
    private static ColumnInfo Col(string name, string type, int? length = null, bool charSemantics = false, int? precision = null, int? scale = null,
        bool nullable = true, string? @default = null, string? comment = null, bool identity = false) =>
        new(name, type, length, charSemantics, precision, scale, nullable, identity, @default, 1, comment);

    private static readonly TableDetails Kunde = new(new TableSummary("SHOP", "KUNDE", TableKind.Table),
    [
        Col("ID", "NUMBER", precision: 10, scale: 0, nullable: false),
        Col("NAME", "VARCHAR2", 50, charSemantics: true, nullable: false, comment: "Name"),
        Col("KUERZEL", "CHAR", 3, charSemantics: true),
        Col("BETRAG", "NUMBER", precision: 12, scale: 2, @default: "0 "),
    ], ["ID"], [], false);

    /// <summary>Every kind of type comes back from the fields as it went in: an untouched column is no change.</summary>
    [Theory]
    [InlineData("VARCHAR2", 50, true, null, null)]
    [InlineData("VARCHAR2", 50, false, null, null)]
    [InlineData("NVARCHAR2", 20, true, null, null)]
    [InlineData("CHAR", 1, false, null, null)]
    [InlineData("RAW", 16, false, null, null)]
    [InlineData("NUMBER", null, false, 12, 2)]
    [InlineData("NUMBER", null, false, 10, 0)]
    [InlineData("NUMBER", null, false, null, 0)]
    [InlineData("NUMBER", null, false, null, null)]
    [InlineData("FLOAT", null, false, 126, null)]
    [InlineData("DATE", null, false, null, null)]
    [InlineData("TIMESTAMP(6)", null, false, null, 6)]
    [InlineData("TIMESTAMP(3) WITH TIME ZONE", null, false, null, 3)]
    [InlineData("TIMESTAMP(9) WITH LOCAL TIME ZONE", null, false, null, 9)]
    [InlineData("CLOB", null, false, null, null)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", null, false, 2, 6)]
    public void Fields_round_trip_every_type(string type, int? length, bool charSemantics, int? precision, int? scale)
    {
        var column = Col("C", type, length, charSemantics, precision, scale);

        var row = DesignColumn.Of(column);
        var (back, problem) = row.ToColumn();

        Assert.Null(problem);
        Assert.Equal(OracleTypes.DdlType(column), OracleTypes.DdlType(back!));
        Assert.False(row.IsChanged);
    }

    [Fact]
    public void A_timestamp_shows_its_fractional_digits_as_size()
    {
        var row = DesignColumn.Of(Col("T", "TIMESTAMP(3) WITH TIME ZONE"));

        Assert.Equal(("TIMESTAMP WITH TIME ZONE", "3"), (row.Type, row.Size));
        row.Size = "6";
        Assert.Equal("TIMESTAMP(6) WITH TIME ZONE", row.ToColumn().Column!.DataType);
    }

    [Fact]
    public void An_untouched_designer_has_no_changes_and_its_draft_writes_nothing()
    {
        var designer = new ColumnDesigner(Kunde, []);
        var (draft, problems) = designer.ToDraft();

        Assert.False(designer.HasChanges);
        Assert.Empty(problems);
        Assert.Empty(TableDesign.Plan(DesignContext.Of(Kunde), draft, DataFacts.None).Statements);
    }

    [Fact]
    public void Edited_fields_make_the_draft()
    {
        var designer = new ColumnDesigner(Kunde, []);
        var name = designer.Columns[1];
        name.Name = "NACHNAME";
        name.Size = "80";
        designer.Columns[3].Default = "";
        designer.ToggleDrop(designer.Columns[2]);
        var fax = designer.AddColumn();
        fax.Name = "FAX";
        fax.Size = "30";

        var (draft, problems) = designer.ToDraft();
        var plan = TableDesign.Plan(DesignContext.Of(Kunde), draft, DataFacts.None);

        Assert.Empty(problems);
        Assert.True(designer.HasChanges);
        Assert.Equal(
        [
            """ALTER TABLE "SHOP"."KUNDE" DROP COLUMN "KUERZEL" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" RENAME COLUMN "NAME" TO "NACHNAME" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("NACHNAME" VARCHAR2(80 CHAR))""",
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("BETRAG" DEFAULT NULL)""",
            """ALTER TABLE "SHOP"."KUNDE" ADD ("FAX" VARCHAR2(30 CHAR))""", // CHAR like most text columns of the table
        ], plan.Statements);
    }

    [Fact]
    public void Numbers_that_are_none_are_a_problem_and_the_column_stays()
    {
        var designer = new ColumnDesigner(Kunde, []);
        designer.Columns[1].Size = "viel";

        var (draft, problems) = designer.ToDraft();

        Assert.Equal(["NAME: Größe und Nachkommastellen müssen ganze Zahlen sein."], problems);
        Assert.Empty(TableDesign.Plan(DesignContext.Of(Kunde), draft, DataFacts.None).Statements);
    }

    [Fact]
    public void A_new_column_goes_away_an_existing_one_is_dropped_and_kept_again()
    {
        var designer = new ColumnDesigner(Kunde, []);
        var added = designer.AddColumn();
        designer.ToggleDrop(added);
        Assert.Equal(4, designer.Columns.Count);

        designer.ToggleDrop(designer.Columns[2]);
        Assert.True(designer.Columns[2].Dropped);
        Assert.DoesNotContain("KUERZEL", designer.ColumnNames);
        designer.ToggleDrop(designer.Columns[2]);
        Assert.False(designer.HasChanges);
    }

    [Fact]
    public void An_identity_column_keeps_its_definition_only_name_and_comment_change()
    {
        var identity = Kunde with { Columns = [Col("ID", "NUMBER", identity: true, nullable: false, @default: "\"SHOP\".\"ISEQ$$_1\".nextval")] };
        var designer = new ColumnDesigner(identity, []);
        var row = designer.Columns[0];
        Assert.True(row.OnlyNameAndComment);
        row.Comment = "Schlüssel";

        var plan = TableDesign.Plan(DesignContext.Of(identity), designer.ToDraft().Draft, DataFacts.None);

        Assert.Equal(["""COMMENT ON COLUMN "SHOP"."KUNDE"."ID" IS 'Schlüssel'"""], plan.Statements);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void Constraints_get_names_by_kind_and_columns_until_one_is_typed()
    {
        var existing = new ConstraintInfo("UK_KUNDE_NAME", ConstraintType.Unique, ["NAME"], null, null, [], null, true, true, false, false, false);
        var designer = new ColumnDesigner(Kunde, [existing]);
        var unique = designer.AddConstraint(ConstraintType.Unique);
        unique.Columns.Add("NAME");
        designer.SuggestName(unique);
        Assert.Equal("UK_KUNDE_NAME_2", unique.Name);

        unique.Name = "uk_eigen";
        unique.NameTyped = true;
        unique.Columns.Add("KUERZEL");
        designer.SuggestName(unique);
        Assert.Equal("UK_EIGEN", designer.ToDraft().Draft.AddedConstraints[0].Name); // typed without quotes: upper case
    }

    [Fact]
    public void Not_null_check_constraints_belong_to_their_columns_and_are_not_listed()
    {
        var notNull = new ConstraintInfo("SYS_C001", ConstraintType.Check, ["NAME"], "\"NAME\" IS NOT NULL", null, [], null, true, true, false, false, true);
        var check = new ConstraintInfo("CK_BETRAG", ConstraintType.Check, ["BETRAG"], "BETRAG >= 0", null, [], null, true, true, false, false, false);

        Assert.Equal(["CK_BETRAG"], new ColumnDesigner(Kunde, [notNull, check]).Existing.Select(c => c.Name));
    }
}
