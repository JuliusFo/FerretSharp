using FerretSharp.Core.Design;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Design;

public class IndexDesignTests
{
    private static readonly TableRef Auftrag = new("SHOP", "AUFTRAG");

    private static readonly TableDetails Table = new(
        new TableSummary("SHOP", "AUFTRAG", TableKind.Table),
        [
            Col("ID", "NUMBER", 1, nullable: false),
            Col("KUNDE_ID", "NUMBER", 2),
            Col("DATUM", "DATE", 3),
            Col("NOTIZ", "CLOB", 4),
            Col("Status", "VARCHAR2", 5),
        ],
        ["ID"], [], false);

    private static ColumnInfo Col(string name, string type, int position, bool nullable = true) =>
        new(name, type, type == "VARCHAR2" ? 20 : null, false, null, null, nullable, false, null, position);

    private static IndexInfo Index(string name, bool unique = false, params string[] columns) =>
        new("SHOP", name, "NORMAL", unique, "VALID", columns.Select(c => new IndexColumn(c, false, false)).ToList(), "USERS", false, true);

    private static readonly IndexInfo PrimaryKey = Index("PK_AUFTRAG", true, "ID");

    // ---- name ----

    [Fact]
    public void Suggested_name_is_ix_table_columns()
    {
        Assert.Equal("IX_AUFTRAG_KUNDE_ID_DATUM", IndexDesign.SuggestName("AUFTRAG", ["KUNDE_ID", "DATUM"], ["PK_AUFTRAG"]));
    }

    [Fact]
    public void Suggested_name_is_plain_also_for_mixed_case_and_special_names()
    {
        var name = IndexDesign.SuggestName("Auftrag-Alt", ["Status", "Nr."], []);

        Assert.Equal("IX_AUFTRAG_ALT_STATUS_NR_", name);
        Assert.Empty(IndexDesign.Check(new IndexDraft(name, ["Status"]), Table, []).Warnings);
    }

    [Fact]
    public void Suggested_name_counts_up_when_taken()
    {
        Assert.Equal("IX_AUFTRAG_DATUM_3", IndexDesign.SuggestName("AUFTRAG", ["DATUM"], ["IX_AUFTRAG_DATUM", "IX_AUFTRAG_DATUM_2"]));
    }

    [Fact]
    public void Suggested_name_stays_within_128_bytes_also_with_umlauts_and_suffix()
    {
        var column = new string('Ä', 100); // 200 bytes
        var name = IndexDesign.SuggestName("AUFTRAG", [column], []);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= 128, name);
        Assert.StartsWith("IX_AUFTRAG_ÄÄ", name, StringComparison.Ordinal);

