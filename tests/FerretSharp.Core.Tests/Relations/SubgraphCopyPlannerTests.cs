using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.Relations.MemoryTables;

namespace FerretSharp.Core.Tests.Relations;

public class SubgraphCopyPlannerTests
{
    private const string Target = "TEST";

    private readonly MemoryTables _source = new();
    private readonly MemoryTables _target = new();
    private readonly TableDetails _kunde;
    private readonly TableDetails _auftrag;
    private readonly TableDetails _mitarbeiter;
    private readonly ForeignKeyInfo _auftragKunde;
    private readonly ForeignKeyInfo _auftragBearbeiter;
    private readonly ForeignKeyInfo _vorgesetzter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SubgraphCopyPlannerTests()
    {
        _kunde = _source.Add(Table("KUNDE", ["ID"], Col("ID", nullable: false), Col("NAME", "VARCHAR2", length: 50)),
            [1m, "Peters Pizza"]);
        _mitarbeiter = _source.Add(Table("MITARBEITER", ["ID"], Col("ID", nullable: false), Col("VORGESETZTER_ID")),
            [10m, null], [11m, 10m], [12m, 11m]);
        _auftrag = _source.Add(Table("AUFTRAG", ["ID"], Col("ID", nullable: false), Col("KUNDE_ID"), Col("BEARBEITER_ID")),
            [100m, 1m, 12m], [101m, 1m, null]);
        _auftragKunde = new("FK_AUFTRAG_KUNDE", _auftrag.Table.Ref, ["KUNDE_ID"], _kunde.Table.Ref, ["ID"], FkSource.Declared);
        _auftragBearbeiter = new("Auftrag.Bearbeiter", _auftrag.Table.Ref, ["BEARBEITER_ID"], _mitarbeiter.Table.Ref, ["ID"], FkSource.ClrModel);
        _vorgesetzter = new("FK_MA_VORGESETZTER", _mitarbeiter.Table.Ref, ["VORGESETZTER_ID"], _mitarbeiter.Table.Ref, ["ID"], FkSource.Declared);
    }

    /// <summary>The orders of customer 1 with their parents, as the copy collects them.</summary>
    private async Task<RowGraph> CollectAsync()
    {
        var relations = new RelationGraph([_auftragKunde, _auftragBearbeiter, _vorgesetzter]);
        var traversal = new RowTraversal(_source.Data, relations, _source.DetailsOf, wholeLobs: true);
        return await traversal.CollectAsync(_kunde, [new RowKey.PrimaryKey([1m])], TraversalPlan.Default(relations, _kunde.Table.Ref), null, Ct);
    }

    private async Task<SubgraphCopyPlan> PlanAsync(IReadOnlyDictionary<TableRef, IReadOnlyList<TableRef>>? triggers = null)
    {
        var schema = await _target.SchemaAsync(Target, triggers);
        return await new SubgraphCopyPlanner("APP", schema, _target.Data).PlanAsync(await CollectAsync(), Ct);
    }

    /// <summary>The source tables, empty, in the target schema.</summary>
    private void SameTablesInTarget()
    {
        _target.Add(In(Target, _kunde));
        _target.Add(In(Target, _mitarbeiter));
        _target.Add(In(Target, _auftrag));
    }

    private static IEnumerable<object?> Ids(TableCopy table) => table.Rows.Select(r => r.Values[0]);

    private static readonly Dictionary<TableRef, ConflictMode> Defaults = [];

    [Fact]
    public async Task Tables_go_to_the_target_schema_parents_first_with_all_columns()
    {
        SameTablesInTarget();

        var plan = await PlanAsync();

        Assert.Equal(["KUNDE", "MITARBEITER", "AUFTRAG"], plan.Tables.Select(t => t.Table.Name));
        Assert.All(plan.Tables, t => Assert.Equal(Target, t.Target!.Table.Owner));
        Assert.Equal([new ColumnMapping(0, 0), new ColumnMapping(1, 1), new ColumnMapping(2, 2)], plan.Tables[2].Columns);
        Assert.Equal(6, plan.Rows);
        Assert.Empty(plan.Issues);
        Assert.True(plan.CanCopy(Defaults));
    }

