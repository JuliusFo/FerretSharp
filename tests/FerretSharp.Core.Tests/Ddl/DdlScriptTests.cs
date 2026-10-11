using FerretSharp.Core.Ddl;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Ddl;

public class DdlScriptTests
{
    private static readonly TableRef Kunde = new("SHOP", "KUNDE");

    private static ColumnInfo Column(string name, string type, int? length = null, int? precision = null, int? scale = null, bool nullable = true,
        string? @default = null, bool defaultOnNull = false, string? comment = null) =>
        new(name, type, length, false, precision, scale, nullable, false, @default, 1, comment, DefaultOnNull: defaultOnNull);

    // ---- order ----

    [Fact]
    public void Steps_run_by_stage_then_object_then_as_written()
    {
        DdlStep S(string table, DdlStepKind kind) => new(table, kind, $"{kind} {table}");

        var ordered = DdlScript.Order(
        [
            S("B", DdlStepKind.Comment), S("B", DdlStepKind.AddColumn), S("A", DdlStepKind.Hint), S("B", DdlStepKind.ModifyNull),
            S("A", DdlStepKind.AddForeignKey), S("B", DdlStepKind.ModifyType), S("A", DdlStepKind.CreateTable), S("B", DdlStepKind.DropConstraint),
            S("A", DdlStepKind.CreateIndex), S("A", DdlStepKind.DropIndex), S("B", DdlStepKind.RenameColumn), S("B", DdlStepKind.DropColumn),
            S("B", DdlStepKind.AddKey), S("A", DdlStepKind.AddCheck), S("A", DdlStepKind.PlSqlObject),
        ]);

        Assert.Equal(
        [
            "DropConstraint B", "DropColumn B", "RenameColumn B", "CreateTable A",
            "ModifyNull B", "ModifyType B", // one stage: as written, so a column's steps stay together
            "AddColumn B", "AddKey B", "AddCheck A", "AddForeignKey A",
            "CreateIndex A", "DropIndex A", "Comment B", "PlSqlObject A", "Hint A",
        ], ordered.Select(s => s.Sql));
    }

    // ---- script ----

    [Fact]
    public void A_script_ends_statements_with_a_semicolon_and_a_plsql_object_with_a_slash_line()
    {
        var script = DdlScript.Render(
        [
            new DdlStep("KUNDE", DdlStepKind.AddColumn, "ALTER TABLE KUNDE ADD (FAX VARCHAR2(30))"),
            new DdlStep("KUNDE", DdlStepKind.PlSqlObject, "CREATE OR REPLACE TRIGGER TRG_KUNDE\nBEFORE INSERT ON KUNDE\nBEGIN\n  NULL;\nEND;"),
        ]);

        Assert.Equal("ALTER TABLE KUNDE ADD (FAX VARCHAR2(30));\n\nCREATE OR REPLACE TRIGGER TRG_KUNDE\nBEFORE INSERT ON KUNDE\nBEGIN\n  NULL;\nEND;\n/", script);
    }

    [Fact]
    public void A_step_that_must_not_run_is_commented_out_with_its_warning_above()
    {
        var step = new DdlStep("KUNDE", DdlStepKind.ModifyNull, "ALTER TABLE KUNDE MODIFY (LAND NULL)", DdlRisk.NotExecutable, "Nur nötig, wenn …");

        Assert.False(step.IsExecutable);
        Assert.Equal("-- ⚠ Nur nötig, wenn …\n-- ALTER TABLE KUNDE MODIFY (LAND NULL)", DdlScript.Render([step]));
        Assert.Equal("-- ALTER TABLE KUNDE MODIFY (LAND NULL)", DdlScript.Text(step));
        Assert.False(DdlStep.Hint("KUNDE", "Spaltenreihenfolge weicht ab", null).IsExecutable);
    }

