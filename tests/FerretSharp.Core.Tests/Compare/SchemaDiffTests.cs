using FerretSharp.Core.Compare;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Compare;

public class SchemaDiffTests
{
    // ---- builders --------------------------------------------------------------------------------------------------

    private static ColumnInfo Col(
        string name, string type = "NUMBER", int? length = null, bool charSemantics = false, int? precision = null, int? scale = null,
        bool nullable = true, bool identity = false, string? defaultValue = null, bool isVirtual = false, bool onNull = false) =>
        new(name, type, length, charSemantics, precision, scale, nullable, identity, defaultValue, 0, null, isVirtual, onNull);

    private static ColumnInfo Id() => Col("ID", precision: 10, scale: 0, nullable: false);

    private static ColumnInfo Text(string name, int length, bool charSemantics = true, bool nullable = true) =>
        Col(name, "VARCHAR2", length: length, charSemantics: charSemantics, nullable: nullable);

    private static ObjectSnapshot Table(
        string name, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<ConstraintInfo>? constraints = null, IReadOnlyList<IndexInfo>? indexes = null) =>
        new(name, TableKind.Table, columns, constraints ?? [], indexes ?? []);

    private static SchemaSnapshot Schema(string owner, params ObjectSnapshot[] objects) => new(owner, DateTimeOffset.UnixEpoch, objects);

    private static SchemaSnapshot Schema(params ObjectSnapshot[] objects) => Schema("ERP", objects);

    private static ConstraintInfo Constraint(
        string name, ConstraintType type, IReadOnlyList<string> columns, string? condition = null, TableRef? references = null,
        IReadOnlyList<string>? referencedColumns = null, string? deleteRule = null, bool enabled = true, bool validated = true,
        bool deferrable = false, bool initiallyDeferred = false) =>
        new(name, type, columns, condition, references, referencedColumns ?? [], deleteRule, enabled, validated, deferrable, initiallyDeferred,
            GeneratedName: name.StartsWith("SYS_C", StringComparison.Ordinal));

    private static ConstraintInfo Pk(string name, params string[] columns) => Constraint(name, ConstraintType.PrimaryKey, columns);

    private static ConstraintInfo Unique(string name, params string[] columns) => Constraint(name, ConstraintType.Unique, columns);

    private static ConstraintInfo Check(string name, string condition) => Constraint(name, ConstraintType.Check, [], condition);

    private static ConstraintInfo Fk(string name, string column, TableRef to, string deleteRule = "NO ACTION") =>
        Constraint(name, ConstraintType.ForeignKey, [column], references: to, referencedColumns: ["ID"], deleteRule: deleteRule);

    private static IndexInfo Index(string name, params IndexColumn[] columns) => Index(name, false, columns);

    private static IndexInfo Index(string name, bool unique, params IndexColumn[] columns) =>
        new("ERP", name, "NORMAL", unique, "VALID", columns, "USERS", false, true);

    private static IndexColumn On(string column, bool descending = false) => new(column, false, descending);

    /// <summary>KUNDEN as on every side of most tests.</summary>
    private static ObjectSnapshot Kunden(string name = "KUNDEN", string pkName = "PK_KUNDEN") =>
        Table(
            name,
            [Id(), Text("NAME", 100, nullable: false), Text("EMAIL", 200)],
            [Pk(pkName, "ID"), Unique("UK_KUNDEN_EMAIL", "EMAIL")],
            [Index(pkName, true, On("ID")), Index("UK_KUNDEN_EMAIL", true, On("EMAIL"))]);

    private static SchemaComparison Compare(params SchemaSnapshot[] sides) => SchemaDiff.Compare(sides, new CompareOptions());

    private static IEnumerable<CompareRow> All(SchemaComparison comparison) => comparison.Objects.SelectMany(o => o.Children.Prepend(o));

    private static CompareRow Row(SchemaComparison comparison, string key) => All(comparison).Single(r => r.Key == key);

    private static CellState[] States(CompareRow row) => row.Cells.Select(c => c.State).ToArray();

    private static int[] Groups(CompareRow row) => row.Cells.Select(c => c.Group).ToArray();

    private const CellState Same = CellState.Same;
    private const CellState Different = CellState.Different;
    private const CellState Missing = CellState.Missing;
    private const CellState OtherCase = CellState.OtherCase;

    // ---- whole schemas ---------------------------------------------------------------------------------------------

