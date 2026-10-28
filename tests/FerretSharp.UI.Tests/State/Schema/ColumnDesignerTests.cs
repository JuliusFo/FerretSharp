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

    /// <summary>A new column takes CHAR from the table's text columns; the box shows it only where the type has a choice.</summary>
    [Fact]
    public void The_char_box_shows_the_semantics_of_the_type_and_keeps_the_choice()
    {
        var column = new ColumnDesigner(Kunde, []).AddColumn();
        Assert.True(column.ShowsCharSemantics);

        column.Type = "NUMBER";
        Assert.False(column.ShowsCharSemantics);
        Assert.False(column.ToColumn().Column!.CharSemantics);

        column.Type = "NVARCHAR2";
        Assert.True(column.ShowsCharSemantics);
        column.Type = "NCLOB";
        Assert.False(column.ShowsCharSemantics);

        column.Type = "VARCHAR2";
        Assert.True(column.ShowsCharSemantics);
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

        unique.Name = "uk_eigen"; // typed: it no longer follows
        unique.Columns.Add("KUERZEL");
        designer.SuggestName(unique);
        Assert.Equal("UK_EIGEN", designer.ToDraft().Draft.AddedConstraints[0].Name); // typed without quotes: upper case
    }

    /// <summary>R4b: a cleared name follows type and columns again – the same rule as for a new index.</summary>
    [Fact]
    public void A_cleared_name_follows_type_and_columns_again()
    {
        var designer = new ColumnDesigner(Kunde, []);
        var constraint = designer.AddConstraint(ConstraintType.Unique);
        constraint.Name = "UK_EIGEN";

        designer.SetType(constraint, ConstraintType.Check);
        Assert.Equal("UK_EIGEN", constraint.Name); // typed: does not follow the type

        constraint.Name = "";
        designer.AddColumn(constraint, "BETRAG");
        Assert.Equal("CK_KUNDE_BETRAG", constraint.Name);
        designer.SetType(constraint, ConstraintType.Unique);
        Assert.Equal("UK_KUNDE_BETRAG", constraint.Name); // follows again
    }

    private static readonly TableDetails Land = new(new TableSummary("SHOP", "LAND", TableKind.Table),
        [Col("CODE", "VARCHAR2", 2, nullable: false), Col("ISO", "CHAR", 3, nullable: false), Col("NAME", "VARCHAR2", 50)], ["CODE", "ISO"], [], false);

    /// <summary>R4b (out of the view): a foreign key's referenced columns follow the referenced table's key, position by position.</summary>
    [Fact]
    public void A_foreign_key_takes_the_referenced_key_once_the_table_is_known()
    {
        var designer = new ColumnDesigner(Kunde, []);
        var fk = designer.AddConstraint(ConstraintType.ForeignKey);
        designer.AddColumn(fk, "KUERZEL");
        designer.AddColumn(fk, "KUERZEL"); // twice: ignored
        designer.AddColumn(fk, "");

        designer.Reference(fk, Land.Table.Ref);
        Assert.Equal(("FK_KUNDE_LAND", true), (fk.Name, fk.ReferencedColumns.Count == 0)); // not loaded yet
        Assert.Null(designer.ColumnsOf(Land.Table.Ref));

        designer.Learn(Land);
        Assert.Equal(["CODE"], fk.ReferencedColumns);
        Assert.Equal(["CODE", "ISO", "NAME"], designer.ColumnsOf(Land.Table.Ref));

        designer.AddColumn(fk, "NAME"); // the key's second column
        designer.AddColumn(fk, "BETRAG"); // beyond the key: its first column
        Assert.Equal(["CODE", "ISO", "CODE"], fk.ReferencedColumns);

        fk.SetReferenced(2, "NAME");
        designer.RemoveColumn(fk, 0);
        Assert.Equal(["NAME", "BETRAG"], fk.Columns);
        Assert.Equal(["ISO", "NAME"], fk.ReferencedColumns);

        designer.Reference(fk, Land.Table.Ref); // chosen again: the key afresh, the table is known by now
        Assert.Equal(["CODE", "ISO"], fk.ReferencedColumns);

        // Known across a run that stopped halfway, and this table's own columns as drafted.
        var rest = designer.Rebase(Kunde, []);
        Assert.Same(Land, rest.Referenced[Land.Table.Ref]);
        Assert.Equal(["ID", "NAME", "KUERZEL", "BETRAG"], rest.ColumnsOf(Kunde.Table.Ref));
    }

    /// <summary>A new column starts as VARCHAR2(50); as a NUMBER the 50 would be a precision Oracle refuses.</summary>
    [Fact]
    public void Another_type_for_a_new_column_drops_a_size_it_cannot_take()
    {
        var designer = new ColumnDesigner(Kunde, []);
        var column = designer.AddColumn();
        column.Name = "BLA";

        column.Type = "NUMBER";
        Assert.Equal("", column.Size);
        Assert.Empty(designer.ToDraft().Problems);

        column.Size = "12";
        column.Scale = "2";
        column.Type = "VARCHAR2"; // 12 fits a length; the scale is not used and stays
        Assert.Equal(("12", "2"), (column.Size, column.Scale));

        column.Type = "TIMESTAMP"; // 12 fractional digits do not fit
        Assert.Equal("", column.Size);

        var existing = designer.Columns[1]; // VARCHAR2(50) in the table: kept, the user sees the change
        existing.Type = "NUMBER";
        Assert.Equal("50", existing.Size);
    }

    [Fact]
    public void An_existing_constraint_is_dropped_and_kept_again()
    {
        var check = new ConstraintInfo("CK_BETRAG", ConstraintType.Check, ["BETRAG"], "BETRAG >= 0", null, [], null, true, true, false, false, false);
        var designer = new ColumnDesigner(Kunde, [check]);

        designer.ToggleDropConstraint("CK_BETRAG");
        Assert.Equal(["CK_BETRAG"], designer.ToDraft().Draft.DroppedConstraints);

        designer.ToggleDropConstraint("CK_BETRAG");
        Assert.Empty(designer.ToDraft().Draft.DroppedConstraints);
    }

    private static readonly ConstraintInfo CkBetrag = new("CK_BETRAG", ConstraintType.Check, ["BETRAG"], "BETRAG >= 0 ", null, [], null, true, true, false, false, false);

    private static readonly ConstraintInfo FkLand = new("FK_KUNDE_LAND", ConstraintType.ForeignKey, ["KUERZEL"], null, Land.Table.Ref, ["CODE"], "NO ACTION", true, true, false, false, false);

    [Fact]
    public void Existing_constraints_become_edits_once_renamed_or_changed()
    {
        var designer = new ColumnDesigner(Kunde, [CkBetrag, FkLand]);
        var (check, fk) = (designer.Existing[0], designer.Existing[1]);
        Assert.Equal(("BETRAG >= 0", ""), (check.Condition, fk.DeleteRule)); // trimmed; NO ACTION is none
        Assert.False(designer.HasChanges);
        Assert.Empty(designer.ToDraft().Draft.ChangedConstraints);

        check.Name = "ck_betrag"; // typed without quotes: the same name
        Assert.False(check.IsChanged);

        check.Name = "ck_betrag_positiv";
        fk.DeleteRule = "CASCADE";
        fk.RenameIndex = false;
        Assert.True(designer.ConstraintsChanged);
        Assert.False(designer.ColumnsChanged);
        Assert.Equal(
        [
            new ConstraintEdit("CK_BETRAG", "CK_BETRAG_POSITIV", "BETRAG >= 0"),
            new ConstraintEdit("FK_KUNDE_LAND", "FK_KUNDE_LAND", DeleteRule: "CASCADE", RenameIndex: false),
        ], designer.ToDraft().Draft.ChangedConstraints);

        designer.ToggleDropConstraint("CK_BETRAG"); // dropped: its edit goes
        Assert.Equal(["FK_KUNDE_LAND"], designer.ToDraft().Draft.ChangedConstraints.Select(e => e.Original));
    }

    [Fact]
    public void A_suggested_name_avoids_the_names_existing_constraints_get()
    {
        var designer = new ColumnDesigner(Kunde, [CkBetrag]);
        designer.Existing[0].Name = "UK_KUNDE_NAME";

        var unique = designer.AddConstraint(ConstraintType.Unique);
        designer.AddColumn(unique, "NAME");

        Assert.Equal("UK_KUNDE_NAME_2", unique.Name);
    }

    /// <summary>A check replaced halfway: the new one is in under the name in between, the old one went or not.</summary>
    [Fact]
    public void A_check_replaced_halfway_goes_on_with_drop_and_rename()
    {
        var designer = new ColumnDesigner(Kunde, [CkBetrag]);
        designer.Existing[0].Condition = "BETRAG > 0";
        var replacement = CkBetrag with { Name = "FS_CHECK_1", Condition = "BETRAG > 0" };

        var dropFailed = designer.Rebase(Kunde, [CkBetrag, replacement]);
        Assert.Equal(
        [
            """ALTER TABLE "SHOP"."KUNDE" DROP CONSTRAINT "CK_BETRAG" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" RENAME CONSTRAINT "FS_CHECK_1" TO "CK_BETRAG" """.TrimEnd(),
        ], Plan(dropFailed, [CkBetrag, replacement]));

        var renameFailed = designer.Rebase(Kunde, [replacement]);
        Assert.Equal(["""ALTER TABLE "SHOP"."KUNDE" RENAME CONSTRAINT "FS_CHECK_1" TO "CK_BETRAG" """.TrimEnd()], Plan(renameFailed, [replacement]));
    }

    /// <summary>A foreign key with a new delete rule that was dropped, but not added again: it is still to add.</summary>
    [Fact]
    public void A_foreign_key_dropped_halfway_is_added_again()
    {
        var designer = new ColumnDesigner(Kunde, [FkLand]);
        designer.Existing[0].DeleteRule = "CASCADE";
        designer.Columns[2].Name = "LAND"; // renamed too, before the run stopped

        var rest = designer.Rebase(Kunde with { Columns = [Kunde.Columns[0], Kunde.Columns[1], Kunde.Columns[2] with { Name = "LAND" }, Kunde.Columns[3]] }, []);

        var fk = Assert.Single(rest.AddedConstraints);
        Assert.Equal(("FK_KUNDE_LAND", "CASCADE", false), (fk.Name, fk.DeleteRule, fk.WithIndex));
        Assert.Equal(["LAND"], fk.Columns);
        Assert.Equal(["CODE"], fk.ReferencedColumns);
    }

    private static IReadOnlyList<string> Plan(ColumnDesigner designer, IReadOnlyList<ConstraintInfo> constraints) =>
        TableDesign.Plan(new DesignContext(Kunde, constraints, [], []), designer.ToDraft().Draft, DataFacts.None).Statements;

    [Fact]
    public void Edits_of_existing_constraints_and_the_tab_shown_survive_a_rebase()
    {
        var designer = new ColumnDesigner(Kunde, [CkBetrag, FkLand]) { Section = DesignerSection.Constraints };
        designer.Existing[0].Name = "CK_POSITIV";
        designer.Existing[1].DeleteRule = "SET NULL";

        // The rename ran, the foreign key did not.
        var rest = designer.Rebase(Kunde, [CkBetrag with { Name = "CK_POSITIV" }, FkLand]);

        Assert.Equal(DesignerSection.Constraints, rest.Section);
        Assert.Equal([new ConstraintEdit("FK_KUNDE_LAND", "FK_KUNDE_LAND", DeleteRule: "SET NULL")], rest.ToDraft().Draft.ChangedConstraints);
    }

    // ---- the draft of a new index (R4b, out of IndexesView) ----

    private static readonly IndexInfo IxName = new("SHOP", "IX_KUNDE_NAME", "NORMAL", false, "VALID", [new IndexColumn("NAME", false, false)], null, false, true);

    [Fact]
    public void An_index_draft_names_itself_after_its_columns_until_a_name_is_typed()
    {
        var editor = IndexDraftEditor.Start(Kunde.Table.Ref, [IxName], null);
        Assert.Equal("", editor.Name);
        Assert.Empty(editor.Steps());

        editor.Add("NAME");
        Assert.Equal("IX_KUNDE_NAME_2", editor.Name); // the table has one of that name
        editor.Add("KUERZEL");
        editor.Move(1, -1);
        Assert.Equal(["KUERZEL", "NAME"], editor.Columns);
        Assert.Equal("IX_KUNDE_KUERZEL_NAME", editor.Name);

        editor.TypeName("ix_eigen");
        editor.Remove(0);
        Assert.Equal("IX_EIGEN", editor.Draft.Name); // typed without quotes: upper case, and it stays
        editor.Unique = true;
        Assert.Equal("""CREATE UNIQUE INDEX "SHOP"."IX_EIGEN" ON "SHOP"."KUNDE" ("NAME")""", Assert.Single(editor.Steps()).Sql);

        editor.TypeName("");
        editor.SuggestName(); // leaving the cleared field
        Assert.Equal("IX_KUNDE_NAME_2", editor.Name);
    }

    [Fact]
    public void An_index_draft_for_a_foreign_key_starts_with_its_columns()
    {
        var foreignKey = new ForeignKeyInfo("FK_KUNDE_LAND", Kunde.Table.Ref, ["KUERZEL"], Land.Table.Ref, ["CODE"], FkSource.Declared);

        var editor = IndexDraftEditor.Start(Kunde.Table.Ref, [IxName], IndexDesign.ForForeignKey(foreignKey, [IxName]));

        Assert.Equal(["KUERZEL"], editor.Columns);
        Assert.Equal(("IX_KUNDE_KUERZEL", false), (editor.Name, editor.Unique));
    }

    /// <summary>
    /// DDL that stopped halfway (WP-23 E2E): rename and drop ran, the new column and the key did not. Laid over the table
    /// as it is now, the draft writes only what is left.
    /// </summary>
    [Fact]
    public void A_draft_laid_over_the_table_after_a_partial_run_writes_only_the_rest()
    {
        var designer = new ColumnDesigner(Kunde, []);
        designer.Columns[1].Name = "NACHNAME";
        designer.ToggleDrop(designer.Columns[2]);
        designer.AddColumn().Name = "FAX";
        designer.AddConstraint(ConstraintType.Unique).Columns.Add("NACHNAME");
        var plan = TableDesign.Plan(DesignContext.Of(Kunde), designer.ToDraft().Draft, DataFacts.None);
        Assert.Equal(4, plan.Statements.Count);

        var now = Kunde with { Columns = [Kunde.Columns[0], Kunde.Columns[1] with { Name = "NACHNAME" }, Kunde.Columns[3]] };
        var rest = designer.Rebase(now, []);

        Assert.Equal(["ID", "NACHNAME", "BETRAG", "FAX"], rest.Columns.Select(c => c.Name));
        Assert.Equal(
        [
            """ALTER TABLE "SHOP"."KUNDE" ADD ("FAX" VARCHAR2(50 CHAR))""",
            """ALTER TABLE "SHOP"."KUNDE" ADD CONSTRAINT "UK_KUNDE" UNIQUE ("NACHNAME")""",
        ], TableDesign.Plan(DesignContext.Of(now), rest.ToDraft().Draft, DataFacts.None).Statements);
    }

    [Fact]
    public void Laid_over_an_unchanged_table_the_draft_stays_whole()
    {
        var designer = new ColumnDesigner(Kunde, []);
        designer.Columns[1].Name = "NACHNAME";
        designer.ToggleDrop(designer.Columns[2]);

        var again = designer.Rebase(Kunde, []);

        Assert.Equal(
            TableDesign.Plan(DesignContext.Of(Kunde), designer.ToDraft().Draft, DataFacts.None).Statements,
            TableDesign.Plan(DesignContext.Of(Kunde), again.ToDraft().Draft, DataFacts.None).Statements);
    }

    /// <summary>WP-26: the audit box survives a run that stopped halfway – the rest of the audit steps still belong to the plan.</summary>
    [Fact]
    public void The_audit_box_survives_a_rebase()
    {
        var designer = new ColumnDesigner(Kunde, []) { WithAudit = true };

        Assert.True(designer.Rebase(Kunde, []).WithAudit);
        Assert.Null(new ColumnDesigner(Kunde, []).Rebase(Kunde, []).WithAudit);
    }

    /// <summary>
    /// The audit box at start: the setting turns it off for a table with an audit table, not when opened for the audit;
    /// without creating audit tables it stays off for a table without one, also when opened for the audit.
    /// </summary>
    [Theory]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, true, true, true)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, true, false, false, true)]
    public void The_audit_box_starts_by_audit_table_settings_and_how_the_designer_was_opened(bool openedForAudit, bool hasAuditTable, bool syncByDefault,
        bool createAllowed, bool expected) =>
        Assert.Equal(expected, ColumnDesigner.AuditAtStart(openedForAudit, hasAuditTable, syncByDefault, createAllowed));

    /// <summary>Without creating audit tables the box is only there for a table that has one.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    public void The_audit_box_takes_part_unless_it_would_create_an_audit_table_that_may_not_be_created(bool hasAuditTable, bool createAllowed, bool expected) =>
        Assert.Equal(expected, ColumnDesigner.CanAudit(hasAuditTable, createAllowed));

    /// <summary>Code review: a box ticked before creating was switched off survives a rebase but puts no audit into the plan.</summary>
    [Fact]
    public void A_ticked_audit_box_takes_part_only_where_the_audit_may_be_planned()
    {
        var designer = new ColumnDesigner(Kunde, []) { WithAudit = true }.Rebase(Kunde, []);

        Assert.False(designer.AuditTakesPart(hasAuditTable: false, createAllowed: false));
        Assert.True(designer.AuditTakesPart(hasAuditTable: false, createAllowed: true));
        Assert.True(designer.AuditTakesPart(hasAuditTable: true, createAllowed: false));
        Assert.False(new ColumnDesigner(Kunde, []) { WithAudit = false }.AuditTakesPart(hasAuditTable: true, createAllowed: true));
        Assert.False(new ColumnDesigner(Kunde, []).AuditTakesPart(hasAuditTable: true, createAllowed: true));
    }

    [Fact]
    public void Not_null_check_constraints_belong_to_their_columns_and_are_not_listed()
    {
        var notNull = new ConstraintInfo("SYS_C001", ConstraintType.Check, ["NAME"], "\"NAME\" IS NOT NULL", null, [], null, true, true, false, false, true);
        var check = new ConstraintInfo("CK_BETRAG", ConstraintType.Check, ["BETRAG"], "BETRAG >= 0", null, [], null, true, true, false, false, false);

        Assert.Equal(["CK_BETRAG"], new ColumnDesigner(Kunde, [notNull, check]).Existing.Select(c => c.Name));
    }
}