    [Fact]
    public void A_header_comes_first_with_a_blank_line_before_each_step()
    {
        var script = DdlScript.Render([new DdlStep("A", DdlStepKind.DropIndex, "DROP INDEX IX_A", Warning: "Erste\nZweite")], ["-- Kopf"]);

        Assert.Equal("-- Kopf\n\n-- ⚠ Erste\n-- ⚠ Zweite\nDROP INDEX IX_A;", script);
        Assert.Equal("-- Kopf", DdlScript.Render([], ["-- Kopf"]));
    }

    // ---- column changes ----

    [Fact]
    public void A_drop_or_rename_is_ddl_with_a_known_intent_and_a_hint_without()
    {
        var before = Column("EMAIL", "VARCHAR2", 200);
        var renamed = before with { Name = "eMail" };

        var known = ColumnChange.Steps(Kunde, before, renamed, ChangeIntent.Known);
        var unknown = ColumnChange.Steps(Kunde, before, renamed, ChangeIntent.Unknown);

        Assert.Equal((DdlStepKind.RenameColumn, true), (Assert.Single(known).Kind, known[0].IsExecutable));
        Assert.Equal((DdlStepKind.Hint, false), (Assert.Single(unknown).Kind, unknown[0].IsExecutable));
        Assert.Equal("""ALTER TABLE "SHOP"."KUNDE" RENAME COLUMN "EMAIL" TO "eMail" """.TrimEnd(), known[0].Sql);
        Assert.Equal(known[0].Sql, unknown[0].Sql);

        var drop = Assert.Single(ColumnChange.Steps(Kunde, before, null, ChangeIntent.Known));
        Assert.Equal((DdlStepKind.DropColumn, DdlRisk.DataLoss), (drop.Kind, drop.Risk));
        Assert.Equal(DdlStepKind.Hint, Assert.Single(ColumnChange.Steps(Kunde, before, null, ChangeIntent.Unknown)).Kind);
    }

    [Fact]
    public void After_a_known_rename_the_statements_use_the_new_name_after_an_unknown_one_the_old()
    {
        var before = Column("EMAIL", "VARCHAR2", 200);
        var after = before with { Name = "MAIL", Length = 300, Comment = "Adresse" };

        Assert.Equal(
        [
            """ALTER TABLE "SHOP"."KUNDE" RENAME COLUMN "EMAIL" TO "MAIL" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("MAIL" VARCHAR2(300 BYTE))""",
            """COMMENT ON COLUMN "SHOP"."KUNDE"."MAIL" IS 'Adresse'""",
        ], ColumnChange.Steps(Kunde, before, after, ChangeIntent.Known).Select(s => s.Sql));
        Assert.Equal(
        [
            """ALTER TABLE "SHOP"."KUNDE" RENAME COLUMN "EMAIL" TO "MAIL" """.TrimEnd(),
            """ALTER TABLE "SHOP"."KUNDE" MODIFY ("EMAIL" VARCHAR2(300 BYTE))""",
            """COMMENT ON COLUMN "SHOP"."KUNDE"."EMAIL" IS 'Adresse'""",
        ], ColumnChange.Steps(Kunde, before, after, ChangeIntent.Unknown).Select(s => s.Sql));
    }