    [Fact]
    public void Identical_schemas_have_no_differences()
    {
        var result = Compare(Schema("ERP", Kunden()), Schema("ERP_TEST", Kunden()));

        var kunden = Assert.Single(result.Objects);
        Assert.False(kunden.HasDifferences);
        Assert.Equal(CompareKind.Table, kunden.Kind);
        Assert.Equal(["TABLE", "TABLE"], kunden.Cells.Select(c => c.Definition));
        Assert.All(All(result), row => Assert.Equal([0, 0], Groups(row)));
        Assert.Equal("VARCHAR2(100 CHAR) NOT NULL", Row(result, "KUNDEN/col/NAME").Cells[0].Definition);
        Assert.Equal("PRIMARY KEY (ID)", Row(result, "KUNDEN/pk").Cells[0].Definition);
        Assert.Equal("UNIQUE INDEX (ID)", Row(result, "KUNDEN/idx/PK_KUNDEN").Cells[0].Definition);
    }

    [Fact]
    public void A_single_side_is_the_same_everywhere()
    {
        var result = SchemaDiff.Compare([Schema(Kunden())], new CompareOptions(Reference: 0));

        Assert.All(All(result), row => Assert.Equal([Same], States(row)));
        Assert.False(result.Objects[0].HasDifferences);
    }

    [Fact]
    public void Missing_and_extra_tables_are_rows_of_their_own()
    {
        var auftrag = Table("AUFTRAG", [Id()]);
        var artikel = Table("ARTIKEL", [Id()]);

        var result = Compare(Schema(Kunden(), auftrag), Schema(Kunden(), artikel));

        Assert.Equal(["ARTIKEL", "AUFTRAG", "KUNDEN"], result.Objects.Select(o => o.Name));
        var row = result.Objects[0];
        Assert.Equal([Missing, Same], States(row));
        Assert.Equal([-1, 0], Groups(row));
        Assert.Null(row.Cells[0].Definition);
        Assert.Null(row.Cells[0].Object);
        Assert.Same(artikel, row.Cells[1].Object);
        Assert.Equal([Missing, Same], States(Row(result, "ARTIKEL/col/ID")));
        Assert.Equal([Same, Missing], States(result.Objects[1]));
        Assert.False(result.Objects[2].HasDifferences);
    }

    [Fact]
    public void A_different_child_marks_only_the_child_row()
    {
        var result = Compare(Schema(Table("T", [Text("A", 10)])), Schema(Table("T", [Text("A", 20)])));

        var table = Assert.Single(result.Objects);
        Assert.False(table.Differs);
        Assert.True(table.HasDifferences);
        Assert.Equal([Different, Different], States(Row(result, "T/col/A")));
    }

    [Fact]
    public void Object_kind_and_flags_are_its_definition()
    {
        var view = new ObjectSnapshot("T", TableKind.View, [Id()], [], []);
        var iot = Table("T", [Id()]) with { IsIndexOrganized = true, Partitioned = true };
        var temporary = Table("T", [Id()]) with { Temporary = true };
        var mview = new ObjectSnapshot("T", TableKind.MaterializedView, [Id()], [], []);

        var result = Compare(Schema(Table("T", [Id()])), Schema(view), Schema(iot), Schema(temporary), Schema(mview));

        var row = Assert.Single(result.Objects);
        Assert.Equal(CompareKind.Table, row.Kind);
        Assert.Equal(["TABLE", "VIEW", "TABLE (IOT, PARTITIONED)", "GLOBAL TEMPORARY TABLE", "MATERIALIZED VIEW"], row.Cells.Select(c => c.Definition));
        Assert.All(row.Cells, c => Assert.Equal(Different, c.State));
        Assert.Equal([0, 1, 2, 3, 4], Groups(row));
    }

    [Fact]
    public void View_columns_compare_type_and_nullability_only()
    {
        var table = Table("V", [Col("A", precision: 5, scale: 0, defaultValue: "1")]);
        var view = new ObjectSnapshot("V", TableKind.View, [Col("A", precision: 5, scale: 0, defaultValue: "1")], [], []);

        var result = Compare(Schema(table), Schema(view));

        Assert.Equal(["NUMBER(5) NULL DEFAULT 1", "NUMBER(5) NULL"], Row(result, "V/col/A").Cells.Select(c => c.Definition));
        Assert.Equal(CompareKind.View, SchemaDiff.Compare([Schema(view), Schema(table)], new CompareOptions()).Objects[0].Kind);
    }