    [Fact]
    public async Task The_preview_as_a_diagram_puts_each_table_right_of_its_parents_without_self_references()
    {
        SameTablesInTarget();
        var graph = await CollectAsync();
        var plan = await new SubgraphCopyPlanner("APP", await _target.SchemaAsync(Target), _target.Data).PlanAsync(graph, Ct);

        var (nodes, edges) = CopyPreviewDiagram.ToLayout(plan, graph);

        Assert.Equal([("t:APP.KUNDE", 0), ("t:APP.MITARBEITER", 0), ("t:APP.AUFTRAG", 1)], nodes.Select(n => (n.Id, n.Layer)));
        Assert.Equal([("t:APP.MITARBEITER", "t:APP.AUFTRAG"), ("t:APP.KUNDE", "t:APP.AUFTRAG")], edges.Select(e => (e.From, e.To)));
    }

    [Fact]
    public async Task Rows_of_a_self_reference_come_after_the_rows_they_reference()
    {
        SameTablesInTarget();

        var plan = await PlanAsync();

        Assert.Equal([10m, 11m, 12m], Ids(plan.Tables.Single(t => t.Table.Name == "MITARBEITER")));
    }

    [Fact]
    public async Task Rows_whose_key_the_target_has_are_existing_and_skipped_by_default()
    {
        _target.Add(In(Target, _kunde), [1m, "Peters Pizza (Test)"]);
        _target.Add(In(Target, _mitarbeiter), [10m, null], [11m, 10m], [12m, 11m]);
        _target.Add(In(Target, _auftrag));

        var plan = await PlanAsync();

        var kunde = plan.Tables[0];
        Assert.Equal([new RowKey.PrimaryKey([1m])], kunde.Existing);
        Assert.Equal(0, kunde.NewRows);
        Assert.True(kunde.CanUpdate);
        Assert.Equal(2, plan.Tables[2].NewRows);
        Assert.True(plan.CanCopy(Defaults));
        Assert.Equal(ConflictMode.Skip, SubgraphCopyPlan.ModeOf(kunde, Defaults));
    }

    [Fact]
    public async Task A_row_with_the_same_key_but_other_values_in_the_target_is_a_warning()
    {
        _target.Add(In(Target, _kunde), [1m, "Anderer Kunde"]);
        _target.Add(In(Target, _mitarbeiter), [10m, null], [11m, 10m], [12m, 11m]); // same values
        _target.Add(In(Target, _auftrag));

        var plan = await PlanAsync();

        Assert.Equal([new RowKey.PrimaryKey([1m])], plan.Tables[0].Differing);
        Assert.Empty(plan.Tables[1].Differing);
        var issue = Assert.Single(plan.Issues);
        Assert.Equal(CopySeverity.Warning, issue.Severity);
        Assert.Equal(
            "KUNDE: 1 Zeile hat im Ziel denselben Schlüssel, aber andere Werte in NAME – womöglich ein anderer Datensatz. Beim Überspringen verweisen mitkopierte Zeilen auf diesen.",
            issue.Message); // the columns are named: some differ between any two databases and say nothing
        Assert.True(plan.CanCopy(Defaults));
    }

    [Fact]
    public async Task A_new_row_whose_unique_key_the_target_holds_under_another_id_is_an_error()
    {
        _target.Add(In(Target, _kunde with { UniqueKeys = [["NAME"]] }), [7m, "Peters Pizza"]);
        _target.Add(In(Target, _mitarbeiter));
        _target.Add(In(Target, _auftrag));

        var plan = await PlanAsync();

        Assert.Empty(plan.Tables[0].Existing); // by its primary key the row is new
        var issue = Assert.Single(plan.Issues);
        Assert.Equal(CopySeverity.Error, issue.Severity);
        Assert.StartsWith("KUNDE: 1 neue Zeile hat in NAME Werte, die eine Zeile des Ziels schon unter anderem Schlüssel hat", issue.Message);
        Assert.False(plan.CanCopy(Defaults));
    }

