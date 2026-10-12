using FerretSharp.Core.Audit;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Audit;

/// <summary>WP-26: the DDL that creates or updates a table's audit table and trigger from the template.</summary>
public sealed class AuditPlannerTests
{
    private static readonly TableRef Kunde = new("APP", "KUNDE");
    private static readonly AuditTemplate Template = AuditTemplates.Parse(AuditTemplateTests.CompanyTemplate);

    private static readonly IReadOnlyList<ColumnInfo> Columns =
    [
        Column("KUNDE_ID", "NUMBER", precision: 10, scale: 0, nullable: false, position: 1),
        Column("NAME", "VARCHAR2", length: 80, position: 2, comment: "Name of the customer"),
        Column("STATUS", "CHAR", length: 1, nullable: false, position: 3),
    ];

    private static ColumnInfo Column(string name, string type, int? length = null, int? precision = null, int? scale = null, bool nullable = true,
        int position = 1, string? comment = null) =>
        new(name, type, length, false, precision, scale, nullable, IsIdentity: false, Default: null, Position: position, Comment: comment);

    private static TableDetails Table(string name, IReadOnlyList<ColumnInfo> columns) =>
        new(new TableSummary("APP", name, TableKind.Table), columns, [], [], false);

    /// <summary>The audit table and trigger exactly as the template makes them for <paramref name="columns"/>.</summary>
    private static AuditState UpToDate(IReadOnlyList<ColumnInfo> columns) =>
        new(Table("AUDIT_KUNDE", Template.TableFor(Kunde, columns).Columns), new AuditTriggerState(Kunde, true, true, Template.TriggerFor(Kunde, columns).Columns),
            "Changes to KUNDE");

    private static DataFacts Rows(long rows) => new(new Dictionary<DataQuestion, long> { [new DataQuestion(DataQuestionKind.Rows)] = rows });