    // ---- columns ---------------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, (ColumnInfo Left, ColumnInfo Right, string LeftText, string RightText)> ColumnCases = new()
    {
        ["type"] = (Col("C", "DATE"), Col("C", "TIMESTAMP(6)"), "DATE NULL", "TIMESTAMP(6) NULL"),
        ["byte vs char"] = (Text("C", 50), Text("C", 50, charSemantics: false), "VARCHAR2(50 CHAR) NULL", "VARCHAR2(50) NULL"),
        ["length"] = (Text("C", 50), Text("C", 60), "VARCHAR2(50 CHAR) NULL", "VARCHAR2(60 CHAR) NULL"),
        ["precision"] = (Col("C", precision: 10, scale: 0), Col("C", precision: 12, scale: 0), "NUMBER(10) NULL", "NUMBER(12) NULL"),
        ["scale"] = (Col("C", precision: 12, scale: 2), Col("C", precision: 12, scale: 3), "NUMBER(12,2) NULL", "NUMBER(12,3) NULL"),
        ["nullability"] = (Text("C", 5, nullable: false), Text("C", 5), "VARCHAR2(5 CHAR) NOT NULL", "VARCHAR2(5 CHAR) NULL"),
        ["default"] = (Col("C", defaultValue: "0"), Col("C", defaultValue: "1"), "NUMBER NULL DEFAULT 0", "NUMBER NULL DEFAULT 1"),
        ["no default"] = (Col("C", defaultValue: "0"), Col("C"), "NUMBER NULL DEFAULT 0", "NUMBER NULL"),
        ["default on null"] = (Col("C", nullable: false, defaultValue: "0", onNull: true), Col("C", nullable: false, defaultValue: "0"),
            "NUMBER NOT NULL DEFAULT ON NULL 0", "NUMBER NOT NULL DEFAULT 0"),
        ["identity"] = (Col("C", nullable: false, identity: true, defaultValue: "\"ERP\".\"ISEQ$$_1\".nextval"), Col("C", nullable: false),
            "NUMBER NOT NULL IDENTITY", "NUMBER NOT NULL"),
        ["virtual"] = (Col("C", isVirtual: true, defaultValue: "\"A\"+\"B\""), Col("C", isVirtual: true, defaultValue: "\"A\"-\"B\""),
            "NUMBER NULL VIRTUAL AS (\"A\"+\"B\")", "NUMBER NULL VIRTUAL AS (\"A\"-\"B\")"),
        ["virtual vs real"] = (Col("C", isVirtual: true, defaultValue: "1"), Col("C", defaultValue: "1"), "NUMBER NULL VIRTUAL AS (1)", "NUMBER NULL DEFAULT 1"),
    };

    [Theory]
    [InlineData("type")]
    [InlineData("byte vs char")]
    [InlineData("length")]
    [InlineData("precision")]
    [InlineData("scale")]
    [InlineData("nullability")]
    [InlineData("default")]
    [InlineData("no default")]
    [InlineData("default on null")]
    [InlineData("identity")]
    [InlineData("virtual")]
    [InlineData("virtual vs real")]
    public void Column_differences_are_found(string name)
    {
        var (left, right, leftText, rightText) = ColumnCases[name];

        var row = Row(Compare(Schema(Table("T", [left])), Schema(Table("T", [right]))), "T/col/C");

        Assert.Equal([Different, Different], States(row));
        Assert.Equal([0, 1], Groups(row));
        Assert.Equal([leftText, rightText], row.Cells.Select(c => c.Definition));
        Assert.Same(left, row.Cells[0].Column);
        Assert.Same(right, row.Cells[1].Column);
    }

    [Theory]
    [InlineData("'x'  \n", "'x'")]
    [InlineData("sysdate\n  ", "sysdate")]
    [InlineData("  nvl(a,\n   0)", "nvl(a, 0)")]
    [InlineData("NULL ", null)]
    [InlineData("null", "")]
    public void Default_text_is_normalized(string left, string? right)
    {
        var row = Row(Compare(Schema(Table("T", [Col("C", defaultValue: left)])), Schema(Table("T", [Col("C", defaultValue: right)]))), "T/col/C");

        Assert.Equal([Same, Same], States(row));
    }

