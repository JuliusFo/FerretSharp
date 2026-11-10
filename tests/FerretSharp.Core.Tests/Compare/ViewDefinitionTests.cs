using FerretSharp.Core.Compare;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.Compare.TestComparison;

namespace FerretSharp.Core.Tests.Compare;

/// <summary>Definitions and status of views and materialized views in the schema comparison and its DDL proposal (WP-82).</summary>
public class ViewDefinitionTests
{
    private const string Ref = "DEV";
    private const string Tgt = "TEST";

    private static readonly ColumnInfo Id = Column("ID", "NUMBER", precision: 10, scale: 0, nullable: false);
    private static readonly ColumnInfo Name = Column("NAME", "VARCHAR2", length: 50);

    private static ObjectSnapshot View(
        string name, string? definition, ColumnInfo[]? columns = null, bool invalid = false, ViewOption option = ViewOption.None,
        string? comment = null, string[]? dependsOn = null, bool truncated = false) =>
        new(name, TableKind.View, (columns ?? [Id, Name]).Select((c, i) => c with { Position = i + 1 }).ToList(), OptionConstraints(option), [],
            Comment: comment, Definition: definition, DefinitionTruncated: truncated, Invalid: invalid, DependsOn: dependsOn);

    private static ObjectSnapshot MView(string name, string? definition, MViewOptions? options = null, bool invalid = false) =>
        new(name, TableKind.MaterializedView, [Id with { Position = 1 }], [], [], Definition: definition, Invalid: invalid,
            MView: options ?? new MViewOptions("FORCE", "DEMAND", QueryRewrite: false, Prebuilt: false));

    private static List<ConstraintInfo> OptionConstraints(ViewOption option) => option switch
    {
        ViewOption.ReadOnly => [ViewConstraint(ConstraintType.ViewReadOnly)],
        ViewOption.CheckOption => [ViewConstraint(ConstraintType.ViewCheckOption)],
        _ => [],
    };

    private static ConstraintInfo ViewConstraint(ConstraintType type) => new("SYS_C0001", type, [], null, null, [], null, true, true, false, false, true);

    private static SchemaComparison Compare(ObjectSnapshot[] reference, ObjectSnapshot[] target, CompareOptions? options = null) =>
        SchemaDiff.Compare([Snapshot(Ref, reference), Snapshot(Tgt, target)], options ?? new CompareOptions(Reference: 0));

    private static DdlProposal Align(ObjectSnapshot[] reference, ObjectSnapshot[] target) => SchemaDdl.Align(Compare(reference, target), 0, 1);

    private static string[] Sql(DdlProposal proposal) => proposal.Steps.Select(DdlScript.Text).ToArray();

    // ---- normalizing ----

    [Theory]
    [InlineData("select a,b from t", "SELECT A, B FROM T")]
    [InlineData("SELECT  a\r\n  , b -- note\n FROM t /* x */", "SELECT A, B FROM T")]
    [InlineData("select \"A\", \"b\" from \"T\"", "SELECT A, \"b\" FROM T")]
    [InlineData("select x from DEV.T join \"DEV\".\"U\" on 1 = 1", "SELECT X FROM T JOIN U ON 1 = 1")]
    [InlineData("select dev.t.x from dev.t", "SELECT T.X FROM T")]
    [InlineData("select 'Dev.x' from OTHER.T", "SELECT 'Dev.x' FROM OTHER.T")]
    [InlineData("select t.DEV.x from t", "SELECT T.DEV.X FROM T")]
    [InlineData("select count ( * ) , max(a)from t", "SELECT COUNT (*), MAX (A) FROM T")]
    public void Normalize_ignores_spaces_comments_case_and_the_own_schema(string text, string expected) =>
        Assert.Equal(expected, ViewText.Normalize(text, Ref));

    [Theory]
    [InlineData("select x from DEV.T", true)]
    [InlineData("select x from \"DEV\".\"T\"", true)]
    [InlineData("select x from dev.t", true)]
    [InlineData("select 'DEV.T' from t", false)]
    [InlineData("select t.DEV.x from t", false)]
    [InlineData("select x from DEVX.T", false)]
    public void NamesSchema_finds_the_schema_as_prefix(string text, bool expected) => Assert.Equal(expected, ViewText.NamesSchema(text, Ref));

    [Theory]
    [InlineData("select 1 from dual with check option", true)]
    [InlineData("select 1 from dual WITH CHECK OPTION CONSTRAINT ck_v", true)]
    [InlineData("select 1 from dual", false)]
    [InlineData("select 'with check option' from dual", false)]
    public void HasCheckOption_reads_the_end_of_the_text(string text, bool expected) => Assert.Equal(expected, ViewText.HasCheckOption(text));

    // ---- comparing ----