    /// <summary>A4: the comparison called both "may fail on data"; Oracle refuses them with any value (WP-23 experiment).</summary>
    [Theory]
    [InlineData("NUMBER", 10, 0, "NUMBER", 10, 2)]
    [InlineData("NVARCHAR2", null, null, "VARCHAR2", null, null)]
    public void A_change_that_needs_an_empty_column_says_so(string fromType, int? fromPrecision, int? fromScale, string toType, int? toPrecision, int? toScale)
    {
        var length = fromType.EndsWith("CHAR2", StringComparison.Ordinal) ? 50 : (int?)null;

        var step = Assert.Single(ColumnChange.Steps(Kunde, Column("X", fromType, length, fromPrecision, fromScale), Column("X", toType, length, toPrecision, toScale),
            ChangeIntent.Unknown));

        Assert.Equal((DdlStepKind.ModifyType, DdlRisk.NeedsEmptyColumn), (step.Kind, step.Risk));
        Assert.StartsWith("Geht nur bei leerer Spalte", step.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_change_modify_cannot_make_is_a_hint_without_intent_and_not_executable_with_one()
    {
        var before = Column("X", "VARCHAR2", 50);
        var clob = Column("X", "CLOB");

        Assert.Equal(DdlStepKind.Hint, Assert.Single(ColumnChange.Steps(Kunde, before, clob, ChangeIntent.Unknown)).Kind);
        var known = Assert.Single(ColumnChange.Steps(Kunde, before, clob, ChangeIntent.Known));
        Assert.Equal((DdlStepKind.ModifyType, DdlRisk.NotExecutable), (known.Kind, known.Risk));

        // Another family: a hint for the comparison, a MODIFY for an empty column for the designer.
        Assert.Equal(DdlStepKind.Hint, Assert.Single(ColumnChange.Steps(Kunde, Column("X", "DATE"), Column("X", "NUMBER"), ChangeIntent.Unknown)).Kind);
        Assert.Equal(DdlRisk.NeedsEmptyColumn, Assert.Single(ColumnChange.Steps(Kunde, Column("X", "DATE"), Column("X", "NUMBER"), ChangeIntent.Known)).Risk);
    }

    [Fact]
    public void Taking_default_on_null_away_writes_not_null_again_or_with_an_unknown_intent_a_commented_null()
    {
        var onNull = Column("LAND", "CHAR", 2, @default: "'DE'", defaultOnNull: true, nullable: false);

        Assert.Equal(
            [(DdlStepKind.ModifyDefault, true), (DdlStepKind.ModifyNull, true)],
            ColumnChange.Steps(Kunde, onNull, onNull with { DefaultOnNull = false }, ChangeIntent.Known).Select(s => (s.Kind, s.IsExecutable)));
        Assert.Equal(
            [(DdlStepKind.ModifyDefault, true)],
            ColumnChange.Steps(Kunde, onNull, onNull with { DefaultOnNull = false, Nullable = true }, ChangeIntent.Known).Select(s => (s.Kind, s.IsExecutable)));
        Assert.Equal(
            [(DdlStepKind.ModifyDefault, true), (DdlStepKind.ModifyNull, false)],
            ColumnChange.Steps(Kunde, onNull, onNull with { DefaultOnNull = false, Nullable = true }, ChangeIntent.Unknown).Select(s => (s.Kind, s.IsExecutable)));
    }

    [Fact]
    public void A_new_mandatory_column_without_default_may_fail_on_the_rows()
    {
        var steps = ColumnChange.Steps(Kunde, null, Column("NR", "NUMBER", nullable: false, comment: "Nummer"), ChangeIntent.Known);

        Assert.Equal([(DdlStepKind.AddColumn, DdlRisk.MayFailOnValues), (DdlStepKind.Comment, DdlRisk.Safe)], steps.Select(s => (s.Kind, s.Risk)));
        Assert.Equal(DdlRisk.Safe, ColumnChange.Steps(Kunde, null, Column("NR", "NUMBER", nullable: false, @default: "0"), ChangeIntent.Known)[0].Risk);
    }

    [Fact]
    public void Renames_wait_for_the_name_they_take_and_cycles_step_aside()
    {
        var steps = ColumnChange.Renames(Kunde, [("A", "B"), ("B", "C"), ("X", "Y"), ("Y", "X")], ["A", "B", "X", "Y", "FS_RENAME_1"]);

        Assert.Equal(
        [
            "\"B\" TO \"C\"", "\"A\" TO \"B\"", "\"X\" TO \"FS_RENAME_2\"", "\"Y\" TO \"X\"", "\"FS_RENAME_2\" TO \"Y\"",
        ], steps.Select(s => s.Sql[(s.Sql.IndexOf("COLUMN ", StringComparison.Ordinal) + 7)..]));
        Assert.All(steps, s => Assert.Equal(DdlStepKind.RenameColumn, s.Kind));
    }
}