    [Fact]
    public void Whitespace_inside_string_literals_counts()
    {
        var row = Row(Compare(Schema(Table("T", [Col("C", defaultValue: "'a  b'")])), Schema(Table("T", [Col("C", defaultValue: "'a b'")]))), "T/col/C");

        Assert.Equal([Different, Different], States(row));
        Assert.Equal("NUMBER NULL DEFAULT 'a  b'", row.Cells[0].Definition);
    }

    [Fact]
    public void Identity_columns_ignore_their_generated_sequence()
    {
        var left = Col("ID", nullable: false, identity: true, defaultValue: "\"ERP\".\"ISEQ$$_73001\".nextval");
        var right = Col("ID", nullable: false, identity: true, defaultValue: "\"ERP_TEST\".\"ISEQ$$_81234\".nextval");

        var row = Row(Compare(Schema(Table("T", [left])), Schema("ERP_TEST", Table("T", [right]))), "T/col/ID");

        Assert.Equal([Same, Same], States(row));
        Assert.Equal("NUMBER NOT NULL IDENTITY", row.Cells[0].Definition);
    }

    [Fact]
    public void Comments_do_not_count()
    {
        var result = Compare(Schema(Table("T", [Id() with { Comment = "Schlüssel" }])), Schema(Table("T", [Id()])));

        Assert.False(result.Objects[0].HasDifferences);
    }

    [Fact]
    public void Column_order_counts_only_when_asked()
    {
        var left = Schema(Table("T", [Id(), Text("NAME", 10)]));
        var right = Schema(Table("T", [Text("NAME", 10), Id()]));

        Assert.False(SchemaDiff.Compare([left, right], new CompareOptions()).Objects[0].HasDifferences);

        var ordered = SchemaDiff.Compare([left, right], new CompareOptions(ColumnOrder: true));
        var id = Row(ordered, "T/col/ID");
        Assert.Equal([Different, Different], States(id));
        Assert.Equal(["NUMBER(10) NOT NULL · #1", "NUMBER(10) NOT NULL · #2"], id.Cells.Select(c => c.Definition));
        Assert.Equal(["ID", "NAME"], ordered.Objects[0].Children.Select(c => c.Name));
    }

    // ---- letter case -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_table_only_in_another_letter_case_is_matched_as_other_case()
    {
        var result = Compare(Schema(Kunden()), Schema(Kunden("Kunden")), Schema(Kunden()));

        var row = Assert.Single(result.Objects);
        Assert.Equal("KUNDEN", row.Name);
        Assert.Equal([Same, OtherCase, Same], States(row));
        Assert.Equal([null, "Kunden", null], row.Cells.Select(c => c.Name));
        Assert.Equal([0, 0, 0], Groups(row));
        Assert.All(row.Children, child => Assert.False(child.Differs));
        Assert.True(row.HasDifferences);
    }

    [Fact]
    public void The_row_is_named_as_on_the_reference_side()
    {
        var result = SchemaDiff.Compare([Schema(Kunden()), Schema(Kunden("Kunden"))], new CompareOptions(Reference: 1));

        var row = Assert.Single(result.Objects);
        Assert.Equal("Kunden", row.Name);
        Assert.Equal("Kunden", row.Key);
        Assert.Equal([OtherCase, Same], States(row));
        Assert.Equal("KUNDEN", row.Cells[0].Name);
    }

    [Fact]
    public void A_side_with_both_spellings_keeps_them_apart()
    {
        var result = Compare(Schema(Kunden()), Schema(Kunden("Kunden")), Schema(Kunden(), Kunden("Kunden")));

        Assert.Equal(["KUNDEN", "Kunden"], result.Objects.Select(o => o.Name));
        Assert.Equal([Same, Missing, Same], States(result.Objects[0]));
        Assert.Equal([Missing, Same, Same], States(result.Objects[1]));
    }

    [Fact]
    public void A_column_only_in_another_letter_case_is_matched_as_other_case()
    {
        var result = Compare(Schema(Table("T", [Text("Chargenr", 20)])), Schema(Table("T", [Text("CHARGENR", 20)])));

        var row = Assert.Single(result.Objects[0].Children);
        Assert.Equal("Chargenr", row.Name);
        Assert.Equal([Same, OtherCase], States(row));
        Assert.Equal("CHARGENR", row.Cells[1].Name);
    }

