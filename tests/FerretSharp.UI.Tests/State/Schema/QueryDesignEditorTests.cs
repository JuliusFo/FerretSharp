using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

public class QueryDesignEditorTests
{
    private const string Owner = "APP";

    private static ColumnInfo Col(string name, string type = "NUMBER", bool nullable = true) =>
        new(name, type, type == "VARCHAR2" ? 50 : null, false, type == "NUMBER" ? 10 : null, type == "NUMBER" ? 0 : null, nullable, false, null, 0);

    private static TableDetails Table(string name, params ColumnInfo[] columns) =>
        new(new TableSummary(Owner, name, TableKind.Table), columns, ["ID"], [], false);

    private static readonly TableDetails Auftrag = Table("AUFTRAG", Col("ID", nullable: false), Col("NR"), Col("KUNDE_ID", nullable: false), Col("NOTIZ", "CLOB"));
    private static readonly TableDetails Kunde = Table("KUNDE", Col("ID", nullable: false), Col("NAME", "VARCHAR2"));
    private static readonly TableDetails Position = Table("POSITION", Col("ID", nullable: false), Col("AUFTRAG_ID", nullable: false), Col("MENGE"));

    private static readonly ForeignKeyInfo AuftragKunde = new("FK_AUFTRAG_KUNDE", Auftrag.Table.Ref, ["KUNDE_ID"], Kunde.Table.Ref, ["ID"], FkSource.Declared) { Enforced = true };
    private static readonly ForeignKeyInfo PositionAuftrag = new("FK_POSITION_AUFTRAG", Position.Table.Ref, ["AUFTRAG_ID"], Auftrag.Table.Ref, ["ID"], FkSource.Declared);
    private static readonly RelationGraph Graph = new([AuftragKunde, PositionAuftrag]);

    private static (TableTab Tab, QueryDesignEditor Editor) Open()
    {
        var tab = new TableTab(Guid.NewGuid(), Auftrag.Table);
        var editor = new QueryDesignEditor(tab);
        editor.Load(Graph);
        return (tab, editor);
    }

    private static void LoadAll(QueryDesignEditor editor)
    {
        foreach (var table in editor.Missing)
        {
            editor.Loaded(new[] { Auftrag, Kunde, Position }.Single(t => t.Table.Ref == table));
        }
    }

    [Fact]
    public void A_click_on_an_edge_adds_the_step_from_the_selected_node_and_a_second_click_takes_it_out_with_what_hangs_on_it()
    {
        var (tab, editor) = Open();

        Assert.True(editor.ToggleEdge(PositionAuftrag));
        Assert.Equal(Position.Table.Ref, editor.Selected!.Table); // the new node is selected
        Assert.True(editor.Selected.IsChosenChild);

        // From the position the relationship to the order would only lead back: it starts at the root, which has it already.
        Assert.True(editor.ToggleEdge(AuftragKunde));
        Assert.Equal(3, tab.Design!.Nodes.Count);

        editor.Select(editor.Design!.Nodes[0]);
        Assert.True(editor.ToggleEdge(PositionAuftrag));
        Assert.Equal([Auftrag.Table.Ref, Kunde.Table.Ref], editor.Design!.Nodes.Select(n => n.Table));
        Assert.False(editor.ToggleEdge(new ForeignKeyInfo("X", new TableRef(Owner, "A"), ["B_ID"], new TableRef(Owner, "B"), ["ID"], FkSource.Manual)));
    }

    [Fact]
    public void The_sql_waits_for_the_columns_of_every_table_a_column_and_valid_conditions()
    {
        var (_, editor) = Open();
        editor.ToggleEdge(AuftragKunde);

        Assert.Equal([Auftrag.Table.Ref, Kunde.Table.Ref], editor.Missing);
        Assert.Null(editor.Sql(DesignSqlMode.Statement));

        LoadAll(editor);
        Assert.True(editor.Complete);
        Assert.Null(editor.Sql(DesignSqlMode.Statement)); // no column yet

        editor.ToggleColumn(editor.Selected!, "NAME");
        editor.SetFilters(editor.Design!.Nodes[0], [new QueryFilter(FilterCondition.Of("NR", FilterOperator.Equals, "keine Zahl"))]);
        Assert.StartsWith("AUFTRAG: ", editor.Problem, StringComparison.Ordinal);
        Assert.Null(editor.Sql(DesignSqlMode.Statement));

        editor.SetFilters(editor.Design!.Nodes[0], [new QueryFilter(FilterCondition.Of("NR", FilterOperator.Equals, "7"))]);
        Assert.Contains("JOIN \"APP\".\"KUNDE\" t1", editor.Sql(DesignSqlMode.Statement)!.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_column_the_table_no_longer_has_falls_out_of_the_tab_s_query_with_a_note()
    {
        var tab = new TableTab(Guid.NewGuid(), Auftrag.Table)
        {
            Design = QueryDesignState.Empty.Update([], n => n with { Columns = [new QueryColumn("NR"), new QueryColumn("WEG")] }) with { Active = true },
        };
        var editor = new QueryDesignEditor(tab);
        editor.Load(Graph);

        LoadAll(editor);

        Assert.Equal(["NR"], tab.Design!.Nodes[0].Columns.Select(c => c.Column));
        Assert.True(tab.Design.Active);
        Assert.Single(editor.Notes);
        Assert.False(QueryDesignEditor.Selectable(Auftrag.Columns[3])); // a CLOB has no value in the result
    }
}