        var second = IndexDesign.SuggestName("AUFTRAG", [column], [name]);
        Assert.EndsWith("_2", second, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(second) <= 128, second);
    }

    [Fact]
    public void Foreign_key_draft_takes_its_columns_in_constraint_order()
    {
        var fk = new ForeignKeyInfo("FK_POS", new TableRef("SHOP", "POSITION"), ["AUFTRAG_ID", "NR"], Auftrag, ["ID", "NR"], FkSource.Declared);

        var draft = IndexDesign.ForForeignKey(fk, [Index("IX_POSITION_AUFTRAG_ID_NR")]);

        Assert.Equal(["AUFTRAG_ID", "NR"], draft.Columns);
        Assert.Equal("IX_POSITION_AUFTRAG_ID_NR_2", draft.Name);
        Assert.False(draft.Unique);
    }

    // ---- DDL ----

    [Fact]
    public void Ddl_quotes_names_exactly_and_passes_the_schema_path()
    {
        var sql = IndexDesign.Ddl(new IndexDraft("IX_AUFTRAG_STATUS", ["Status", "DATUM"]), Auftrag);

        Assert.Equal("""CREATE INDEX "SHOP"."IX_AUFTRAG_STATUS" ON "SHOP"."AUFTRAG" ("Status", "DATUM")""", sql);
        Assert.True(OracleSession.IsDdlStatement(sql));
        Assert.Equal(new SqlTableReference("SHOP", "AUFTRAG", null, 0), SqlScript.DdlTableOf(sql)); // the lock holders are found by it
    }

    [Fact]
    public void Unique_draft_writes_create_unique_index()
    {
        var sql = IndexDesign.Ddl(new IndexDraft("UX_AUFTRAG_KUNDE_DATUM", ["KUNDE_ID", "DATUM"], Unique: true), Auftrag);

        Assert.Equal("""CREATE UNIQUE INDEX "SHOP"."UX_AUFTRAG_KUNDE_DATUM" ON "SHOP"."AUFTRAG" ("KUNDE_ID", "DATUM")""", sql);
        Assert.True(OracleSession.IsDdlStatement(sql));
    }

    [Fact]
    public void Descending_columns_and_the_tablespace_go_into_the_ddl()
    {
        var draft = new IndexDraft("IX_AUFTRAG_KUNDE_DATUM", ["KUNDE_ID", "DATUM"]) { Descending = new HashSet<string> { "DATUM" }, Tablespace = "INDX" };

        var sql = IndexDesign.Ddl(draft, Auftrag);

        Assert.Equal("""CREATE INDEX "SHOP"."IX_AUFTRAG_KUNDE_DATUM" ON "SHOP"."AUFTRAG" ("KUNDE_ID", "DATUM" DESC) TABLESPACE "INDX" """.TrimEnd(), sql);
        Assert.True(OracleSession.IsDdlStatement(sql));
    }

    // ---- check ----

    [Fact]
    public void A_fresh_draft_on_plain_columns_can_run()
    {
        var check = IndexDesign.Check(new IndexDraft("IX_AUFTRAG_KUNDE_ID", ["KUNDE_ID"]), Table, [PrimaryKey]);

        Assert.True(check.CanRun);
        Assert.Empty(check.Warnings);
    }

    [Theory]
    [InlineData("", "Gib einen Namen für den Index ein.")]
    [InlineData("   ", "Gib einen Namen für den Index ein.")]
    [InlineData("IX\"X", "IX\"X: Ein Name darf keine doppelten Anführungszeichen enthalten.")] // R5b: with the name, as everywhere
    [InlineData("PK_AUFTRAG", "Die Tabelle hat schon einen Index PK_AUFTRAG.")]
    [InlineData("IX_KUNDE_NAME", "Eine andere Tabelle des Schemas hat schon einen Index IX_KUNDE_NAME (ORA-00955).")] // review of R8
    public void Name_problems_stop_the_draft(string name, string problem)
    {
        var check = IndexDesign.Check(new IndexDraft(name, ["KUNDE_ID"]), Table, [PrimaryKey], new HashSet<string> { "IX_KUNDE_NAME" });

        Assert.False(check.CanRun);
        Assert.Contains(problem, check.Problems);
    }

    [Fact]
    public void A_name_over_128_bytes_stops_the_draft()
    {
        var check = IndexDesign.Check(new IndexDraft(new string('Ä', 65), ["KUNDE_ID"]), Table, []);

        Assert.Equal([$"{new string('Ä', 65)}: Der Name ist länger als 128 Byte."], check.Problems);
    }

    [Theory]
    [InlineData("ix_auftrag")]
    [InlineData("IX-AUFTRAG")]
    [InlineData("1IX")]
    [InlineData("INDEX")] // R5b: a reserved word warned nothing
    public void A_name_that_needs_quotes_is_a_warning(string name)
    {
        var check = IndexDesign.Check(new IndexDraft(name, ["KUNDE_ID"]), Table, []);

        Assert.True(check.CanRun);
        Assert.Single(check.Warnings);
    }

    [Fact]
    public void Plain_names_with_digits_dollar_hash_and_umlauts_need_no_quotes()
    {
        Assert.Empty(IndexDesign.Check(new IndexDraft("IX_ÄNDERUNG$1#", ["KUNDE_ID"]), Table, []).Warnings);
    }

    [Fact]
    public void No_columns_stops_the_draft()
    {
        Assert.Equal(["Wähle mindestens eine Spalte."], IndexDesign.Check(new IndexDraft("IX_A", []), Table, []).Problems);
    }

    [Fact]
    public void Column_problems_are_named()
    {
        var check = IndexDesign.Check(new IndexDraft("IX_A", ["DATUM", "NOTIZ", "DATUM", "GIBTS_NICHT"]), Table, []);

        Assert.Equal(
        [
            "DATUM ist doppelt gewählt.",
            "NOTIZ ist eine CLOB-Spalte und lässt sich nicht indizieren.",
            "Die Tabelle hat keine Spalte GIBTS_NICHT.",
        ], check.Problems);
    }

    [Fact]
    public void Column_names_are_exact()
    {
        var check = IndexDesign.Check(new IndexDraft("IX_A", ["STATUS"]), Table, []);

        Assert.Equal(["Die Tabelle hat keine Spalte STATUS."], check.Problems);
    }

    [Fact]
    public void Same_column_list_as_an_existing_index_stops_the_draft_also_unique()
    {
        var existing = Index("IX_AUFTRAG_KUNDE", false, "KUNDE_ID", "DATUM");

        Assert.Equal(["IX_AUFTRAG_KUNDE indiziert genau diese Spalten schon in dieser Reihenfolge."],
            IndexDesign.Check(new IndexDraft("UX", ["KUNDE_ID", "DATUM"], Unique: true), Table, [existing]).Problems);
        Assert.Equal(["PK_AUFTRAG indiziert genau diese Spalten schon in dieser Reihenfolge."],
            IndexDesign.Check(new IndexDraft("IX", ["ID"]), Table, [PrimaryKey]).Problems);
    }

    [Fact]
    public void Other_order_of_the_same_columns_is_allowed_but_covered()
    {
        var existing = Index("IX_AUFTRAG_KUNDE", false, "KUNDE_ID", "DATUM");

        var check = IndexDesign.Check(new IndexDraft("IX_B", ["DATUM", "KUNDE_ID"]), Table, [existing]);

        Assert.True(check.CanRun);
        Assert.Equal(["IX_AUFTRAG_KUNDE beginnt schon mit diesen Spalten – ein eigener Index ist meist überflüssig."], check.Warnings);
    }

    [Fact]
    public void An_index_starting_with_the_columns_is_a_warning_unless_unique()
    {
        var existing = Index("IX_AUFTRAG_KUNDE", false, "KUNDE_ID", "DATUM");

        Assert.Single(IndexDesign.Check(new IndexDraft("IX_B", ["KUNDE_ID"]), Table, [existing]).Warnings);
        Assert.Empty(IndexDesign.Check(new IndexDraft("UX_B", ["KUNDE_ID"], Unique: true), Table, [existing]).Warnings);
    }

    [Fact]
    public void Another_direction_is_another_index_and_a_descending_one_stands_in_for_none()
    {
        IndexInfo Descending(string name, string column) => Index(name, false, column) with { Columns = [new IndexColumn(column, false, true)] };
        IndexDraft Draft(params string[] descending) => new("IX_B", ["KUNDE_ID", "DATUM"]) { Descending = descending.ToHashSet() };
        var ascending = Index("IX_KUNDE_DATUM", false, "KUNDE_ID", "DATUM");
        var mixed = Index("IX_KUNDE_DATUM_DESC", false, "KUNDE_ID") with { Columns = [new IndexColumn("KUNDE_ID", false, false), new IndexColumn("DATUM", false, true)] };

        Assert.Empty(IndexDesign.Check(Draft("DATUM"), Table, [ascending]).Problems); // Oracle has both
        Assert.Equal(["IX_KUNDE_DATUM_DESC indiziert genau diese Spalten schon in dieser Reihenfolge."], IndexDesign.Check(Draft("DATUM"), Table, [mixed]).Problems);
        Assert.Empty(IndexDesign.Check(new IndexDraft("IX_B", ["KUNDE_ID"]), Table, [Descending("IX_KUNDE_DESC", "KUNDE_ID")]).Warnings); // backs no foreign key
        Assert.Empty(IndexDesign.Check(Draft("DATUM"), Table, [Index("IX_KUNDE", false, "KUNDE_ID")]).Warnings); // a sort order: nothing stands in
    }

    [Fact]
    public void All_columns_descending_is_a_warning()
    {
        Assert.Equal(["Alle Spalten absteigend: Ein aufsteigender Index dient denselben Sortierungen (Oracle liest ihn rückwärts) und taugt auch für Fremdschlüssel."],
            IndexDesign.Check(new IndexDraft("IX_B", ["KUNDE_ID", "DATUM"]) { Descending = new HashSet<string> { "KUNDE_ID", "DATUM" } }, Table, []).Warnings);
    }

    [Fact]
    public void A_tablespace_without_quota_is_a_warning_and_a_bad_name_a_problem()
    {
        var quota = Tablespaces.None with { Quota = new HashSet<string> { "USERS" } };
        IndexDraft In(string tablespace) => new("IX_B", ["KUNDE_ID"]) { Tablespace = tablespace };

        Assert.Equal(["SHOP hat im Tablespace INDX kein Kontingent: Oracle lehnt die erste Zeile ab (ORA-01950), bei einer leeren Tabelle erst, wenn sie eine bekommt."],
            IndexDesign.Check(In("INDX"), Table, [], tablespaces: quota).Warnings);
        Assert.Empty(IndexDesign.Check(In("USERS"), Table, [], tablespaces: quota).Warnings);
        Assert.Empty(IndexDesign.Check(In("INDX"), Table, [], tablespaces: Tablespaces.None).Warnings); // unlimited or another schema: nothing to check
        Assert.False(IndexDesign.Check(In("IN\"DX"), Table, []).CanRun);
        Assert.Equal(["Eine temporäre Tabelle hält ihre Indizes im temporären Tablespace – Oracle würde die Wahl ignorieren."],
            IndexDesign.Check(In("USERS"), Table, [], tablespaces: quota with { TemporaryTable = true }).Problems);
    }

    [Fact]
    public void An_expression_index_is_no_twin()
    {
        var expression = new IndexInfo("SHOP", "IX_UPPER", "FUNCTION-BASED NORMAL", false, "VALID", [new IndexColumn("UPPER(\"Status\")", true, false)], null, false, true);

        var check = IndexDesign.Check(new IndexDraft("IX_STATUS", ["Status"]), Table, [expression]);

        Assert.True(check.CanRun);
        Assert.Empty(check.Warnings);
    }
}