    [Fact]
    public async Task Abort_with_existing_rows_is_an_error_and_nothing_new_is_nothing_to_copy()
    {
        _target.Add(In(Target, _kunde), [1m, "Peters Pizza"]);
        _target.Add(In(Target, _mitarbeiter), [10m, null], [11m, 10m], [12m, 11m]);
        _target.Add(In(Target, _auftrag), [100m, 1m, 12m], [101m, 1m, null]);
        var plan = await PlanAsync();
        var abort = new Dictionary<TableRef, ConflictMode> { [_kunde.Table.Ref] = ConflictMode.Abort };

        Assert.False(plan.CanCopy(Defaults)); // all there: nothing to write
        Assert.True(plan.CanCopy(new Dictionary<TableRef, ConflictMode> { [_kunde.Table.Ref] = ConflictMode.Update }));
        Assert.False(plan.CanCopy(abort));
        Assert.Equal("KUNDE: 1 Zeile gibt es im Ziel schon, und bei Konflikten ist „Abbrechen“ gewählt.", Assert.Single(plan.IssuesWith(abort)).Message);
    }

    [Fact]
    public async Task A_table_missing_in_the_target_stops_the_copy()
    {
        _target.Add(In(Target, _kunde));
        _target.Add(In(Target, _mitarbeiter));

        var plan = await PlanAsync();

        var issue = Assert.Single(plan.Issues);
        Assert.Equal(CopySeverity.Error, issue.Severity);
        Assert.Equal("AUFTRAG lässt sich nicht kopieren: Im Ziel gibt es keine Tabelle TEST.AUFTRAG.", issue.Message);
        Assert.False(plan.CanCopy(Defaults));
    }

    [Fact]
    public async Task Columns_are_matched_by_name_and_differences_are_reported()
    {
        _target.Add(In(Target, _kunde with
        {
            Columns = [Col("ID", nullable: false), Col("NAME", "VARCHAR2", length: 30), Col("MANDANT", nullable: false)],
        }));
        _target.Add(In(Target, _mitarbeiter with { Columns = [_mitarbeiter.Columns[0]] }));
        _target.Add(In(Target, _auftrag with { Columns = [_auftrag.Columns[1], _auftrag.Columns[0], _auftrag.Columns[2]] }));

        var plan = await PlanAsync();

        Assert.Equal(
        [
            (CopySeverity.Error, "Die Spalte KUNDE.MANDANT gibt es nur im Ziel, NOT NULL ohne Default: Keine Zeile lässt sich einfügen."),
            (CopySeverity.Warning, "Die Spalte KUNDE.NAME ist in der Quelle VARCHAR2(50) und im Ziel VARCHAR2(30): Werte passen womöglich nicht."),
            (CopySeverity.Warning, "Die Spalte MITARBEITER.VORGESETZTER_ID gibt es im Ziel nicht: Ihre Werte werden nicht kopiert."),
        ], plan.Issues.Select(i => (i.Severity, i.Message)));
        Assert.Equal([new ColumnMapping(0, 1), new ColumnMapping(1, 0), new ColumnMapping(2, 2)], plan.Tables[2].Columns);
    }

    [Fact]
    public async Task An_identity_generated_always_cannot_keep_its_key_and_takes_no_other_value()
    {
        _target.Add(In(Target, _kunde with { Columns = [Col("ID", nullable: false) with { IsIdentity = true, IdentityAlways = true }, _kunde.Columns[1]] }));
        _target.Add(In(Target, _mitarbeiter));
        _target.Add(In(Target, _auftrag with { Columns = [_auftrag.Columns[0], _auftrag.Columns[1], _auftrag.Columns[2] with { IsIdentity = true, IdentityAlways = true }] }));

        var plan = await PlanAsync();

        Assert.Equal(
        [
            (CopySeverity.Error, "Die Schlüsselspalte KUNDE.ID ist im Ziel eine Identity GENERATED ALWAYS: Die Zeilen können ihre Schlüssel nicht behalten."),
            (CopySeverity.Warning, "Die Spalte AUFTRAG.BEARBEITER_ID ist im Ziel eine Identity GENERATED ALWAYS: Das Ziel vergibt neue Werte."),
        ], plan.Issues.Select(i => (i.Severity, i.Message)));
    }