    // ---- constraints -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_primary_key_is_matched_whatever_its_name()
    {
        var result = Compare(Schema(Kunden(pkName: "PK_KUNDEN")), Schema(Kunden(pkName: "KUNDEN_PK")), Schema(Kunden(pkName: "SYS_C0042")));

        var pk = Row(result, "KUNDEN/pk");
        Assert.Equal(CompareKind.PrimaryKey, pk.Kind);
        Assert.Equal("PK_KUNDEN", pk.Name);
        Assert.Equal([Same, Same, Same], States(pk));
        Assert.Equal([null, "KUNDEN_PK", "SYS_C0042"], pk.Cells.Select(c => c.Name));
        Assert.Equal("SYS_C0042", pk.Cells[2].Constraint!.Name);
    }

    [Fact]
    public void A_generated_primary_key_is_named_by_its_content()
    {
        var pk = Row(Compare(Schema(Kunden(pkName: "SYS_C0001")), Schema(Kunden(pkName: "SYS_C0099"))), "KUNDEN/pk");

        Assert.Equal("PRIMARY KEY (ID)", pk.Name);
        Assert.Equal(["SYS_C0001", "SYS_C0099"], pk.Cells.Select(c => c.Name));
    }

    [Fact]
    public void Generated_constraints_are_matched_by_content()
    {
        var left = Table("POS", [Id(), Col("MENGE")], [Check("SYS_C0010", "MENGE > 0"), Check("SYS_C0011", "MENGE < 1000")]);
        var right = Table("POS", [Id(), Col("MENGE")], [Check("SYS_C0777", "MENGE <  1000"), Check("SYS_C0778", "MENGE\n > 0")]);

        var result = Compare(Schema(left), Schema(right));

        var checks = result.Objects[0].Children.Where(c => c.Kind == CompareKind.Check).ToList();
        Assert.Equal(["CHECK (MENGE < 1000)", "CHECK (MENGE > 0)"], checks.Select(c => c.Name));
        Assert.All(checks, c => Assert.Equal([Same, Same], States(c)));
        Assert.Equal(["SYS_C0010", "SYS_C0778"], checks[1].Cells.Select(c => c.Name));
        Assert.Equal("POS/con/CHECK (MENGE > 0)", checks[1].Key);
    }

    [Fact]
    public void A_generated_constraint_joins_the_named_one_with_the_same_content()
    {
        var left = Table("POS", [Col("MENGE")], [Check("CK_POS_MENGE", "MENGE > 0")]);
        var right = Table("POS", [Col("MENGE")], [Check("SYS_C0010", "MENGE > 0")]);

        var result = Compare(Schema(left), Schema(right));

        var check = Assert.Single(result.Objects[0].Children, c => c.Kind == CompareKind.Check);
        Assert.Equal("CK_POS_MENGE", check.Name);
        Assert.Equal("POS/con/CK_POS_MENGE", check.Key);
        Assert.Equal([Same, Same], States(check));
        Assert.Equal([null, "SYS_C0010"], check.Cells.Select(c => c.Name));
    }

    [Fact]
    public void Named_constraints_with_different_names_are_different_rows()
    {
        var left = Table("POS", [Col("MENGE")], [Check("CK_A", "MENGE > 0")]);
        var right = Table("POS", [Col("MENGE")], [Check("CK_B", "MENGE > 0")]);

        var checks = Compare(Schema(left), Schema(right)).Objects[0].Children.Where(c => c.Kind == CompareKind.Check).ToList();

        Assert.Equal(["CK_A", "CK_B"], checks.Select(c => c.Name));
        Assert.Equal([Same, Missing], States(checks[0]));
        Assert.Equal([Missing, Same], States(checks[1]));
    }

    [Fact]
    public void A_named_constraint_with_other_content_differs()
    {
        var left = Table("POS", [Col("MENGE")], [Check("CK_MENGE", "MENGE > 0")]);
        var right = Table("POS", [Col("MENGE")], [Check("CK_MENGE", "MENGE >= 0")]);

        var check = Row(Compare(Schema(left), Schema(right)), "POS/con/CK_MENGE");

        Assert.Equal([Different, Different], States(check));
        Assert.Equal(["CHECK (MENGE > 0)", "CHECK (MENGE >= 0)"], check.Cells.Select(c => c.Definition));
    }