    [Fact]
    public void Views_get_definition_and_status_rows_before_their_columns()
    {
        var row = Compare([View("V", "select id, name from DEV.kunde")], [View("V", "SELECT ID, NAME FROM KUNDE")]).Objects[0];

        Assert.Equal([CompareKind.Definition, CompareKind.Status, CompareKind.Column, CompareKind.Column], row.Children.Select(c => c.Kind));
        Assert.False(row.HasDifferences);
        Assert.Equal("SELECT ID, NAME FROM KUNDE", row.Children[0].Cells[0].Definition);
        Assert.Equal("VALID", row.Children[1].Cells[1].Definition);
        Assert.Equal("V/def", row.Children[0].Key);
        Assert.Same(row.Cells[1].Object, row.Children[0].Cells[1].Object);
    }

    [Fact]
    public void Tables_get_no_definition_rows()
    {
        var row = Compare([Table("T", [Id])], [Table("T", [Id])]).Objects[0];

        Assert.DoesNotContain(row.Children, c => c.Kind is CompareKind.Definition or CompareKind.Status);
    }

    [Fact]
    public void Another_query_or_option_is_a_difference()
    {
        Assert.True(Compare([View("V", "select id, name from kunde")], [View("V", "select id, name from kunde where id > 0")]).Objects[0].Children[0].Differs);
        Assert.True(Compare([View("V", "select id, name from kunde", option: ViewOption.ReadOnly)], [View("V", "select id, name from kunde")]).Objects[0].Children[0].Differs);
        Assert.Equal("SELECT ID, NAME FROM KUNDE WITH READ ONLY",
            Compare([View("V", "select id, name from kunde", option: ViewOption.ReadOnly)], []).Objects[0].Children[0].Cells[0].Definition);
    }

    [Fact]
    public void Materialized_view_compares_refresh_options()
    {
        var row = Compare(
            [MView("MV", "select id from t", new MViewOptions("FAST", "COMMIT", QueryRewrite: true, Prebuilt: false))],
            [MView("MV", "select id from t")]).Objects[0];

        Assert.Equal("SELECT ID FROM T · REFRESH FAST ON COMMIT · ENABLE QUERY REWRITE", row.Children[0].Cells[0].Definition);
        Assert.True(row.Children[0].Differs);
    }

    [Fact]
    public void Invalid_view_differs_in_status_and_its_undefined_columns_count_for_nothing()
    {
        var undefined = Column("NAME", "UNDEFINED");
        var row = Compare([View("V", "select id, name from kunde")], [View("V", "select id, name from kunde", [Id, undefined], invalid: true)]).Objects[0];

        var status = row.Children.Single(c => c.Kind == CompareKind.Status);
        Assert.Equal([CellState.Same, CellState.Different], status.Cells.Select(c => c.State));
        Assert.Equal("INVALID", status.Cells[1].Definition);

        var name = row.Children.Single(c => c.Name == "NAME");
        Assert.Equal(CellState.Unknown, name.Cells[1].State);
        Assert.Equal(-1, name.Cells[1].Group);
        Assert.False(name.Differs);
    }

    [Fact]
    public void Unknown_reference_column_leaves_the_others_alike()
    {
        var row = Compare(
            [View("V", "select id, name from kunde", [Id, Column("NAME", "UNDEFINED")], invalid: true)],
            [View("V", "select id, name from kunde")]).Objects[0];

        var name = row.Children.Single(c => c.Name == "NAME");
        Assert.Equal([CellState.Unknown, CellState.Same], name.Cells.Select(c => c.State));
    }

    // ---- DDL proposal ----

    [Fact]
    public void Missing_view_is_created_with_column_list_option_and_comments()
    {
        var view = View("V_KUNDE", "select id, name\nfrom kunde", [Id, Name with { Comment = "Name" }], option: ViewOption.CheckOption, comment: "Kunden");

        Assert.Equal(
        [
            "CREATE VIEW \"TEST\".\"V_KUNDE\" (\"ID\", \"NAME\") AS\nselect id, name\nfrom kunde\nWITH CHECK OPTION",
            "COMMENT ON TABLE \"TEST\".\"V_KUNDE\" IS 'Kunden'",
            "COMMENT ON COLUMN \"TEST\".\"V_KUNDE\".\"NAME\" IS 'Name'",
        ], Sql(Align([view], [])));
    }

    [Fact]
    public void Option_already_in_the_text_is_not_written_twice()
    {
        var step = Assert.Single(Align([View("V", "select id, name from kunde with read only", option: ViewOption.ReadOnly)], []).Steps);

        Assert.EndsWith("from kunde with read only", step.Sql);
    }