    [Fact]
    public async Task Without_a_primary_key_a_unique_key_finds_existing_rows_but_cannot_update()
    {
        _target.Add(In(Target, _kunde with { PrimaryKey = [], UniqueKeys = [["NAME"]] }), [7m, "Peters Pizza"]);
        _target.Add(In(Target, _mitarbeiter with { PrimaryKey = [] }));
        _target.Add(In(Target, _auftrag));

        var plan = await PlanAsync();

        var kunde = plan.Tables[0];
        Assert.Single(kunde.Existing);
        Assert.False(kunde.CanUpdate);
        Assert.Equal(ConflictMode.Skip, SubgraphCopyPlan.ModeOf(kunde, new Dictionary<TableRef, ConflictMode> { [kunde.Table] = ConflictMode.Update }));
        Assert.Equal(
            [
                "KUNDE: 1 Zeile hat im Ziel denselben Schlüssel, aber andere Werte in ID – womöglich ein anderer Datensatz. Beim Überspringen verweisen mitkopierte Zeilen auf diesen.",
                "MITARBEITER hat im Ziel keinen Primär- oder Unique-Schlüssel: Zeilen werden immer eingefügt, vorhandene nicht erkannt.",
            ],
            plan.Issues.Select(i => i.Message)); // the same name under ID 7: the ID is another value
    }

    [Fact]
    public async Task Triggers_on_a_target_table_are_a_hint()
    {
        SameTablesInTarget();
        var auftrag = new TableRef(Target, "AUFTRAG");

        var plan = await PlanAsync(new Dictionary<TableRef, IReadOnlyList<TableRef>>
        {
            [auftrag] = [new(Target, "TRG_AUFTRAG_AUDIT"), new("AUDIT", "TRG_AUFTRAG_LOG")],
        });

        var hint = Assert.Single(plan.Issues);
        Assert.Equal(CopySeverity.Hint, hint.Severity);
        Assert.Equal("Trigger auf AUFTRAG im Ziel laufen beim Kopieren mit: TRG_AUFTRAG_AUDIT, AUDIT.TRG_AUFTRAG_LOG.", hint.Message);
        Assert.True(plan.CanCopy(Defaults));
    }

    [Fact]
    public async Task The_write_inserts_new_rows_table_by_table_and_skips_existing_ones()
    {
        _target.Add(In(Target, _kunde), [1m, "x"]);
        _target.Add(In(Target, _mitarbeiter));
        _target.Add(In(Target, _auftrag));
        var plan = await PlanAsync();

        var write = plan.ToWrite(Defaults, "DEV", lockWaitSeconds: 5);

        Assert.Equal("Kopie aus DEV: 5 Zeilen", write.Description);
        Assert.Equal(5, write.LockWaitSeconds);
        Assert.Equal(5, write.Rows);
        var inserts = write.Steps.Cast<CopyInsert>().ToList();
        Assert.Equal(["MITARBEITER", "AUFTRAG"], inserts.Select(i => i.Table.Table.Name));
        Assert.All(inserts, i => Assert.Equal(Target, i.Table.Table.Owner));
        Assert.Equal("INSERT INTO \"TEST\".\"MITARBEITER\" (\"ID\", \"VORGESETZTER_ID\")\nVALUES (:v0, :v1)", inserts[0].Batch.Sql);
        Assert.Equal([10m, 11m, 12m], inserts[0].Batch.Parameters[0].Values);
        Assert.Equal([null, 10m, 11m], inserts[0].Batch.Parameters[1].Values);
    }

    [Fact]
    public async Task Update_overwrites_existing_rows_by_the_targets_primary_key_after_the_new_rows_of_the_table()
    {
        _target.Add(In(Target, _kunde), [1m, "x"]);
        _target.Add(In(Target, _mitarbeiter), [10m, null]);
        _target.Add(In(Target, _auftrag));
        var plan = await PlanAsync();

        var write = plan.ToWrite(new Dictionary<TableRef, ConflictMode> { [_kunde.Table.Ref] = ConflictMode.Update, [_mitarbeiter.Table.Ref] = ConflictMode.Update }, "DEV", 3);

        // MITARBEITER 10 exists, 11 and 12 are new: an update that pointed 10 at a new manager must find it inserted.
        Assert.Equal(["CopyUpdate KUNDE", "CopyInsert MITARBEITER", "CopyUpdate MITARBEITER", "CopyInsert AUFTRAG"],
            write.Steps.Select(s => $"{s.GetType().Name} {s.Table.Table.Name}"));
        var kunde = (CopyUpdate)write.Steps[0];
        Assert.Equal(new RowKey.PrimaryKey([1m]), kunde.Key);
        Assert.Equal(new Dictionary<int, object?> { [1] = "Peters Pizza" }, kunde.Values);
        Assert.Equal(6, write.Rows);
    }