    [Fact]
    public void Constraint_state_counts()
    {
        var left = Table("T", [Col("A")], [Unique("UK_A", "A")]);
        var right = Table("T", [Col("A")], [Constraint("UK_A", ConstraintType.Unique, ["A"], enabled: false, validated: false, deferrable: true, initiallyDeferred: true)]);

        var row = Row(Compare(Schema(left), Schema(right)), "T/con/UK_A");

        Assert.Equal(["UNIQUE (A)", "UNIQUE (A) DISABLED NOVALIDATE DEFERRABLE INITIALLY DEFERRED"], row.Cells.Select(c => c.Definition));
        Assert.Equal([Different, Different], States(row));
    }

    [Fact]
    public void A_different_delete_rule_is_a_difference()
    {
        var kunden = new TableRef("ERP", "KUNDEN");
        var left = Table("AUFTRAG", [Col("KUNDE_ID")], [Fk("FK_AUFTRAG_KUNDE", "KUNDE_ID", kunden, "CASCADE")]);
        var right = Table("AUFTRAG", [Col("KUNDE_ID")], [Fk("FK_AUFTRAG_KUNDE", "KUNDE_ID", kunden)]);

        var fk = Row(Compare(Schema(left), Schema(right)), "AUFTRAG/con/FK_AUFTRAG_KUNDE");

        Assert.Equal(CompareKind.ForeignKey, fk.Kind);
        Assert.Equal([Different, Different], States(fk));
        Assert.Equal(
            ["FOREIGN KEY (KUNDE_ID) REFERENCES KUNDEN (ID) ON DELETE CASCADE", "FOREIGN KEY (KUNDE_ID) REFERENCES KUNDEN (ID)"],
            fk.Cells.Select(c => c.Definition));
    }

    [Fact]
    public void Foreign_keys_into_the_own_schema_compare_without_owner()
    {
        ObjectSnapshot Auftrag(string kundenOwner) =>
            Table("AUFTRAG", [Col("KUNDE_ID"), Col("LAND_ID")], [
                Fk("FK_AUFTRAG_KUNDE", "KUNDE_ID", new TableRef(kundenOwner, "KUNDEN")),
                Fk("FK_AUFTRAG_LAND", "LAND_ID", new TableRef("STAMM", "LAND")),
            ]);

        var result = Compare(Schema("ERP", Auftrag("ERP")), Schema("ERP_TEST", Auftrag("ERP_TEST")), Schema("ERP_DEV", Auftrag("ERP_TEST")));

        var kunde = Row(result, "AUFTRAG/con/FK_AUFTRAG_KUNDE");
        Assert.Equal([0, 0, 1], Groups(kunde));
        Assert.Equal("FOREIGN KEY (KUNDE_ID) REFERENCES KUNDEN (ID)", kunde.Cells[0].Definition);
        Assert.Equal("FOREIGN KEY (KUNDE_ID) REFERENCES ERP_TEST.KUNDEN (ID)", kunde.Cells[2].Definition);

        var land = Row(result, "AUFTRAG/con/FK_AUFTRAG_LAND");
        Assert.Equal([Same, Same, Same], States(land));
        Assert.Equal("FOREIGN KEY (LAND_ID) REFERENCES STAMM.LAND (ID)", land.Cells[0].Definition);
    }

    [Fact]
    public void Generated_foreign_keys_match_by_columns_and_reference_and_show_a_different_rule()
    {
        var left = Table("AUFTRAG", [Col("KUNDE_ID")], [Fk("SYS_C0100", "KUNDE_ID", new TableRef("ERP", "KUNDEN"), "CASCADE")]);
        var right = Table("AUFTRAG", [Col("KUNDE_ID")], [Fk("SYS_C0200", "KUNDE_ID", new TableRef("ERP_TEST", "KUNDEN"))]);

        var fk = Assert.Single(Compare(Schema(left), Schema("ERP_TEST", right)).Objects[0].Children, c => c.Kind == CompareKind.ForeignKey);

        Assert.Equal("FOREIGN KEY (KUNDE_ID) REFERENCES KUNDEN (ID)", fk.Name);
        Assert.Equal([Different, Different], States(fk));
    }

    [Fact]
    public void Generated_not_null_checks_and_view_constraints_are_left_out()
    {
        var table = Table("T", [Col("A", nullable: false)], [
            Check("SYS_C0001", "\"A\" IS NOT NULL"),
            Constraint("SYS_C0002", ConstraintType.ViewReadOnly, []),
        ]);

        Assert.Single(Compare(Schema(table), Schema(table)).Objects[0].Children);
    }