    [Fact]
    public void Differing_view_is_replaced_and_its_instead_of_triggers_named()
    {
        var step = Assert.Single(Align([View("V", "select id, name from kunde where id > 0")], [View("V", "select id, name from kunde")]).Steps);

        Assert.Equal("CREATE OR REPLACE VIEW \"TEST\".\"V\" (\"ID\", \"NAME\") AS\nselect id, name from kunde where id > 0", step.Sql);
        Assert.True(step.IsExecutable);
        Assert.Contains("INSTEAD-OF-Trigger", step.Warning); // Oracle drops them on replace (review WP-82)
    }

    /// <summary>Review WP-82: Oracle keeps a line comment at the end of the text; the ";" must not land in it.</summary>
    [Fact]
    public void A_view_text_ending_in_a_line_comment_stays_one_statement_in_the_script()
    {
        var proposal = Align([View("V_A", "select id, name from kunde -- note"), View("V_B", "select id, name from kunde")], []);

        var statements = SqlScript.Split(proposal.Script.ReplaceLineEndings("\n"))
            .Where(s => s.Text.Contains("CREATE VIEW", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, statements.Count);
        Assert.Contains(statements, s => s.Text.Contains("V_B", StringComparison.Ordinal) && !s.Text.Contains("V_A", StringComparison.Ordinal));
    }

    /// <summary>Review WP-82: the SQL editor ends a statement at a line with only "/".</summary>
    [Fact]
    public void A_view_text_with_a_slash_line_is_only_a_hint()
    {
        var step = Assert.Single(Align([View("V", "select 4\n/\n2 as id, name from kunde")], []).Steps);

        Assert.False(step.IsExecutable);
        Assert.Contains("„/“", step.Warning);
        Assert.False(ViewText.HasSlashLine("select 4 / 2 from dual"));
        Assert.False(ViewText.HasSlashLine("select '\n/\n' from dual"));
    }

    /// <summary>Review WP-82: like a foreign key to a table the choice leaves out.</summary>
    [Fact]
    public void View_on_a_missing_view_the_choice_leaves_out_warns()
    {
        var comparison = Compare([View("V_A", "select id, name from kunde"), View("V_B", "select id, name from v_a", dependsOn: ["V_A"])], []);

        var proposal = SchemaDdl.Align(comparison, 0, 1, [comparison.Objects.Single(o => o.Name == "V_B")]);

        var warning = Assert.Single(proposal.Steps).Warning;
        Assert.Contains("V_A", warning);
        Assert.Contains("ORA-00942", warning);
    }

    /// <summary>Review WP-82: two texts cut at the LONG limit with the same start do not count as alike.</summary>
    [Fact]
    public void Cut_or_invisible_definitions_are_not_compared()
    {
        var cut = Compare([View("V", "select id, name from kunde", truncated: true)], [View("V", "select id, name from kunde", truncated: true)]).Objects[0];
        var hidden = Compare([View("V", null)], [View("V", null)]).Objects[0];

        Assert.Equal([CellState.Unknown, CellState.Unknown], cut.Children[0].Cells.Select(c => c.State));
        Assert.Equal([CellState.Unknown, CellState.Unknown], hidden.Children[0].Cells.Select(c => c.State));
    }

    /// <summary>Second review pass: a readable side next to a cut reference looked "the same" – the row is not compared, and the proposal follows.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void A_readable_side_next_to_a_cut_one_is_not_compared_either(int? reference)
    {
        var comparison = Compare(
            [View("V", "select id, name from kunde", truncated: true)], [View("V", "select id, name from other_table")], new CompareOptions(reference));

        var definition = comparison.Objects[0].Children[0];
        Assert.Equal([CellState.Unknown, CellState.Unknown], definition.Cells.Select(c => c.State));
        Assert.False(definition.Differs);
        Assert.Empty(SchemaDdl.Align(comparison, 1, 0).Steps); // the readable side is the reference here: no CREATE OR REPLACE over the cut one
    }

    /// <summary>Review WP-82: other refresh options alone need no DROP – ALTER keeps the rows.</summary>
    [Fact]
    public void Materialized_view_with_other_options_only_is_altered()
    {
        var proposal = Align(
            [MView("MV", "select id from DEV.t", new MViewOptions("COMPLETE", "DEMAND", QueryRewrite: true, Prebuilt: false))],
            [MView("MV", "SELECT ID FROM TEST.T")]);

        Assert.Equal(
            ["ALTER MATERIALIZED VIEW \"TEST\".\"MV\" REFRESH COMPLETE ON DEMAND", "ALTER MATERIALIZED VIEW \"TEST\".\"MV\" ENABLE QUERY REWRITE"],
            Sql(proposal));
        Assert.All(proposal.Steps, s => Assert.True(s.IsExecutable));
    }

    [Fact]
    public void Other_column_names_replace_the_view_but_types_alone_do_not()
    {
        var renamed = Align([View("V", "select id, name from kunde")], [View("V", "select id, name from kunde", [Id, Column("TITEL", "VARCHAR2", length: 50)])]);
        Assert.StartsWith("CREATE OR REPLACE VIEW", Assert.Single(renamed.Steps).Sql);

        var retyped = Align([View("V", "select id, name from kunde")], [View("V", "select id, name from kunde", [Id, Column("NAME", "VARCHAR2", length: 80)])]);
        Assert.Empty(retyped.Steps);
    }

    [Fact]
    public void Invalid_target_with_the_same_definition_is_compiled()
    {
        var proposal = Align([View("V", "select id, name from kunde")], [View("V", "select id, name from kunde", [Id, Column("NAME", "UNDEFINED")], invalid: true)]);

        Assert.Equal(["ALTER VIEW \"TEST\".\"V\" COMPILE"], Sql(proposal));
    }

    [Fact]
    public void Query_naming_the_reference_schema_and_invalid_reference_warn()
    {
        var step = Assert.Single(Align([View("V", "select id, name from DEV.kunde", invalid: true)], []).Steps);

        Assert.Contains("Schema der Referenz (DEV)", step.Warning);
        Assert.Contains("in der Referenz ungültig", step.Warning);
    }

    [Fact]
    public void Truncated_or_unknown_definition_is_only_a_hint()
    {
        var step = Assert.Single(Align([View("V", "select id, name from kunde", truncated: true)], []).Steps);

        Assert.False(step.IsExecutable);
        Assert.Equal("-- CREATE VIEW \"TEST\".\"V\" AS …", DdlScript.Text(step));
        Assert.Contains("nicht vollständig lesen", step.Warning);
    }

    [Fact]
    public void Views_run_after_the_views_they_select_from()
    {
        var proposal = Align(
        [
            View("A_TOP", "select id, name from m_mid", dependsOn: ["M_MID"]),
            View("M_MID", "select id, name from z_base", dependsOn: ["Z_BASE"]),
            View("Z_BASE", "select id, name from kunde"),
        ], []);

        Assert.Equal(["Z_BASE", "M_MID", "A_TOP"], proposal.Steps.Select(s => s.Object));
    }

    [Fact]
    public void Missing_materialized_view_is_created_with_its_options()
    {
        var step = Assert.Single(Align([MView("MV", "select id from t", new MViewOptions("FAST", "COMMIT", QueryRewrite: true, Prebuilt: false))], []).Steps);

        Assert.Equal("CREATE MATERIALIZED VIEW \"TEST\".\"MV\" (\"ID\")\nBUILD IMMEDIATE REFRESH FAST ON COMMIT ENABLE QUERY REWRITE AS\nselect id from t", step.Sql);
        Assert.True(step.IsExecutable);
        Assert.Contains("BUILD IMMEDIATE", step.Warning);
        Assert.Contains("ORA-23413", step.Warning);
    }

    [Fact]
    public void Never_refresh_is_written_as_such()
    {
        var step = Assert.Single(Align([MView("MV", "select id from t", new MViewOptions("NEVER", "NEVER", false, false))], []).Steps);

        Assert.Contains("BUILD IMMEDIATE NEVER REFRESH AS", step.Sql);
    }

    [Fact]
    public void Prebuilt_materialized_view_is_only_a_hint()
    {
        var step = Assert.Single(Align([MView("MV", "select id from t", new MViewOptions("FORCE", "DEMAND", false, Prebuilt: true))], []).Steps);

        Assert.False(step.IsExecutable);
        Assert.Contains("ON PREBUILT TABLE", step.Warning);
    }

    [Fact]
    public void Differing_materialized_view_is_a_commented_drop_and_create()
    {
        var proposal = Align([MView("MV", "select id from t where id > 0")], [MView("MV", "select id from t")]);

        Assert.All(proposal.Steps, s => Assert.False(s.IsExecutable));
        Assert.Equal("-- DROP MATERIALIZED VIEW \"TEST\".\"MV\"", DdlScript.Text(proposal.Steps[0]));
        Assert.StartsWith("-- CREATE MATERIALIZED VIEW \"TEST\".\"MV\" (\"ID\")", DdlScript.Text(proposal.Steps[1]));
        Assert.Contains("DROP entfernt ihre Zeilen", proposal.Steps[0].Warning);
    }

    [Fact]
    public void Invalid_materialized_view_is_compiled()
    {
        var proposal = Align([MView("MV", "select id from t")], [MView("MV", "select id from t", invalid: true)]);

        Assert.Equal(["ALTER MATERIALIZED VIEW \"TEST\".\"MV\" COMPILE"], Sql(proposal));
    }

    [Fact]
    public void Materialized_view_comment_uses_its_own_keyword()
    {
        var mview = MView("MV", "select id from t") with { Comment = "Summen" };

        Assert.Equal("COMMENT ON MATERIALIZED VIEW \"TEST\".\"MV\" IS 'Summen'", Sql(Align([mview], []))[1]);
    }
}