    [Fact]
    public async Task Many_rows_go_in_blocks_and_a_copy_that_cannot_run_is_refused()
    {
        _source.Add(_kunde, [1m, "Peters Pizza"]);
        _source.Add(_auftrag, [.. Enumerable.Range(0, SubgraphCopyPlan.MaxBatchRows + 1).Select(i => new object?[] { (decimal)(1000 + i), 1m, null })]);
        SameTablesInTarget();
        var plan = await PlanAsync();

        var write = plan.ToWrite(Defaults, "DEV", 3);

        Assert.Equal([1, SubgraphCopyPlan.MaxBatchRows, 1], write.Steps.Cast<CopyInsert>().Select(i => i.Batch.Rows));
    }

    [Fact]
    public async Task A_copy_that_cannot_run_is_not_written()
    {
        _target.Add(In(Target, _kunde));

        var plan = await PlanAsync();

        Assert.Throws<InvalidOperationException>(() => plan.ToWrite(Defaults, "DEV", 3));
    }

    /// <summary>The orders of customer 1 without the clerks: the relationship to MITARBEITER switched off.</summary>
    private async Task<SubgraphCopyPlan> PlanWithoutClerksAsync(IReadOnlyList<ForeignKeyInfo>? targetForeignKeys = null)
    {
        var relations = new RelationGraph([_auftragKunde, _auftragBearbeiter, _vorgesetzter]);
        var skipped = new HashSet<string> { RelationGraph.KeyOf(_auftragBearbeiter) };
        var traversal = new RowTraversal(_source.Data, relations, _source.DetailsOf, wholeLobs: true);
        var graph = await traversal.CollectAsync(_kunde, [new RowKey.PrimaryKey([1m])], TraversalPlan.Default(relations, _kunde.Table.Ref) with { SkippedParents = skipped }, null, Ct);
        var schema = await _target.SchemaAsync(Target, foreignKeys: targetForeignKeys);
        return await new SubgraphCopyPlanner("APP", schema, _target.Data).PlanAsync(graph, [_auftragBearbeiter], Ct);
    }

    [Fact]
    public async Task A_switched_off_parent_missing_in_the_target_is_an_error_where_the_target_has_the_constraint()
    {
        SameTablesInTarget();
        var declared = new ForeignKeyInfo("FK_AUFTRAG_BEARBEITER", new TableRef(Target, "AUFTRAG"), ["BEARBEITER_ID"], new TableRef(Target, "MITARBEITER"), ["ID"], FkSource.Declared);

        var plan = await PlanWithoutClerksAsync([declared]);

        Assert.Null(plan.Tables.SingleOrDefault(t => t.Table.Name == "MITARBEITER"));
        var issue = Assert.Single(plan.Issues);
        Assert.Equal(CopySeverity.Error, issue.Severity);
        Assert.Equal("AUFTRAG: 1 Zeile von MITARBEITER, auf die verwiesen wird, fehlt im Ziel, und die Beziehung Auftrag.Bearbeiter ist abgewählt – der Constraint des Ziels lehnt das Einfügen ab.", issue.Message);
    }

    [Fact]
    public async Task A_switched_off_parent_missing_without_a_constraint_is_a_warning_and_one_the_target_has_is_fine()
    {
        SameTablesInTarget();

        var plan = await PlanWithoutClerksAsync();

        Assert.Equal(CopySeverity.Warning, Assert.Single(plan.Issues).Severity);

        _target.Add(In(Target, _mitarbeiter), [12m, null]);
        Assert.Empty((await PlanWithoutClerksAsync()).Issues);
    }