    [Fact]
    public void Without_audit_objects_it_creates_table_comments_and_trigger_last()
    {
        var plan = AuditPlanner.Plan(Template, Kunde, Columns, AuditState.None, DataFacts.None, taken: ["KUNDE"]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Problems));
        Assert.Equal(DdlStepKind.CreateTable, plan.Steps[0].Kind);
        Assert.Contains("\"APP\".\"AUDIT_KUNDE\"", plan.Steps[0].Sql);
        Assert.Contains(plan.Steps, s => s is { Kind: DdlStepKind.Comment } && s.Sql.Contains("Name of the customer"));
        Assert.Contains(plan.Steps, s => s is { Kind: DdlStepKind.Comment } && s.Sql.Contains("COMMENT ON TABLE") && s.Sql.Contains("Changes to KUNDE"));
        Assert.Equal(DdlStepKind.PlSqlObject, plan.Steps[^1].Kind);
        Assert.StartsWith("CREATE OR REPLACE TRIGGER APP.KUNDE_AUDIT", plan.Steps[^1].Sql);
        Assert.All(plan.Steps, s => Assert.True(OracleSession.IsDdlStatement(s.Sql), s.Sql));
    }

    [Fact]
    public void A_taken_name_is_a_problem()
    {
        var plan = AuditPlanner.Plan(Template, Kunde, Columns, AuditState.None, DataFacts.None, taken: ["KUNDE", "AUDIT_KUNDE"]);

        Assert.False(plan.CanRun);
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Problems, p => p.Contains("AUDIT_KUNDE"));
    }

    [Fact]
    public void Nothing_to_do_when_table_and_trigger_are_up_to_date()
    {
        var plan = AuditPlanner.Plan(Template, Kunde, Columns, UpToDate(Columns), Rows(10));

        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Problems);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_new_column_of_the_table_is_added_and_the_trigger_rewritten()
    {
        List<ColumnInfo> now = [.. Columns, Column("FAX", "VARCHAR2", length: 30, position: 4, comment: "Fax")];

        var plan = AuditPlanner.Plan(Template, Kunde, now, UpToDate(Columns), Rows(10));

        Assert.True(plan.CanRun, string.Join("\n", plan.Problems));
        Assert.Equal([DdlStepKind.AddColumn, DdlStepKind.Comment, DdlStepKind.PlSqlObject], plan.Steps.Select(s => s.Kind));
        Assert.Contains("FAX", plan.Steps[0].Sql);
        Assert.Contains(":new.FAX", plan.Steps[2].Sql);
    }

    [Fact]
    public void A_new_mandatory_column_is_nullable_in_a_filled_audit_table_only()
    {
        List<ColumnInfo> now = [.. Columns, Column("ART", "CHAR", length: 1, nullable: false, position: 4)];

        var filled = AuditPlanner.Plan(Template, Kunde, now, UpToDate(Columns), Rows(10));
        var empty = AuditPlanner.Plan(Template, Kunde, now, UpToDate(Columns), Rows(0));
        var unknown = AuditPlanner.Plan(Template, Kunde, now, UpToDate(Columns), DataFacts.None);

        Assert.True(filled.CanRun, string.Join("\n", filled.Problems));
        Assert.DoesNotContain("NOT NULL", filled.Steps[0].Sql);
        Assert.Contains(filled.Warnings, w => w.Contains("ART"));
        Assert.Contains("NOT NULL", empty.Steps[0].Sql);
        Assert.DoesNotContain("NOT NULL", unknown.Steps[0].Sql);
    }

    [Fact]
    public void A_dropped_column_stays_in_the_audit_table_and_becomes_nullable()
    {
        List<ColumnInfo> now = [Columns[0], Columns[1]]; // STATUS (NOT NULL) dropped

        var plan = AuditPlanner.Plan(Template, Kunde, now, UpToDate(Columns), Rows(10));

        Assert.True(plan.CanRun, string.Join("\n", plan.Problems));
        Assert.DoesNotContain(plan.Steps, s => s.Kind == DdlStepKind.DropColumn);
        Assert.Contains(plan.Steps, s => s.Kind == DdlStepKind.ModifyNull && s.Sql.Contains("STATUS"));
        Assert.Contains(plan.Warnings, w => w.Contains("STATUS"));
        Assert.DoesNotContain("STATUS", plan.Steps.Single(s => s.Kind == DdlStepKind.PlSqlObject).Sql);
    }

    [Fact]
    public void Types_only_grow()
    {
        List<ColumnInfo> wider = [Columns[0], Columns[1] with { Length = 120 }, Columns[2]];
        List<ColumnInfo> narrower = [Columns[0], Columns[1] with { Length = 40 }, Columns[2]];

        var grow = AuditPlanner.Plan(Template, Kunde, wider, UpToDate(Columns), Rows(10));
        var shrink = AuditPlanner.Plan(Template, Kunde, narrower, UpToDate(Columns), Rows(10));

        Assert.Equal([DdlStepKind.ModifyType], grow.Steps.Select(s => s.Kind));
        Assert.Contains("120", grow.Steps[0].Sql);
        Assert.Empty(shrink.Steps); // the audit column holds the shorter values as it is
        Assert.Empty(shrink.Problems);
        Assert.Equal(AuditHealth.UpToDate, AuditStatus.Of(Template, Kunde, narrower, UpToDate(Columns)).Health);
    }

    /// <summary>R5 review: DATE → TIMESTAMP is growth (ChangeOf is Free both ways, but DATE loses the fractions).</summary>
    [Fact]
    public void Date_grows_to_timestamp_not_back()
    {
        var date = Column("GEAENDERT", "DATE", position: 4);
        var timestamp = date with { DataType = "TIMESTAMP(6)", Scale = 6 };

        var grow = AuditPlanner.Plan(Template, Kunde, [.. Columns, timestamp], UpToDate([.. Columns, date]), Rows(10));
        var back = AuditPlanner.Plan(Template, Kunde, [.. Columns, date], UpToDate([.. Columns, timestamp]), Rows(10));

        Assert.Contains("TIMESTAMP(6)", Assert.Single(grow.Steps, s => s.Kind == DdlStepKind.ModifyType).Sql);
        Assert.Empty(back.Steps);
        Assert.Empty(back.Problems);
    }

    /// <summary>
    /// R5 review: an audit column that cannot take the table's values would make the trigger fail every DML – a problem,
    /// and the status says outdated instead of up to date.
    /// </summary>
    [Fact]
    public void An_audit_column_of_another_type_family_is_a_problem()
    {
        List<ColumnInfo> text = [Columns[0] with { DataType = "VARCHAR2", Length = 20, Precision = null, Scale = null }, Columns[1], Columns[2]];

        var plan = AuditPlanner.Plan(Template, Kunde, text, UpToDate(Columns), Rows(10));
        var status = AuditStatus.Of(Template, Kunde, text, UpToDate(Columns));

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Problems, p => p.Contains("KUNDE_ID") && p.Contains("NUMBER(10)") && p.Contains("VARCHAR2(20"));
        Assert.Equal(AuditHealth.Outdated, status.Health);
        Assert.Contains(status.Reasons, r => r.Contains("KUNDE_ID"));
    }

    /// <summary>R5 review: a rename onto a name the audit table has already is not done silently.</summary>
    [Fact]
    public void A_rename_onto_an_existing_audit_column_is_reported()
    {
        var withOld = UpToDate(Columns) with
        {
            AuditTable = Table("AUDIT_KUNDE", [.. Template.TableFor(Kunde, Columns).Columns, Column("NACHNAME", "VARCHAR2", length: 80, position: 9)]),
        };
        List<ColumnInfo> now = [Columns[0], Columns[1] with { Name = "NACHNAME" }, Columns[2]];

        var plan = AuditPlanner.Plan(Template, Kunde, now, withOld, Rows(10), renamed: new Dictionary<string, string> { ["NAME"] = "NACHNAME" });

        Assert.DoesNotContain(plan.Steps, s => s.Kind == DdlStepKind.RenameColumn);
        Assert.Contains(plan.Warnings, w => w.Contains("NAME → NACHNAME"));
    }

    [Fact]
    public void A_column_the_designer_renames_is_renamed_in_the_audit_table()
    {
        List<ColumnInfo> now = [Columns[0], Columns[1] with { Name = "NACHNAME" }, Columns[2]];

        var plan = AuditPlanner.Plan(Template, Kunde, now, UpToDate(Columns), Rows(10), renamed: new Dictionary<string, string> { ["NAME"] = "NACHNAME" });

        Assert.True(plan.CanRun, string.Join("\n", plan.Problems));
        Assert.Contains(plan.Steps, s => s.Kind == DdlStepKind.RenameColumn && s.Sql.Contains("NAME") && s.Sql.Contains("NACHNAME"));
        Assert.DoesNotContain(plan.Steps, s => s.Kind == DdlStepKind.AddColumn);
        Assert.Contains(":new.NACHNAME", plan.Steps[^1].Sql);
    }

    [Fact]
    public void An_invalid_trigger_is_rewritten_a_disabled_up_to_date_one_only_reported()
    {
        var invalid = UpToDate(Columns) with { Trigger = new AuditTriggerState(Kunde, false, true, []) };
        var disabled = UpToDate(Columns) with { Trigger = UpToDate(Columns).Trigger! with { Enabled = false } };

        var rewrite = AuditPlanner.Plan(Template, Kunde, Columns, invalid, Rows(10));
        var report = AuditPlanner.Plan(Template, Kunde, Columns, disabled, Rows(10));

        Assert.Equal([DdlStepKind.PlSqlObject], rewrite.Steps.Select(s => s.Kind));
        Assert.Empty(report.Steps);
        Assert.Contains(report.Warnings, w => w.Contains("KUNDE_AUDIT"));
        Assert.Single(AuditPlanner.Plan(Template, Kunde, Columns, disabled, Rows(10), rewriteTrigger: true).Steps);
    }

    [Fact]
    public void A_trigger_of_that_name_on_another_table_is_a_problem()
    {
        var clash = UpToDate(Columns) with { Trigger = new AuditTriggerState(new TableRef("APP", "LIEFERANT"), true, true, ["X"]) };

        var plan = AuditPlanner.Plan(Template, Kunde, Columns, clash, Rows(10));

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Problems, p => p.Contains("KUNDE_AUDIT") && p.Contains("APP.LIEFERANT"));
    }

    [Fact]
    public void A_changed_table_comment_and_column_comment_follow()
    {
        List<ColumnInfo> now = [Columns[0], Columns[1] with { Comment = "Surname" }, Columns[2]];
        var state = UpToDate(Columns) with { AuditTableComment = "old" };

        var plan = AuditPlanner.Plan(Template, Kunde, now, state, Rows(10));

        Assert.Equal([DdlStepKind.Comment, DdlStepKind.Comment], plan.Steps.Select(s => s.Kind));
        Assert.Contains(plan.Steps, s => s.Sql.Contains("Surname"));
        Assert.Contains(plan.Steps, s => s.Sql.Contains("COMMENT ON TABLE"));
    }

    [Fact]
    public void Combined_with_the_designer_the_table_changes_before_its_audit_table_and_the_trigger_comes_last()
    {
        var current = Table("KUNDE", Columns);
        var draft = new TableDraft([.. TableDraft.Of(current).Columns, new ColumnDraft(null, Column("FAX", "VARCHAR2", length: 30, position: 4))]);
        var tablePlan = TableDesign.Plan(DesignContext.Of(current), draft, Rows(10));
        var auditPlan = AuditPlanner.Plan(Template, Kunde, [.. draft.Columns.Select(c => c.Column)], UpToDate(Columns), Rows(10));

        var plan = AuditPlanner.Combine(tablePlan, auditPlan);

        Assert.Equal(
            [("AUDIT_KUNDE", DdlStepKind.AddColumn), ("KUNDE", DdlStepKind.AddColumn), ("KUNDE", DdlStepKind.PlSqlObject)],
            plan.Steps.Select(s => (s.Object, s.Kind)));
    }

    [Fact]
    public void After_the_draft_columns_are_numbered_again_and_renames_known()
    {
        var current = Table("KUNDE", Columns);
        var draft = new TableDraft(
        [
            new ColumnDraft("KUNDE_ID", Columns[0]),
            new ColumnDraft("NAME", Columns[1] with { Name = " NACHNAME " }),
            new ColumnDraft("STATUS", Columns[2], Dropped: true),
            new ColumnDraft(null, Column("FAX", "VARCHAR2", length: 30, position: 0)),
        ]);

        var (columns, renamed) = AuditPlanner.After(draft);

        Assert.Equal([("KUNDE_ID", 1), ("NACHNAME", 2), ("FAX", 3)], columns.Select(c => (c.Name, c.Position)));
        Assert.Equal(new Dictionary<string, string> { ["NAME"] = "NACHNAME" }, renamed);
        Assert.Equal(3, current.Columns.Count); // the table itself is untouched
    }

    [Fact]
    public void The_status_is_none_up_to_date_or_outdated_with_reasons()
    {
        List<ColumnInfo> now = [.. Columns, Column("FAX", "VARCHAR2", length: 30, position: 4)];

        Assert.Equal(AuditHealth.None, AuditStatus.Of(Template, Kunde, Columns, AuditState.None).Health);
        Assert.Equal(AuditHealth.UpToDate, AuditStatus.Of(Template, Kunde, Columns, UpToDate(Columns)).Health);

        var outdated = AuditStatus.Of(Template, Kunde, now, UpToDate(Columns));
        Assert.Equal(AuditHealth.Outdated, outdated.Health);
        Assert.Equal(["FAX fehlt in der Audit-Tabelle", "Trigger KUNDE_AUDIT füllt nicht alle Spalten"], outdated.Reasons);

        var invalid = AuditStatus.Of(Template, Kunde, Columns, UpToDate(Columns) with { Trigger = new AuditTriggerState(Kunde, false, true, []) });
        Assert.Equal(["Trigger KUNDE_AUDIT ist ungültig"], invalid.Reasons);
        var onlyTrigger = AuditStatus.Of(Template, Kunde, Columns, UpToDate(Columns) with { AuditTable = null });
        Assert.Equal(["Audit-Tabelle AUDIT_KUNDE fehlt"], onlyTrigger.Reasons);
        var comment = AuditStatus.Of(Template, Kunde, Columns, UpToDate(Columns) with { AuditTableComment = "alt" });
        Assert.Equal(["Typen oder Kommentare weichen ab"], comment.Reasons);
    }

    [Fact]
    public void An_audit_table_gets_no_audit_table_offered()
    {
        TableRef[] tables = [Kunde, new("APP", "AUDIT_KUNDE"), new("APP", "LIEFERANT")];

        Assert.True(AuditStatus.IsAuditTable(Template, new TableRef("APP", "AUDIT_KUNDE"), tables));
        Assert.False(AuditStatus.IsAuditTable(Template, Kunde, tables));
        Assert.False(AuditStatus.IsAuditTable(Template, new TableRef("APP", "AUDIT_LIEFERANT"), tables[..2]));
    }
}
