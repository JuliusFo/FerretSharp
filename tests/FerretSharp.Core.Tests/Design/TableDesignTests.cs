using FerretSharp.Core.Ddl;
using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Design;

public class TableDesignTests
{
    private const string T = "\"SHOP\".\"KUNDE\"";

    private static readonly ColumnInfo Id = new("ID", "NUMBER", null, false, 10, 0, false, false, null, 1);
    private static readonly ColumnInfo Name = new("NAME", "VARCHAR2", 50, true, null, null, true, false, null, 2);
    private static readonly ColumnInfo Status = new("STATUS", "CHAR", 1, false, null, null, false, false, "'A' ", 3, "Status");
    private static readonly ColumnInfo Umsatz = new("UMSATZ", "NUMBER", null, false, 12, 2, true, false, null, 4);
    private static readonly ColumnInfo Notiz = new("NOTIZ", "VARCHAR2", 200, false, null, null, true, false, null, 5);

    private static readonly TableDetails Kunde = new(new TableSummary("SHOP", "KUNDE", TableKind.Table), [Id, Name, Status, Umsatz, Notiz], ["ID"], [], false);

    private static ColumnDraft Same(ColumnInfo c) => new(c.Name, c);

    private static ColumnDraft Changed(ColumnInfo c, Func<ColumnInfo, ColumnInfo> change) => new(c.Name, change(c));

    private static TableDraft Draft(params ColumnDraft[] changes)
    {
        var columns = Kunde.Columns.Select(c => changes.FirstOrDefault(d => d.Original == c.Name) ?? Same(c)).ToList();
        columns.AddRange(changes.Where(d => d.IsNew));
        return new TableDraft(columns);
    }

    private static DesignPlan Plan(TableDraft draft, DataFacts? facts = null, IReadOnlyList<ConstraintInfo>? constraints = null, IReadOnlyList<ForeignKeyInfo>? incoming = null) =>
        TableDesign.Plan(new DesignContext(Kunde, constraints ?? [], [], incoming ?? []), draft, facts ?? DataFacts.None);

    private static DataFacts Facts(params (DataQuestion Question, long Count)[] counts) => new(counts.ToDictionary(c => c.Question, c => c.Count));

    private static DataQuestion Nulls(string column) => new(DataQuestionKind.Nulls, column);

    private static DataQuestion Values(string column) => new(DataQuestionKind.Values, column);

    private static readonly DataQuestion Rows = new(DataQuestionKind.Rows);

    private static ColumnInfo NewColumn(string name, string type, int? length = null, bool nullable = true, string? @default = null, string? comment = null, bool identity = false) =>
        new(name, type, length, false, type == "NUMBER" ? 10 : null, type == "NUMBER" ? 0 : null, nullable, identity, @default, 0, comment);

    [Fact]
    public void An_unchanged_draft_writes_nothing()
    {
        var plan = Plan(TableDraft.Of(Kunde));

        Assert.Empty(plan.Statements);
        Assert.Empty(plan.Problems);
        Assert.Empty(plan.Warnings);
        Assert.False(plan.CanRun);
        Assert.Empty(TableDesign.Questions(Kunde, TableDraft.Of(Kunde)));
    }

    [Fact]
    public void A_dictionary_default_with_trailing_blank_is_no_change()
    {
        var plan = Plan(Draft(Changed(Status, c => c with { Default = "'A'" })));

        Assert.Empty(plan.Statements);
    }

    // ---- new columns ----

    [Fact]
    public void A_size_or_scale_out_of_range_names_the_field_value_and_range()
    {
        Assert.Equal(["FAX: Größe 50 passt nicht zu NUMBER – erlaubt sind 1 bis 38."],
            Plan(Draft(new ColumnDraft(null, NewColumn("FAX", "NUMBER") with { Precision = 50 }))).Problems);
        Assert.Equal(["FAX: Größe 40000 passt nicht zu VARCHAR2 – erlaubt sind 1 bis 32767."],
            Plan(Draft(new ColumnDraft(null, NewColumn("FAX", "VARCHAR2", 40000)))).Problems);
        Assert.Equal(["FAX: Nachkommastellen 200 passen nicht zu NUMBER – erlaubt sind -84 bis 127."],
            Plan(Draft(new ColumnDraft(null, NewColumn("FAX", "NUMBER") with { Scale = 200 }))).Problems);
        Assert.Empty(Plan(Draft(new ColumnDraft(null, NewColumn("FAX", "FLOAT") with { Precision = 126, Scale = null }))).Problems);
    }