    [Fact]
    public async Task A_switched_off_parent_whose_rows_come_along_by_another_way_still_orders_the_tables()
    {
        // AUFGABE and PERSONAL are both children of the site; AUFGABE → PERSONAL is switched off, but the staff come along as
        // children. By name AUFGABE would go first and its insert fail on the target's constraint.
        var standort = _source.Add(Table("STANDORT", ["ID"], Col("ID", nullable: false)), [1m]);
        var personal = _source.Add(Table("PERSONAL", ["ID"], Col("ID", nullable: false), Col("STANDORT_ID")), [7m, 1m]);
        var aufgabe = _source.Add(Table("AUFGABE", ["ID"], Col("ID", nullable: false), Col("STANDORT_ID"), Col("PERSONAL_ID")), [70m, 1m, 7m]);
        ForeignKeyInfo personalStandort = new("FK_PERSONAL_STANDORT", personal.Table.Ref, ["STANDORT_ID"], standort.Table.Ref, ["ID"], FkSource.Declared);
        ForeignKeyInfo aufgabeStandort = new("FK_AUFGABE_STANDORT", aufgabe.Table.Ref, ["STANDORT_ID"], standort.Table.Ref, ["ID"], FkSource.Declared);
        ForeignKeyInfo aufgabePersonal = new("FK_AUFGABE_PERSONAL", aufgabe.Table.Ref, ["PERSONAL_ID"], personal.Table.Ref, ["ID"], FkSource.Declared);
        _target.Add(In(Target, standort));
        _target.Add(In(Target, personal));
        _target.Add(In(Target, aufgabe));
        var relations = new RelationGraph([personalStandort, aufgabeStandort, aufgabePersonal]);
        var traversal = new RowTraversal(_source.Data, relations, _source.DetailsOf, wholeLobs: true);
        var plan = TraversalPlan.Default(relations, standort.Table.Ref) with { SkippedParents = new HashSet<string> { RelationGraph.KeyOf(aufgabePersonal) } };
        var graph = await traversal.CollectAsync(standort, [new RowKey.PrimaryKey([1m])], plan, null, Ct);
        var schema = await _target.SchemaAsync(Target);

        var copy = await new SubgraphCopyPlanner("APP", schema, _target.Data).PlanAsync(graph, [aufgabePersonal], Ct);

        Assert.Equal(["STANDORT", "PERSONAL", "AUFGABE"], copy.Tables.Select(t => t.Table.Name));
        Assert.Empty(copy.Issues);
    }

    [Fact]
    public async Task Values_of_another_type_in_the_target_are_converted_and_those_that_do_not_convert_are_an_error()
    {
        var ort = _source.Add(Table("ORT", ["ID"], Col("ID", nullable: false), Col("PLZ"), Col("CODE", "VARCHAR2", length: 10)),
            [1m, 12345m, "7"], [2m, 4711m, "x"]);
        _target.Add(In(Target, Table("ORT", ["ID"], Col("ID", nullable: false), Col("PLZ", "VARCHAR2", length: 10), Col("CODE"))));
        var schema = await _target.SchemaAsync(Target);
        var traversal = new RowTraversal(_source.Data, new RelationGraph([]), _source.DetailsOf, wholeLobs: true);
        async Task<SubgraphCopyPlan> PlanOf(params decimal[] ids) => await new SubgraphCopyPlanner("APP", schema, _target.Data).PlanAsync(
            await traversal.CollectAsync(ort, [.. ids.Select(id => (RowKey)new RowKey.PrimaryKey([id]))], TraversalPlan.ParentsOnly, null, Ct), Ct);

        var plan = await PlanOf(1m, 2m);

        Assert.Equal([new ColumnMapping(0, 0), new ColumnMapping(1, 1, Convert: true), new ColumnMapping(2, 2, Convert: true)], plan.Tables[0].Columns);
        var error = Assert.Single(plan.Issues, i => i.Severity == CopySeverity.Error);
        Assert.Equal("ORT: 1 Wert der Spalte CODE lässt sich nicht in NUMBER(10) des Ziels umwandeln.", error.Message);
        Assert.False(plan.CanCopy(Defaults));

        var insert = Assert.IsType<CopyInsert>(Assert.Single((await PlanOf(1m)).ToWrite(Defaults, "DEV", 3).Steps));
        Assert.Equal(["12345"], insert.Batch.Parameters[1].Values);
        Assert.Equal([7m], insert.Batch.Parameters[2].Values);
    }

    [Fact]
    public async Task Tables_of_other_schemas_keep_their_owner()
    {
        var schema = await _target.SchemaAsync(Target);
        var planner = new SubgraphCopyPlanner("APP", schema, _target.Data);

        Assert.Equal(new TableRef(Target, "KUNDE"), planner.TargetOf(new TableRef("APP", "KUNDE")));
        Assert.Equal(new TableRef("STAMM", "LAND"), planner.TargetOf(new TableRef("STAMM", "LAND")));
    }
}