    // ---- indexes ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Index_direction_and_expressions_count()
    {
        var left = Table("T", [Text("NAME", 50)], indexes: [Index("IX_NAME", On("NAME")), Index("IX_UPPER", new IndexColumn("UPPER(\"NAME\")", true, false))]);
        var right = Table("T", [Text("NAME", 50)], indexes: [Index("IX_NAME", On("NAME", descending: true)), Index("IX_UPPER", new IndexColumn("LOWER(\"NAME\")", true, false))]);

        var result = Compare(Schema(left), Schema(right));

        var name = Row(result, "T/idx/IX_NAME");
        Assert.Equal(CompareKind.Index, name.Kind);
        Assert.Equal(["INDEX (NAME)", "INDEX (NAME DESC)"], name.Cells.Select(c => c.Definition));
        Assert.Equal([Different, Different], States(name));
        Assert.Equal(["INDEX (UPPER(\"NAME\"))", "INDEX (LOWER(\"NAME\"))"], Row(result, "T/idx/IX_UPPER").Cells.Select(c => c.Definition));
    }

    [Fact]
    public void Index_storage_and_status_do_not_count_but_visibility_and_type_do()
    {
        var plain = Index("IX_A", On("A"));
        var elsewhere = plain with { Owner = "ERP_TEST", Tablespace = "IDX", Status = "UNUSABLE", Partitioned = true };
        var invisible = plain with { Visible = false };
        var bitmap = plain with { IndexType = "BITMAP" };
        var reverse = plain with { IndexType = "NORMAL/REV" };

        var row = Row(Compare(
            Schema(Table("T", [Col("A")], indexes: [plain])),
            Schema(Table("T", [Col("A")], indexes: [elsewhere])),
            Schema(Table("T", [Col("A")], indexes: [invisible])),
            Schema(Table("T", [Col("A")], indexes: [bitmap])),
            Schema(Table("T", [Col("A")], indexes: [reverse]))), "T/idx/IX_A");

        Assert.Equal(["INDEX (A)", "INDEX (A)", "INDEX (A) INVISIBLE", "BITMAP INDEX (A)", "INDEX (A) REVERSE"], row.Cells.Select(c => c.Definition));
        Assert.Equal([0, 0, 1, 2, 3], Groups(row));
        Assert.Same(elsewhere, row.Cells[1].Index);
    }

    [Fact]
    public void Generated_indexes_are_matched_by_columns()
    {
        var left = Table("T", [Col("A")], indexes: [Index("PK_T", true, On("A"))]);
        var middle = Table("T", [Col("A")], indexes: [Index("SYS_C0042", true, On("A"))]);
        var right = Table("T", [Col("A")], indexes: [Index("SYS_C0777", false, On("A"))]);

        var result = Compare(Schema(left), Schema(middle), Schema(right));

        var index = Assert.Single(result.Objects[0].Children, c => c.Kind == CompareKind.Index);
        Assert.Equal("PK_T", index.Name);
        Assert.Equal([null, "SYS_C0042", "SYS_C0777"], index.Cells.Select(c => c.Name));
        Assert.Equal([Different, Different, Different], States(index));
        Assert.Equal([0, 0, 1], Groups(index));

        var generated = Assert.Single(Compare(Schema(middle), Schema(right)).Objects[0].Children, c => c.Kind == CompareKind.Index);
        Assert.Equal("UNIQUE INDEX (A)", generated.Name);
        Assert.Equal("T/idx/UNIQUE INDEX (A)", generated.Key);
    }

    [Fact]
    public void The_top_index_of_an_index_organized_table_is_left_out()
    {
        var iot = Table("T", [Id()], [Pk("PK_T", "ID")], [Index("SYS_IOT_TOP_73001", true, On("ID")) with { IndexType = "IOT - TOP" }]) with { IsIndexOrganized = true };

        var children = Compare(Schema(iot), Schema(iot)).Objects[0].Children;

        Assert.Equal([CompareKind.Column, CompareKind.PrimaryKey], children.Select(c => c.Kind));
    }

    // ---- groups and reference --------------------------------------------------------------------------------------