    [Fact]
    public void A_new_column_is_added_with_its_comment_afterwards()
    {
        var plan = Plan(Draft(new ColumnDraft(null, NewColumn("FAX", "VARCHAR2", 30, comment: "Fax-Nummer"))));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} ADD ("FAX" VARCHAR2(30 BYTE))""",
            $"""COMMENT ON COLUMN {T}."FAX" IS 'Fax-Nummer'""",
        ], plan.Statements);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void A_new_not_null_column_without_default_needs_an_empty_table()
    {
        var draft = Draft(new ColumnDraft(null, NewColumn("PFLICHT", "NUMBER", nullable: false)));

        Assert.Equal([Rows], TableDesign.Questions(Kunde, draft));
        Assert.Equal(["PFLICHT: Die Tabelle hat 3 Zeilen – eine neue NOT-NULL-Spalte braucht für sie einen Default (ORA-01758)."],
            Plan(draft, Facts((Rows, 3))).Problems);
        Assert.True(Plan(draft, Facts((Rows, 0))).CanRun);
        var unknown = Plan(draft);
        Assert.True(unknown.CanRun);
        Assert.Single(unknown.Warnings);
        Assert.Equal($"""ALTER TABLE {T} ADD ("PFLICHT" NUMBER(10) NOT NULL)""", Assert.Single(unknown.Statements));
    }

    [Fact]
    public void A_new_not_null_column_with_default_or_identity_needs_no_check()
    {
        var draft = Draft(
            new ColumnDraft(null, NewColumn("AKTIV", "CHAR", 1, nullable: false, @default: "'J'")),
            new ColumnDraft(null, NewColumn("NR", "NUMBER", nullable: false, identity: true)));

        Assert.Empty(TableDesign.Questions(Kunde, draft));
        Assert.Equal(
        [
            $"""ALTER TABLE {T} ADD ("AKTIV" CHAR(1 BYTE) DEFAULT 'J' NOT NULL)""",
            $"""ALTER TABLE {T} ADD ("NR" NUMBER(10) GENERATED BY DEFAULT AS IDENTITY)""",
        ], Plan(draft).Statements);
    }

    [Fact]
    public void A_new_column_without_length_or_type_is_a_problem()
    {
        var plan = Plan(Draft(new ColumnDraft(null, NewColumn("A", "VARCHAR2")), new ColumnDraft(null, NewColumn("B", ""))));

        Assert.Equal(["A: VARCHAR2 braucht eine Länge.", "B: Wähle einen Typ."], plan.Problems);
        Assert.Empty(plan.Statements);
    }

    // ---- names ----

    [Fact]
    public void Names_must_be_there_unique_and_without_quotes()
    {
        var plan = Plan(Draft(
            new ColumnDraft(null, NewColumn("NAME", "NUMBER")),
            new ColumnDraft(null, NewColumn("", "NUMBER")),
            new ColumnDraft(null, NewColumn("A\"B", "NUMBER"))));

        Assert.Equal(
        [
            "Jede Spalte braucht einen Namen.",
            "A\"B: Ein Name darf keine doppelten Anführungszeichen enthalten.",
            "Zwei Spalten heißen NAME.",
        ], plan.Problems);
    }

    [Fact]
    public void A_name_that_needs_quotes_is_a_warning_but_not_for_existing_ones()
    {
        var mixed = Kunde with { Columns = [.. Kunde.Columns, new ColumnInfo("Alt", "NUMBER", null, false, null, null, true, false, null, 6)] };
        var draft = new TableDraft([.. TableDraft.Of(mixed).Columns, new ColumnDraft(null, NewColumn("neu", "NUMBER"))]);

        var plan = TableDesign.Plan(DesignContext.Of(mixed), draft, DataFacts.None);

        Assert.Equal(["neu ist kein einfacher Oracle-Name; SQL und das C#-Modell müssen ihn genau so in doppelten Anführungszeichen schreiben."], plan.Warnings);
    }

    /// <summary>R5b: a reserved word warned nothing in the designer – Oracle reads DATE unquoted as the type.</summary>
    [Fact]
    public void A_reserved_word_needs_quotes()
    {
        var draft = new TableDraft([.. TableDraft.Of(Kunde).Columns, new ColumnDraft(null, NewColumn("DATE", "DATE"))]);

        var plan = TableDesign.Plan(DesignContext.Of(Kunde), draft, DataFacts.None);

        Assert.Equal(["DATE ist kein einfacher Oracle-Name; SQL und das C#-Modell müssen ihn genau so in doppelten Anführungszeichen schreiben."], plan.Warnings);
    }

    // ---- changes of existing columns ----

    [Fact]
    public void Rename_runs_before_the_changes_which_use_the_new_name()
    {
        var plan = Plan(Draft(Changed(Name, c => c with { Name = "NACHNAME", Length = 80, Comment = "Nachname" })));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} RENAME COLUMN "NAME" TO "NACHNAME" """.TrimEnd(),
            $"""ALTER TABLE {T} MODIFY ("NACHNAME" VARCHAR2(80 CHAR))""",
            $"""COMMENT ON COLUMN {T}."NACHNAME" IS 'Nachname'""",
        ], plan.Statements);
        Assert.Equal(["NAME → NACHNAME: Views, PL/SQL und das C#-Modell (HasColumnName) verwenden noch den alten Namen."], plan.Warnings);
    }

    /// <summary>R4a: in column order, NAME → NOTIZ ran while NOTIZ still existed (ORA-00957), after the drops were committed.</summary>
    [Fact]
    public void A_rename_onto_a_name_another_column_gives_up_waits_for_it()
    {
        var plan = Plan(Draft(Changed(Name, c => c with { Name = "NOTIZ" }), Changed(Notiz, c => c with { Name = "BEMERKUNG" })));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} RENAME COLUMN "NOTIZ" TO "BEMERKUNG" """.TrimEnd(),
            $"""ALTER TABLE {T} RENAME COLUMN "NAME" TO "NOTIZ" """.TrimEnd(),
        ], plan.Statements);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void Swapped_names_step_aside_under_a_free_name()
    {
        var plan = Plan(Draft(Changed(Name, c => c with { Name = "NOTIZ" }), Changed(Notiz, c => c with { Name = "NAME" })));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} RENAME COLUMN "NAME" TO "FS_RENAME_1" """.TrimEnd(),
            $"""ALTER TABLE {T} RENAME COLUMN "NOTIZ" TO "NAME" """.TrimEnd(),
            $"""ALTER TABLE {T} RENAME COLUMN "FS_RENAME_1" TO "NOTIZ" """.TrimEnd(),
        ], plan.Statements);
    }

    [Fact]
    public void A_removed_comment_is_set_to_empty()
    {
        Assert.Equal([$"""COMMENT ON COLUMN {T}."STATUS" IS ''"""], Plan(Draft(Changed(Status, c => c with { Comment = "" }))).Statements);
    }

    [Fact]
    public void Shorter_text_warns_that_long_values_fail()
    {
        var plan = Plan(Draft(Changed(Notiz, c => c with { Length = 100 })));

        Assert.Equal([$"""ALTER TABLE {T} MODIFY ("NOTIZ" VARCHAR2(100 BYTE))"""], plan.Statements);
        Assert.Equal(["NOTIZ: VARCHAR2(200) → VARCHAR2(100) scheitert, wenn ein Wert zu lang dafür ist (ORA-01441)."], plan.Warnings);
    }

    /// <summary>Review 3.40.0, A9: these went through with data and changed the values without a word.</summary>
    [Fact]
    public void Type_changes_that_change_values_say_so()
    {
        var padded = Plan(Draft(Changed(Notiz, c => c with { DataType = "CHAR" })));
        var stamp = new ColumnInfo("ZEIT", "TIMESTAMP(6)", null, true, null, 6, true, false, null, 6);

        Assert.Equal(["NOTIZ: VARCHAR2(200) → CHAR(200) füllt jeden Wert mit Leerzeichen auf die volle Länge auf; Vergleiche mit VARCHAR2-Werten (Beziehungen, Filter) treffen danach womöglich nicht mehr."],
            padded.Warnings);
        Assert.True(padded.CanRun);
        var step = Assert.Single(ColumnChange.Steps(Kunde.Table.Ref, stamp, stamp with { DataType = "DATE", Scale = null }, ChangeIntent.Unknown));
        Assert.Equal((DdlRisk.DataLoss, "Läuft mit Daten durch, aber die Werte verlieren ihre Sekundenbruchteile."), (step.Risk, step.Warning));
    }

    /// <summary>
    /// Review 3.44.0, A7 and A13: CHAR → VARCHAR2 kept the padding without a word (a comparison with the trimmed text finds
    /// nothing after it), and a shorter CHAR named only the values that may fail, not the padding of the rest.
    /// </summary>
    [Fact]
    public void Char_changes_name_the_blanks_in_designer_and_comparison()
    {
        var code = new ColumnInfo("CODE", "CHAR", 3, true, null, null, false, false, null, 7);
        var kept = Plan(Draft(Changed(Notiz, c => c with { Length = 100, DataType = "CHAR" })));

        Assert.Equal(
            [
                "NOTIZ: VARCHAR2(200) → CHAR(100) scheitert, wenn ein Wert zu lang dafür ist (ORA-01441).",
                "NOTIZ: VARCHAR2(200) → CHAR(100) füllt jeden Wert mit Leerzeichen auf die volle Länge auf; Vergleiche mit VARCHAR2-Werten (Beziehungen, Filter) treffen danach womöglich nicht mehr.",
            ],
            kept.Warnings);
        var step = Assert.Single(ColumnChange.Steps(Kunde.Table.Ref, code, code with { DataType = "VARCHAR2" }, ChangeIntent.Unknown));
        Assert.Equal(DdlRisk.DataLoss, step.Risk);
        Assert.StartsWith("Läuft mit Daten durch, aber jeder Wert behält die Leerzeichen", step.Warning);
    }

    [Fact]
    public void A_type_change_that_needs_an_empty_column_asks_for_its_values()
    {
        var draft = Draft(Changed(Umsatz, c => c with { Precision = 8 }));
        Assert.Equal([Values("UMSATZ")], TableDesign.Questions(Kunde, draft));

        Assert.Equal(["UMSATZ: NUMBER(12,2) → NUMBER(8,2) geht nur bei leerer Spalte; sie hat 7 Werte. Neue Spalte anlegen, Werte umkopieren, alte löschen."],
            Plan(draft, Facts((Values("UMSATZ"), 7))).Problems);
        Assert.Equal([$"""ALTER TABLE {T} MODIFY ("UMSATZ" NUMBER(8,2))"""], Plan(draft, Facts((Values("UMSATZ"), 0))).Statements);
        Assert.Single(Plan(draft).Warnings); // not counted: it may fail
    }

    [Fact]
    public void To_a_lob_is_not_possible()
    {
        var plan = Plan(Draft(Changed(Notiz, c => c with { DataType = "CLOB", Length = null })));

        Assert.Equal(["NOTIZ: VARCHAR2(200) → CLOB geht nicht per MODIFY (von oder zu einem LOB, ORA-22858). Neue Spalte anlegen, Werte umkopieren, alte löschen."], plan.Problems);
        Assert.Empty(plan.Statements);
    }

    [Fact]
    public void Not_null_asks_for_nulls_and_refuses_with_them()
    {
        var draft = Draft(Changed(Name, c => c with { Nullable = false }));
        Assert.Equal([Nulls("NAME")], TableDesign.Questions(Kunde, draft));

        Assert.Equal(["NAME hat 4 Zeilen mit NULL – erst füllen, dann geht NOT NULL (ORA-02296)."], Plan(draft, Facts((Nulls("NAME"), 4))).Problems);
        Assert.Equal([$"""ALTER TABLE {T} MODIFY ("NAME" NOT NULL)"""], Plan(draft, Facts((Nulls("NAME"), 0))).Statements);
    }

    [Fact]
    public void Null_is_written_only_for_a_not_null_column_and_never_for_the_primary_key()
    {
        Assert.Equal([$"""ALTER TABLE {T} MODIFY ("STATUS" NULL)"""], Plan(Draft(Changed(Status, c => c with { Nullable = true }))).Statements);
        Assert.Equal(["ID gehört zum Primärschlüssel und kann nicht NULL erlauben (ORA-01451)."], Plan(Draft(Changed(Id, c => c with { Nullable = true }))).Problems);
    }

    [Fact]
    public void Defaults_are_set_and_removed()
    {
        Assert.Equal([$"""ALTER TABLE {T} MODIFY ("NAME" DEFAULT 'unbekannt')"""], Plan(Draft(Changed(Name, c => c with { Default = "'unbekannt'" }))).Statements);
        Assert.Equal([$"""ALTER TABLE {T} MODIFY ("STATUS" DEFAULT NULL)"""], Plan(Draft(Changed(Status, c => c with { Default = "" }))).Statements);
    }

    /// <summary>
    /// R4a: a typed expression went into the DDL as it was. A ";" at its end made the statement fail the schema path –
    /// halfway through the run, after the drops before it were committed, and as an unhandled programming error.
    /// </summary>
    [Theory]
    [InlineData("SYSDATE;")]
    [InlineData(":heute")]
    [InlineData("TRUNC(SYSDATE")]
    [InlineData("SYSDATE)")]
    public void A_default_or_check_that_does_not_fit_into_one_statement_is_a_problem(string expression)
    {
        var changed = Plan(Draft(Changed(Name, c => c with { Default = expression })));
        var added = Plan(Draft(new ColumnDraft(null, NewColumn("SEIT", "DATE", @default: expression))));
        var check = Plan(Draft() with { AddedConstraints = [new ConstraintDraft("CK_SEIT", ConstraintType.Check, [], Condition: expression)] });

        Assert.Equal(["NAME: Der Ausdruck darf weder „;“ noch Bind-Variablen (:name) enthalten, und seine Klammern müssen zusammenpassen."], changed.Problems);
        Assert.Equal(["SEIT: Der Ausdruck darf weder „;“ noch Bind-Variablen (:name) enthalten, und seine Klammern müssen zusammenpassen."], added.Problems);
        Assert.Equal(["CK_SEIT: Der Ausdruck darf weder „;“ noch Bind-Variablen (:name) enthalten, und seine Klammern müssen zusammenpassen."], check.Problems);
    }

    [Fact]
    public void Semicolons_and_brackets_inside_literals_and_names_are_part_of_the_expression()
    {
        var plan = Plan(Draft() with { AddedConstraints = [new ConstraintDraft("CK_NAME", ConstraintType.Check, [], Condition: "\"NAME\" <> 'a;b)'")] });

        Assert.Empty(plan.Problems);
        Assert.Equal([$"""ALTER TABLE {T} ADD CONSTRAINT "CK_NAME" CHECK ("NAME" <> 'a;b)')"""], plan.Statements);
    }

    /// <summary>R4a: Oracle keeps a trailing comment in DATA_DEFAULT; written as it was, it swallowed the ")" after it.</summary>
    [Fact]
    public void An_expression_ending_in_a_line_comment_ends_with_a_line_break()
    {
        var plan = Plan(Draft(Changed(Name, c => c with { Default = "'x' -- vorläufig" })));

        Assert.Equal([$"ALTER TABLE {T} MODIFY (\"NAME\" DEFAULT 'x' -- vorläufig\n)"], plan.Statements);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void Default_on_null_needs_a_value_and_no_nulls()
    {
        var draft = Draft(Changed(Name, c => c with { Default = "'x'", DefaultOnNull = true, Nullable = false }));
        Assert.Equal([Nulls("NAME")], TableDesign.Questions(Kunde, draft));
        Assert.Equal([$"""ALTER TABLE {T} MODIFY ("NAME" DEFAULT ON NULL 'x')"""], Plan(draft, Facts((Nulls("NAME"), 0))).Statements);
        Assert.Single(Plan(draft, Facts((Nulls("NAME"), 2))).Problems);

        Assert.Equal(["NAME: DEFAULT ON NULL braucht einen Default-Wert."], Plan(Draft(Changed(Name, c => c with { DefaultOnNull = true }))).Problems);
    }

    [Fact]
    public void Taking_default_on_null_away_keeps_not_null_when_wanted()
    {
        var onNull = Status with { DefaultOnNull = true };
        var table = Kunde with { Columns = [Id, Name, onNull, Umsatz, Notiz] };
        var draft = new TableDraft([.. TableDraft.Of(table).Columns.Select(c => c.Original == "STATUS" ? c with { Column = onNull with { DefaultOnNull = false } } : c)]);

        Assert.Equal(
        [
            $"""ALTER TABLE {T} MODIFY ("STATUS" DEFAULT 'A')""",
            $"""ALTER TABLE {T} MODIFY ("STATUS" NOT NULL)""",
        ], TableDesign.Plan(DesignContext.Of(table), draft, DataFacts.None).Statements);
    }

    [Fact]
    public void Identity_and_virtual_columns_are_only_renamed_or_commented()
    {
        Assert.Equal(["UMSATZ: Identity-Spalten entstehen nur neu und lassen sich hier nur umbenennen oder kommentieren (ORA-30673)."],
            Plan(Draft(Changed(Umsatz, c => c with { IsIdentity = true }))).Problems);

        var virtualColumn = new ColumnInfo("BRUTTO", "NUMBER", null, false, null, null, true, false, "\"UMSATZ\"*1.19", 6, IsVirtual: true);
        var table = Kunde with { Columns = [.. Kunde.Columns, virtualColumn] };
        TableDraft With(ColumnInfo after) => new([.. TableDraft.Of(Kunde).Columns, new ColumnDraft("BRUTTO", after)]);
        Assert.Equal([$"""ALTER TABLE {T} RENAME COLUMN "BRUTTO" TO "GROSS" """.TrimEnd()],
            TableDesign.Plan(DesignContext.Of(table), With(virtualColumn with { Name = "GROSS" }), DataFacts.None).Statements);
        Assert.Single(TableDesign.Plan(DesignContext.Of(table), With(virtualColumn with { Precision = 10 }), DataFacts.None).Problems);
    }

    // ---- dropping ----

    [Fact]
    public void A_dropped_column_is_dropped_first_with_a_warning()
    {
        var plan = Plan(Draft(Changed(Notiz, c => c) with { Dropped = true }, new ColumnDraft(null, NewColumn("BEMERKUNG", "VARCHAR2", 200))));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} DROP COLUMN "NOTIZ" """.TrimEnd(),
            $"""ALTER TABLE {T} ADD ("BEMERKUNG" VARCHAR2(200 BYTE))""",
        ], plan.Statements);
        Assert.Equal(["NOTIZ wird mit allen Werten gelöscht – ohne Rückweg."], plan.Warnings);
    }

    /// <summary>R4a: Oracle drops an index over several columns with one of them, without a word.</summary>
    [Fact]
    public void Dropping_a_column_warns_of_the_indexes_over_several_columns_it_takes_along()
    {
        var single = new IndexInfo("SHOP", "IX_NOTIZ", "NORMAL", false, "VALID", [new IndexColumn("NOTIZ", false, false)], null, false, true);
        var both = new IndexInfo("SHOP", "IX_NAME_NOTIZ", "NORMAL", false, "VALID", [new IndexColumn("NAME", false, false), new IndexColumn("NOTIZ", false, false)], null, false, true);
        var plan = TableDesign.Plan(new DesignContext(Kunde, [], [single, both], []), Draft(Changed(Notiz, c => c) with { Dropped = true }), DataFacts.None);

        Assert.Equal(
            ["NOTIZ: Oracle löscht auch den Index IX_NAME_NOTIZ, der weitere Spalten umfasst.", "NOTIZ wird mit allen Werten gelöscht – ohne Rückweg."],
            plan.Warnings);
    }

    [Fact]
    public void A_referenced_column_or_one_of_a_multi_column_constraint_is_not_dropped()
    {
        var incoming = new ForeignKeyInfo("FK_AUFTRAG_KUNDE", new TableRef("SHOP", "AUFTRAG"), ["KUNDE_ID"], Kunde.Table.Ref, ["ID"], FkSource.Declared);
        var unique = new ConstraintInfo("UK_KUNDE", ConstraintType.Unique, ["NAME", "STATUS"], null, null, [], null, true, true, false, false, false);

        var plan = Plan(Draft(Changed(Id, c => c) with { Dropped = true }, Changed(Name, c => c) with { Dropped = true }), constraints: [unique], incoming: [incoming]);

        Assert.Equal(
        [
            "ID wird von Fremdschlüsseln anderer Tabellen referenziert (FK_AUFTRAG_KUNDE); Oracle löscht die Spalte nicht (ORA-12992).",
            "NAME gehört zu UK_KUNDE, einem Constraint über mehrere Spalten – erst den Constraint löschen (ORA-12991).",
        ], plan.Problems);
    }

    [Fact]
    public void A_table_keeps_at_least_one_column()
    {
        var draft = new TableDraft(TableDraft.Of(Kunde).Columns.Select(c => c with { Dropped = true }).ToList());

        Assert.Contains("Eine Tabelle braucht mindestens eine Spalte.", Plan(draft).Problems);
    }

    // ---- order and the schema path ----

    [Fact]
    public void Statements_run_drops_renames_changes_adds_comments_and_all_pass_the_schema_path()
    {
        var plan = Plan(Draft(
            new ColumnDraft(null, NewColumn("FAX", "VARCHAR2", 30, comment: "Fax")),
            Changed(Name, c => c with { Name = "NACHNAME", Default = "'?'" }),
            Changed(Notiz, c => c) with { Dropped = true },
            Changed(Umsatz, c => c with { Precision = 14 })));

        Assert.Equal(["DROP", "RENAME", "MODIFY", "MODIFY", "ADD", "COMMENT"], plan.Statements.Select(Kind));
        Assert.All(plan.Statements, sql =>
        {
            Assert.True(OracleSession.IsDdlStatement(sql), sql);
            Assert.Equal(new SqlTableReference("SHOP", "KUNDE", null, 0), SqlScript.DdlTableOf(sql));
        });

        static string Kind(string sql) => sql.StartsWith("COMMENT", StringComparison.Ordinal) ? "COMMENT" : sql.Split(' ')[3];
    }

    // ---- constraints ----

    private static readonly ConstraintInfo PkKunde = new("PK_KUNDE", ConstraintType.PrimaryKey, ["ID"], null, null, [], null, true, true, false, false, false);

    private static readonly TableDetails Land = new(new TableSummary("SHOP", "LAND", TableKind.Table),
        [new ColumnInfo("CODE", "VARCHAR2", 2, false, null, null, false, false, null, 1), new ColumnInfo("NAME", "VARCHAR2", 50, false, null, null, true, false, null, 2)],
        ["CODE"], [["NAME"]], false);

    private static DesignPlan PlanWith(TableDraft draft, DataFacts? facts = null, IReadOnlyList<IndexInfo>? indexes = null, IReadOnlyList<ForeignKeyInfo>? incoming = null) =>
        TableDesign.Plan(new DesignContext(Kunde, [PkKunde], indexes ?? [], incoming ?? []) { Referenced = new Dictionary<TableRef, TableDetails> { [Land.Table.Ref] = Land } },
            draft, facts ?? DataFacts.None);

    private static TableDraft WithLand(params ConstraintDraft[] constraints) =>
        Draft(new ColumnDraft(null, NewColumn("LAND", "VARCHAR2", 2))) with { AddedConstraints = constraints };

    [Fact]
    public void A_foreign_key_comes_with_its_index_after_the_new_column()
    {
        var plan = PlanWith(WithLand(new ConstraintDraft("FK_KUNDE_LAND", ConstraintType.ForeignKey, ["LAND"], References: Land.Table.Ref, ReferencedColumns: ["CODE"])),
            Facts((Rows, 0)));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} ADD ("LAND" VARCHAR2(2 BYTE))""",
            $"""ALTER TABLE {T} ADD CONSTRAINT "FK_KUNDE_LAND" FOREIGN KEY ("LAND") REFERENCES "SHOP"."LAND" ("CODE")""",
            """CREATE INDEX "SHOP"."IX_KUNDE_LAND" ON "SHOP"."KUNDE" ("LAND")""",
        ], plan.Statements);
        Assert.Empty(plan.Warnings); // an empty table: no rows to check
        Assert.All(plan.Statements, sql => Assert.True(OracleSession.IsDdlStatement(sql), sql));
    }

    [Fact]
    public void A_foreign_key_without_index_is_a_warning_and_a_covering_index_needs_none()
    {
        var noIndex = PlanWith(WithLand(new ConstraintDraft("FK", ConstraintType.ForeignKey, ["LAND"], References: Land.Table.Ref, ReferencedColumns: ["CODE"], WithIndex: false)));
        Assert.Equal(2, noIndex.Statements.Count);
        Assert.Contains("FK bekommt keinen Index: Löschen oder Ändern eines referenzierten Schlüssels sperrt dann diese ganze Tabelle und durchsucht sie.", noIndex.Warnings);
        Assert.Contains("Neue Constraints prüfen die vorhandenen Zeilen: Duplikate, NULLs, Zeilen gegen einen Check oder ohne Elternzeile lassen sie scheitern (ORA-02437, ORA-02299, ORA-02293, ORA-02298).", noIndex.Warnings);

        var draft = Draft() with { AddedConstraints = [new ConstraintDraft("FK", ConstraintType.ForeignKey, ["NAME"], References: Land.Table.Ref, ReferencedColumns: ["NAME"])] };
        var covering = new IndexInfo("SHOP", "IX_NAME_STATUS", "NORMAL", false, "VALID", [new IndexColumn("NAME", false, false), new IndexColumn("STATUS", false, false)], null, false, true);
        Assert.Single(PlanWith(draft, indexes: [covering]).Statements);
    }

    [Fact]
    public void A_foreign_key_needs_a_key_of_the_referenced_table_fitting_types_and_as_many_columns()
    {
        ConstraintDraft Fk(string column, params string[] referenced) => new("FK", ConstraintType.ForeignKey, [column], References: Land.Table.Ref, ReferencedColumns: referenced);

        Assert.Equal(["FK: STATUS ist kein Primär- oder Unique-Schlüssel von LAND (ORA-02270)."], PlanWith(WithLand(Fk("LAND", "STATUS"))).Problems);
        Assert.Equal(["FK: UMSATZ (NUMBER(12,2)) kann nicht auf CODE (VARCHAR2(2)) verweisen – die Typen passen nicht (ORA-02267)."], PlanWith(WithLand(Fk("UMSATZ", "CODE"))).Problems);
        Assert.Equal(["FK: 1 Spalten verweisen auf 2 Spalten – die Anzahl muss gleich sein."], PlanWith(WithLand(Fk("LAND", "CODE", "NAME"))).Problems);
        Assert.Equal(["FK: Wähle die referenzierte Tabelle."], PlanWith(WithLand(new ConstraintDraft("FK", ConstraintType.ForeignKey, ["LAND"]))).Problems);
        Assert.Empty(PlanWith(WithLand(Fk("LAND", "CODE") with { References = new TableRef("SHOP", "NICHT_GELADEN"), ReferencedColumns = ["X"] })).Problems); // Oracle checks it
    }

    [Fact]
    public void A_foreign_key_may_reference_a_new_key_of_the_same_table()
    {
        var draft = Draft(new ColumnDraft(null, NewColumn("CHEF", "NUMBER"))) with
        {
            AddedConstraints =
            [
                new ConstraintDraft("FK_KUNDE_CHEF", ConstraintType.ForeignKey, ["CHEF"], References: Kunde.Table.Ref, ReferencedColumns: ["ID"], DeleteRule: "SET NULL"),
            ],
        };

        var plan = PlanWith(draft);

        Assert.Empty(plan.Problems);
        Assert.Contains($"""ALTER TABLE {T} ADD CONSTRAINT "FK_KUNDE_CHEF" FOREIGN KEY ("CHEF") REFERENCES "SHOP"."KUNDE" ("ID") ON DELETE SET NULL""", plan.Statements);
    }

    [Fact]
    public void A_second_primary_key_or_a_key_on_the_same_columns_is_refused_a_dropped_one_makes_room()
    {
        var pk = new ConstraintDraft("PK_KUNDE_NEU", ConstraintType.PrimaryKey, ["ID", "NAME"]);
        Assert.Equal(["Die Tabelle hat schon einen Primärschlüssel – erst diesen löschen (ORA-02260)."], PlanWith(Draft() with { AddedConstraints = [pk] }).Problems);
        Assert.Equal(["UK_ID: PK_KUNDE ist schon ein Schlüssel auf diesen Spalten (ORA-02261)."],
            PlanWith(Draft() with { AddedConstraints = [new ConstraintDraft("UK_ID", ConstraintType.Unique, ["ID"])] }).Problems);

        var replaced = PlanWith(Draft() with { DroppedConstraints = ["PK_KUNDE"], AddedConstraints = [pk] });
        Assert.Equal(
        [
            $"""ALTER TABLE {T} DROP CONSTRAINT "PK_KUNDE" """.TrimEnd(),
            $"""ALTER TABLE {T} ADD CONSTRAINT "PK_KUNDE_NEU" PRIMARY KEY ("ID", "NAME")""",
        ], replaced.Statements);
    }

    [Fact]
    public void A_referenced_key_is_not_dropped()
    {
        var incoming = new ForeignKeyInfo("FK_AUFTRAG_KUNDE", new TableRef("SHOP", "AUFTRAG"), ["KUNDE_ID"], Kunde.Table.Ref, ["ID"], FkSource.Declared);

        Assert.Equal(["PK_KUNDE wird von Fremdschlüsseln anderer Tabellen referenziert (FK_AUFTRAG_KUNDE); Oracle löscht den Schlüssel nicht (ORA-02273)."],
            PlanWith(Draft() with { DroppedConstraints = ["PK_KUNDE"] }, incoming: [incoming]).Problems);
    }

    [Fact]
    public void Dropping_the_primary_key_lets_its_column_allow_null()
    {
        var plan = PlanWith(Draft(Changed(Id, c => c with { Nullable = true })) with { DroppedConstraints = ["PK_KUNDE"] });

        Assert.Equal(
        [
            $"""ALTER TABLE {T} DROP CONSTRAINT "PK_KUNDE" """.TrimEnd(),
            $"""ALTER TABLE {T} MODIFY ("ID" NULL)""",
        ], plan.Statements);
    }

    [Fact]
    public void Dropping_a_multi_column_constraint_lets_its_column_go_and_runs_first()
    {
        var unique = new ConstraintInfo("UK_KUNDE", ConstraintType.Unique, ["NAME", "STATUS"], null, null, [], null, true, true, false, false, false);
        var draft = Draft(Changed(Name, c => c) with { Dropped = true }) with { DroppedConstraints = ["UK_KUNDE"] };

        var plan = TableDesign.Plan(new DesignContext(Kunde, [PkKunde, unique], [], []), draft, DataFacts.None);

        Assert.Empty(plan.Problems);
        Assert.Equal(["DROP CONSTRAINT", "DROP COLUMN"], plan.Statements.Select(s => string.Join(' ', s.Split(' ')[3..5])));
    }

    [Fact]
    public void Checks_need_a_condition_and_names_must_be_free()
    {
        Assert.Equal(["CK: Ein Check braucht eine Bedingung."], PlanWith(Draft() with { AddedConstraints = [new ConstraintDraft("CK", ConstraintType.Check, [], "  ")] }).Problems);
        Assert.Equal(["Die Tabelle hat schon einen Constraint PK_KUNDE (ORA-02264)."],
            PlanWith(Draft() with { AddedConstraints = [new ConstraintDraft("PK_KUNDE", ConstraintType.Check, [], "UMSATZ >= 0")] }).Problems);

        var plan = PlanWith(Draft() with { AddedConstraints = [new ConstraintDraft("", ConstraintType.Check, ["UMSATZ"], "UMSATZ >= 0")] });
        Assert.Equal([$"""ALTER TABLE {T} ADD CHECK (UMSATZ >= 0)"""], plan.Statements); // no name: Oracle names it
    }

    [Fact]
    public void Constraint_columns_must_be_columns_of_the_draft()
    {
        var draft = Draft(Changed(Name, c => c with { Name = "NACHNAME" })) with { AddedConstraints = [new ConstraintDraft("UK", ConstraintType.Unique, ["NAME", "NACHNAME", "NACHNAME"])] };

        Assert.Equal(["UK: Die Tabelle hat keine Spalte NAME.", "UK: NACHNAME ist doppelt gewählt."], PlanWith(draft).Problems);
    }

    [Fact]
    public void New_constraints_ask_for_the_rows()
    {
        Assert.Equal([Rows], TableDesign.Questions(Kunde, Draft() with { AddedConstraints = [new ConstraintDraft("UK", ConstraintType.Unique, ["NAME"])] }));
    }

    [Theory]
    [InlineData(ConstraintType.PrimaryKey, "PK_KUNDE")]
    [InlineData(ConstraintType.Unique, "UK_KUNDE_NAME_STATUS")]
    [InlineData(ConstraintType.Check, "CK_KUNDE_NAME_STATUS")]
    [InlineData(ConstraintType.ForeignKey, "FK_KUNDE_LAND")]
    public void Constraint_names_are_suggested_by_kind(ConstraintType type, string name) =>
        Assert.Equal(name, TableDesign.SuggestConstraintName("Kunde", type, ["NAME", "STATUS"], "LAND", []));

    [Fact]
    public void A_taken_constraint_name_counts_up() =>
        Assert.Equal("PK_KUNDE_2", TableDesign.SuggestConstraintName("KUNDE", ConstraintType.PrimaryKey, [], null, ["PK_KUNDE"]));

    // ---- new table (stage 3) ----

    private static readonly NewTable Ort = new("SHOP", "ORT", "Orte mit PLZ");

    private static TableDraft NewDraft(params ColumnInfo[] columns) => new(columns.Select(c => new ColumnDraft(null, c)).ToList());

    private static DesignPlan PlanNew(TableDraft draft, NewTable? table = null, params string[] taken) =>
        TableDesign.PlanNew(table ?? Ort, draft, new Dictionary<TableRef, TableDetails> { [Land.Table.Ref] = Land }, taken);

    [Fact]
    public void A_new_table_is_created_with_its_keys_foreign_keys_index_and_comments()
    {
        var draft = NewDraft(
            NewColumn("ID", "NUMBER", nullable: false, identity: true),
            NewColumn("PLZ", "VARCHAR2", 5, nullable: false, comment: "Postleitzahl"),
            NewColumn("LAND", "VARCHAR2", 2, @default: "'DE'")) with
        {
            AddedConstraints =
            [
                new ConstraintDraft("FK_ORT_LAND", ConstraintType.ForeignKey, ["LAND"], References: Land.Table.Ref, ReferencedColumns: ["CODE"]),
                new ConstraintDraft("PK_ORT", ConstraintType.PrimaryKey, ["ID"]),
            ],
        };

        var plan = PlanNew(draft);

        Assert.Empty(plan.Problems);
        Assert.Empty(plan.Warnings); // a new table has no rows to check
        Assert.Equal(
        [
            """
            CREATE TABLE "SHOP"."ORT" (
                "ID" NUMBER(10) GENERATED BY DEFAULT AS IDENTITY,
                "PLZ" VARCHAR2(5 BYTE) NOT NULL,
                "LAND" VARCHAR2(2 BYTE) DEFAULT 'DE')
            """.ReplaceLineEndings("\n"),
            """ALTER TABLE "SHOP"."ORT" ADD CONSTRAINT "PK_ORT" PRIMARY KEY ("ID")""",
            """ALTER TABLE "SHOP"."ORT" ADD CONSTRAINT "FK_ORT_LAND" FOREIGN KEY ("LAND") REFERENCES "SHOP"."LAND" ("CODE")""",
            """CREATE INDEX "SHOP"."IX_ORT_LAND" ON "SHOP"."ORT" ("LAND")""",
            """COMMENT ON TABLE "SHOP"."ORT" IS 'Orte mit PLZ'""",
            """COMMENT ON COLUMN "SHOP"."ORT"."PLZ" IS 'Postleitzahl'""",
        ], plan.Statements);
        Assert.All(plan.Statements, sql => Assert.True(OracleSession.IsDdlStatement(sql), sql));
    }

    [Fact]
    public void A_new_table_needs_a_free_name_and_a_column()
    {
        Assert.Equal(["Gib einen Namen für die Tabelle ein."], PlanNew(NewDraft(NewColumn("ID", "NUMBER")), Ort with { Name = " " }).Problems);
        Assert.Equal(["Das Schema hat schon ein Objekt ORT (ORA-00955)."], PlanNew(NewDraft(NewColumn("ID", "NUMBER")), null, "KUNDE", "ORT").Problems);
        Assert.Equal(["Eine Tabelle braucht mindestens eine Spalte."], PlanNew(NewDraft()).Problems);
        Assert.Single(PlanNew(NewDraft(NewColumn("ID", "NUMBER")), Ort with { Name = "orte" }).Warnings); // needs quotes
    }

    [Fact]
    public void A_new_table_may_reference_itself_and_a_mandatory_column_needs_no_default()
    {
        var draft = NewDraft(NewColumn("ID", "NUMBER", nullable: false), NewColumn("PARENT_ID", "NUMBER")) with
        {
            AddedConstraints =
            [
                new ConstraintDraft("PK_ORT", ConstraintType.PrimaryKey, ["ID"]),
                new ConstraintDraft("FK_ORT_PARENT", ConstraintType.ForeignKey, ["PARENT_ID"], References: Ort.Ref, ReferencedColumns: ["ID"]),
            ],
        };

        var plan = PlanNew(draft);

        Assert.Empty(plan.Problems);
        Assert.Contains("""ALTER TABLE "SHOP"."ORT" ADD CONSTRAINT "FK_ORT_PARENT" FOREIGN KEY ("PARENT_ID") REFERENCES "SHOP"."ORT" ("ID")""", plan.Statements);
    }

    // ---- renamed and changed constraints ----

    private static readonly ConstraintInfo CkUmsatz = new("CK_UMSATZ", ConstraintType.Check, [], "UMSATZ >= 0", null, [], null, true, true, false, false, false);

    private static readonly ConstraintInfo FkLand = new("FK_KUNDE_LAND", ConstraintType.ForeignKey, ["STATUS"], null, Land.Table.Ref, ["CODE"], "NO ACTION", true, true, false, false, false);

    private static IndexInfo UniqueIndex(string name, string column) => new("SHOP", name, "NORMAL", true, "VALID", [new IndexColumn(column, false, false)], null, false, true);

    private static DesignPlan PlanEdits(TableDraft draft, IReadOnlyList<ConstraintInfo>? constraints = null, IReadOnlyList<IndexInfo>? indexes = null, DataFacts? facts = null) =>
        TableDesign.Plan(new DesignContext(Kunde, constraints ?? [PkKunde, CkUmsatz, FkLand], indexes ?? [UniqueIndex("PK_KUNDE", "ID")], []), draft, facts ?? DataFacts.None);

    private static TableDraft Edited(params ConstraintEdit[] edits) => Draft() with { ChangedConstraints = edits };

    [Fact]
    public void A_renamed_key_takes_its_index_of_the_same_name_along()
    {
        var plan = PlanEdits(Edited(new ConstraintEdit("PK_KUNDE", "PK_KUNDEN")));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} RENAME CONSTRAINT "PK_KUNDE" TO "PK_KUNDEN" """.TrimEnd(),
            """ALTER INDEX "SHOP"."PK_KUNDE" RENAME TO "PK_KUNDEN" """.TrimEnd(),
        ], plan.Statements);
        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Warnings, w => w.StartsWith("PK_KUNDE → PK_KUNDEN", StringComparison.Ordinal));
        Assert.All(plan.Statements, sql => Assert.True(OracleSession.IsDdlStatement(sql), sql));

        Assert.Single(PlanEdits(Edited(new ConstraintEdit("PK_KUNDE", "PK_KUNDEN", RenameIndex: false))).Statements);
        Assert.Single(PlanEdits(Edited(new ConstraintEdit("PK_KUNDE", "PK_KUNDEN")), indexes: [UniqueIndex("SYS_C001", "ID")]).Statements); // another name: stays
        Assert.Equal(["Die Tabelle hat schon einen Index IX_KUNDE."],
            PlanEdits(Edited(new ConstraintEdit("PK_KUNDE", "IX_KUNDE")), indexes: [UniqueIndex("PK_KUNDE", "ID"), UniqueIndex("IX_KUNDE", "NAME")]).Problems);
    }

    [Fact]
    public void A_renamed_check_or_foreign_key_keeps_its_definition()
    {
        var plan = PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "CK_KUNDE_UMSATZ", "UMSATZ >= 0"), new ConstraintEdit("FK_KUNDE_LAND", "FK_LAND")));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} RENAME CONSTRAINT "CK_UMSATZ" TO "CK_KUNDE_UMSATZ" """.TrimEnd(),
            $"""ALTER TABLE {T} RENAME CONSTRAINT "FK_KUNDE_LAND" TO "FK_LAND" """.TrimEnd(),
        ], plan.Statements);
        Assert.Empty(PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "CK_UMSATZ", " UMSATZ >= 0 "))).Statements); // blanks are no change
    }

    [Fact]
    public void A_new_condition_is_added_before_the_old_check_goes()
    {
        var plan = PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "CK_UMSATZ", "UMSATZ >= 0 AND UMSATZ < 1000000")) with
        {
            AddedConstraints = [new ConstraintDraft("UK_NAME", ConstraintType.Unique, ["NAME"])],
        }, facts: Facts((Rows, 3)));

        Assert.Equal(
        [
            $"""ALTER TABLE {T} ADD CONSTRAINT "UK_NAME" UNIQUE ("NAME")""",
            $"""ALTER TABLE {T} ADD CONSTRAINT "FS_CHECK_1" CHECK (UMSATZ >= 0 AND UMSATZ < 1000000)""",
            $"""ALTER TABLE {T} DROP CONSTRAINT "CK_UMSATZ" """.TrimEnd(),
            $"""ALTER TABLE {T} RENAME CONSTRAINT "FS_CHECK_1" TO "CK_UMSATZ" """.TrimEnd(),
        ], plan.Statements);
        Assert.Contains(plan.Warnings, w => w.StartsWith("CK_UMSATZ: Die neue Bedingung", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Neue Constraints prüfen", StringComparison.Ordinal));
        Assert.Equal([Rows], TableDesign.Questions(Kunde, Edited(new ConstraintEdit("CK_UMSATZ", "CK_UMSATZ", "UMSATZ > 0"))));
    }

    [Fact]
    public void A_check_with_a_new_condition_and_name_needs_no_name_in_between()
    {
        Assert.Equal(
        [
            $"""ALTER TABLE {T} ADD CONSTRAINT "CK_POSITIV" CHECK (UMSATZ > 0)""",
            $"""ALTER TABLE {T} DROP CONSTRAINT "CK_UMSATZ" """.TrimEnd(),
        ], PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "CK_POSITIV", "UMSATZ > 0"))).Statements);

        var generated = CkUmsatz with { Name = "SYS_C0042", GeneratedName = true };
        Assert.Equal(
        [
            $"""ALTER TABLE {T} ADD CHECK (UMSATZ > 0)""",
            $"""ALTER TABLE {T} DROP CONSTRAINT "SYS_C0042" """.TrimEnd(),
        ], PlanEdits(Edited(new ConstraintEdit("SYS_C0042", "SYS_C0042", "UMSATZ > 0")), [generated]).Statements);

        Assert.Equal(["CK_UMSATZ: Ein Check braucht eine Bedingung."], PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "CK_UMSATZ", " "))).Problems);
    }

    [Fact]
    public void A_new_delete_rule_drops_and_adds_the_foreign_key_on_the_renamed_columns()
    {
        var draft = Draft(Changed(Status, c => c with { Name = "LAND" })) with { ChangedConstraints = [new ConstraintEdit("FK_KUNDE_LAND", "FK_KUNDE_LAND", DeleteRule: "CASCADE")] };

        var plan = PlanEdits(draft);

        Assert.Equal(
        [
            $"""ALTER TABLE {T} DROP CONSTRAINT "FK_KUNDE_LAND" """.TrimEnd(),
            $"""ALTER TABLE {T} RENAME COLUMN "STATUS" TO "LAND" """.TrimEnd(),
            $"""ALTER TABLE {T} ADD CONSTRAINT "FK_KUNDE_LAND" FOREIGN KEY ("LAND") REFERENCES "SHOP"."LAND" ("CODE") ON DELETE CASCADE""",
        ], plan.Statements);
        Assert.Contains(plan.Warnings, w => w.StartsWith("FK_KUNDE_LAND wird gelöscht", StringComparison.Ordinal));
        Assert.Empty(PlanEdits(Edited(new ConstraintEdit("FK_KUNDE_LAND", "FK_KUNDE_LAND"))).Statements); // NO ACTION is none
    }

    [Fact]
    public void A_new_constraint_name_must_be_given_and_free()
    {
        Assert.Equal(["Die Tabelle hat schon einen Constraint PK_KUNDE (ORA-02264)."], PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "PK_KUNDE", "UMSATZ >= 0"))).Problems);
        Assert.Equal(["CK_UMSATZ: Gib einen Namen für den Constraint ein."], PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "", "UMSATZ >= 0"))).Problems);

        // Swapped names would need one in between.
        Assert.Equal(2, PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "FK_KUNDE_LAND", "UMSATZ >= 0"), new ConstraintEdit("FK_KUNDE_LAND", "CK_UMSATZ"))).Problems.Count);

        // The name of a dropped one is free; a new constraint cannot take the name a renamed one gets.
        Assert.Empty(PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "FK_KUNDE_LAND", "UMSATZ >= 0")) with { DroppedConstraints = ["FK_KUNDE_LAND"] }).Problems);
        Assert.Equal(["Die Tabelle hat schon einen Constraint CK_NEU (ORA-02264)."], PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "CK_NEU", "UMSATZ >= 0")) with
        {
            AddedConstraints = [new ConstraintDraft("CK_NEU", ConstraintType.Check, [], "NAME IS NOT NULL")],
        }).Problems);
    }

    [Fact]
    public void A_changed_constraint_on_a_dropped_column_is_a_problem_a_rename_is_not()
    {
        var check = CkUmsatz with { Columns = ["UMSATZ"] };
        var dropUmsatz = Draft(Changed(Umsatz, c => c) with { Dropped = true });

        var changed = PlanEdits(dropUmsatz with { ChangedConstraints = [new ConstraintEdit("CK_UMSATZ", "CK_UMSATZ", "UMSATZ > 0")] }, [PkKunde, check, FkLand]);
        Assert.Equal(["CK_UMSATZ: Die Spalte UMSATZ wird gelöscht, und Oracle löscht den Constraint mit ihr – die Änderung zurücknehmen oder den Constraint löschen."],
            changed.Problems);
        Assert.Equal(changed.Problems, changed.ConstraintProblems);

        var fk = PlanEdits(Draft(Changed(Status, c => c) with { Dropped = true }) with { ChangedConstraints = [new ConstraintEdit("FK_KUNDE_LAND", "FK_KUNDE_LAND", DeleteRule: "CASCADE")] });
        Assert.Single(fk.Problems);

        var renamed = PlanEdits(dropUmsatz with { ChangedConstraints = [new ConstraintEdit("CK_UMSATZ", "CK_POSITIV", "UMSATZ >= 0")] }, [PkKunde, check, FkLand]);
        Assert.Empty(renamed.Problems);
    }

    [Fact]
    public void Only_edits_that_check_the_rows_again_need_them_counted()
    {
        IReadOnlyList<ConstraintInfo> constraints = [PkKunde, CkUmsatz, FkLand];

        Assert.Empty(TableDesign.Questions(Kunde, Edited(new ConstraintEdit("PK_KUNDE", "PK_KUNDEN"), new ConstraintEdit("CK_UMSATZ", "CK_X", " UMSATZ >= 0")), constraints));
        Assert.Equal([Rows], TableDesign.Questions(Kunde, Edited(new ConstraintEdit("FK_KUNDE_LAND", "FK_KUNDE_LAND", DeleteRule: "SET NULL")), constraints));
        Assert.Empty(TableDesign.Questions(Kunde, Edited(new ConstraintEdit("CK_UMSATZ", "CK_UMSATZ", "UMSATZ > 0")) with { DroppedConstraints = ["CK_UMSATZ"] }, constraints));
        Assert.Equal([Rows], TableDesign.Questions(Kunde, Edited(new ConstraintEdit("PK_KUNDE", "PK_KUNDEN")))); // without the constraints: counted
    }

    [Fact]
    public void Constraint_problems_are_told_apart_from_the_columns()
    {
        var plan = PlanEdits(Draft(Changed(Name, c => c with { Name = "" })) with { ChangedConstraints = [new ConstraintEdit("CK_UMSATZ", "PK_KUNDE", "UMSATZ >= 0")] });

        Assert.Equal(2, plan.Problems.Count);
        Assert.Equal(["Die Tabelle hat schon einen Constraint PK_KUNDE (ORA-02264)."], plan.ConstraintProblems);
    }

    [Fact]
    public void The_edit_of_a_dropped_constraint_is_none()
    {
        var plan = PlanEdits(Edited(new ConstraintEdit("CK_UMSATZ", "CK_NEU", "UMSATZ > 0")) with { DroppedConstraints = ["CK_UMSATZ"] });

        Assert.Equal([$"""ALTER TABLE {T} DROP CONSTRAINT "CK_UMSATZ" """.TrimEnd()], plan.Statements);
    }
}