    private static SchemaComparison DevTestProd(int? reference)
    {
        SchemaSnapshot Side(string owner, int length) => Schema(owner, Table("KUNDEN", [Id(), Text("EMAIL", length)]));
        return SchemaDiff.Compare([Side("ERP_DEV", 200), Side("ERP_TEST", 200), Side("ERP", 100)], new CompareOptions(reference));
    }

    [Fact]
    public void Without_reference_sides_are_grouped_by_definition()
    {
        var email = Row(DevTestProd(reference: null), "KUNDEN/col/EMAIL");

        Assert.Equal([0, 0, 1], Groups(email));
        Assert.Equal([Different, Different, Different], States(email));
        Assert.Equal([Same, Same, Same], States(Row(DevTestProd(reference: null), "KUNDEN/col/ID")));
    }

    [Theory]
    [InlineData(0, new[] { Same, Same, Different })]
    [InlineData(1, new[] { Same, Same, Different })]
    [InlineData(2, new[] { Different, Different, Same })]
    public void With_reference_sides_are_measured_against_it(int reference, CellState[] expected)
    {
        var email = Row(DevTestProd(reference), "KUNDEN/col/EMAIL");

        Assert.Equal(expected, States(email));
        Assert.Equal([0, 0, 1], Groups(email));
    }

    [Fact]
    public void When_the_reference_lacks_the_object_every_side_having_it_differs()
    {
        var result = SchemaDiff.Compare(
            [Schema(Kunden()), Schema(Kunden(), Table("NEU", [Id()])), Schema(Kunden(), Table("NEU", [Id()]))],
            new CompareOptions(Reference: 0));

        var neu = result.Objects.Single(o => o.Name == "NEU");
        Assert.Equal([Missing, Different, Different], States(neu));
        Assert.Equal([Missing, Different, Different], States(Row(result, "NEU/col/ID")));
        Assert.Equal([-1, 0, 0], Groups(neu));
    }

    [Fact]
    public void An_invalid_reference_or_no_sides_are_refused()
    {
        Assert.Throws<ArgumentException>(() => SchemaDiff.Compare([], new CompareOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => SchemaDiff.Compare([Schema(Kunden())], new CompareOptions(Reference: 1)));
    }

    // ---- order and keys --------------------------------------------------------------------------------------------

    [Fact]
    public void Objects_are_ordered_by_name_and_children_by_kind()
    {
        var left = Table(
            "T",
            [Id(), Col("Z"), Col("A")],
            [Check("CK_B", "A > 0"), Fk("FK_A", "A", new TableRef("ERP", "X")), Unique("UK_Z", "Z"), Pk("PK_T", "ID"), Check("CK_A", "Z > 0")],
            [Index("IX_Z", On("Z")), Index("IX_A", On("A"))]);
        var right = Table("T", [Id(), Col("NEU"), Col("Z")]);

        var result = Compare(Schema(Table("b", [Id()]), left, Table("A", [Id()])), Schema(right));

        Assert.Equal(["A", "T", "b"], result.Objects.Select(o => o.Name));
        Assert.Equal(["ID", "Z", "A", "NEU", "PK_T", "UK_Z", "FK_A", "CK_A", "CK_B", "IX_A", "IX_Z"], result.Objects[1].Children.Select(c => c.Name));

        var fromRight = SchemaDiff.Compare([Schema(left), Schema(right)], new CompareOptions(Reference: 1));
        Assert.Equal(["ID", "NEU", "Z", "A"], fromRight.Objects[0].Children.Where(c => c.Kind == CompareKind.Column).Select(c => c.Name));
    }

    [Fact]
    public void Keys_are_unique()
    {
        var weird = Table("T/col/A", [Id()]);
        var table = Table("T", [Col("A"), Col("B")], [Check("SYS_C0001", "A > 0"), Check("SYS_C0002", "A > 0")]);

        var result = Compare(Schema(weird, table, Kunden()), Schema(table, Kunden("Kunden")));

        var keys = All(result).Select(r => r.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("T/col/A", keys);
        Assert.Contains("T/col/A#2", keys);
        Assert.Contains("T/con/CHECK (A > 0)", keys);
        Assert.Contains("T/con/CHECK (A > 0)#2", keys);
        Assert.Contains("KUNDEN/con/UK_KUNDEN_EMAIL", keys);
        Assert.All(result.Objects.Single(o => o.Name == "T").Children.Where(c => c.Kind == CompareKind.Check), c => Assert.Equal([Same, Same], States(c)));
    }
}
