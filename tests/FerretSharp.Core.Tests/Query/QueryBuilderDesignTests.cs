using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public class QueryBuilderDesignTests
{
    private const string Owner = "APP";

    private static ColumnInfo Col(string name, string type = "NUMBER", bool nullable = true) =>
        new(name, type, type == "VARCHAR2" ? 50 : null, false, type == "NUMBER" ? 10 : null, type == "NUMBER" ? 0 : null, nullable, false, null, 0);

    private static TableDetails Table(string name, params ColumnInfo[] columns) =>
        new(new TableSummary(Owner, name, TableKind.Table), columns, ["ID"], [], false);

    private static readonly TableDetails Land = Table("LAND", Col("ID", nullable: false), Col("NAME", "VARCHAR2", nullable: false));
    private static readonly TableDetails Kunde = Table("KUNDE", Col("ID", nullable: false), Col("NAME", "VARCHAR2", nullable: false), Col("LAND_ID"));
    private static readonly TableDetails Auftrag = Table("AUFTRAG",
        Col("ID", nullable: false), Col("NR", nullable: false), Col("DATUM", "DATE"), Col("KUNDE_ID", nullable: false), Col("BEARBEITER_ID"));
    private static readonly TableDetails Mitarbeiter = Table("MITARBEITER", Col("ID", nullable: false), Col("NAME", "VARCHAR2"));
    private static readonly TableDetails Position = new(new TableSummary(Owner, "POSITION", TableKind.Table),
        [Col("AUFTRAG_ID", nullable: false), Col("POS_NR", nullable: false), Col("MENGE"), Col("ARTIKEL_ID")], ["AUFTRAG_ID", "POS_NR"], [], false);
    private static readonly TableDetails Artikel = Table("ARTIKEL", Col("ID", nullable: false), Col("NAME", "VARCHAR2", nullable: false));

    private static readonly Dictionary<TableRef, TableDetails> Tables =
        new[] { Land, Kunde, Auftrag, Mitarbeiter, Position, Artikel }.ToDictionary(t => t.Table.Ref);

    private static ForeignKeyInfo Fk(string name, TableDetails from, string column, TableDetails to, bool enforced = true) =>
        new(name, from.Table.Ref, [column], to.Table.Ref, ["ID"], FkSource.Declared) { Enforced = enforced };

    private static readonly ForeignKeyInfo AuftragKunde = Fk("FK_AUFTRAG_KUNDE", Auftrag, "KUNDE_ID", Kunde);
    private static readonly ForeignKeyInfo AuftragBearbeiter = Fk("FK_AUFTRAG_BEARBEITER", Auftrag, "BEARBEITER_ID", Mitarbeiter);
    private static readonly ForeignKeyInfo KundeLand = Fk("FK_KUNDE_LAND", Kunde, "LAND_ID", Land);
    private static readonly ForeignKeyInfo PositionAuftrag = Fk("FK_POSITION_AUFTRAG", Position, "AUFTRAG_ID", Auftrag);
    private static readonly ForeignKeyInfo PositionArtikel = Fk("FK_POSITION_ARTIKEL", Position, "ARTIKEL_ID", Artikel, enforced: false);

    private static readonly RelationGraph Graph = new([AuftragKunde, AuftragBearbeiter, KundeLand, PositionAuftrag, PositionArtikel]);

    private static QueryStep Up(ForeignKeyInfo relation) => new(RelationGraph.KeyOf(relation), false);

    private static QueryStep Down(ForeignKeyInfo relation) => new(RelationGraph.KeyOf(relation), true);

    private static QueryNodeState Columns(QueryNodeState node, params string[] columns) => node with { Columns = [.. columns.Select(c => new QueryColumn(c))] };

    private static DesignSql Build(QueryDesignState state, DesignSqlMode mode = DesignSqlMode.Statement) =>
        QueryBuilder.BuildDesign(QueryDesign.Resolve(Auftrag.Table.Ref, state, Graph), t => Tables[t], mode);

    [Fact]
    public void Parents_join_inner_only_where_no_root_row_can_get_lost()
    {
        var state = QueryDesignState.Empty
            .Update([], n => Columns(n, "NR"))
            .Add([], Up(AuftragKunde)).Update([Up(AuftragKunde)], n => Columns(n, "NAME"))
            .Add([Up(AuftragKunde)], Up(KundeLand)).Update([Up(AuftragKunde), Up(KundeLand)], n => Columns(n, "NAME"))
            .Add([], Up(AuftragBearbeiter)).Update([Up(AuftragBearbeiter)], n => Columns(n, "NAME"));

        var sql = Build(state);

        // KUNDE: enforced, KUNDE_ID NOT NULL. LAND: LAND_ID nullable. MITARBEITER: BEARBEITER_ID nullable.
        Assert.Equal(
            """
            SELECT t0."NR" "NR",
                   t1."NAME" "KUNDE_NAME",
                   t2."NAME" "KUNDE_LAND_NAME",
                   t3."NAME" "BEARBEITER_NAME"
              FROM "APP"."AUFTRAG" t0
              JOIN "APP"."KUNDE" t1 ON t0."KUNDE_ID" = t1."ID"
              LEFT JOIN "APP"."LAND" t2 ON t1."LAND_ID" = t2."ID"
              LEFT JOIN "APP"."MITARBEITER" t3 ON t0."BEARBEITER_ID" = t3."ID"
             ORDER BY t0."ID"
            """.ReplaceLineEndings("\n"), sql.Sql);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void After_a_left_join_an_enforced_parent_joins_left_as_well()
    {
        var notNullLand = Tables[Kunde.Table.Ref] with { Columns = [.. Kunde.Columns.Select(c => c with { Nullable = c.Name != "LAND_ID" && c.Nullable })] };
        var tables = new Dictionary<TableRef, TableDetails>(Tables) { [Kunde.Table.Ref] = notNullLand };
        var design = QueryDesign.Resolve(Auftrag.Table.Ref, QueryDesignState.Empty.Add([], Up(AuftragBearbeiter)).Add([], Up(AuftragKunde)).Add([Up(AuftragKunde)], Up(KundeLand)), Graph);

        Assert.True(QueryBuilder.JoinsInner(design.Nodes[3], t => tables[t])); // KUNDE (inner) → LAND
        var weak = new Dictionary<TableRef, TableDetails>(tables) { [Auftrag.Table.Ref] = Auftrag with { Columns = [.. Auftrag.Columns.Select(c => c with { Nullable = true })] } };
        Assert.False(QueryBuilder.JoinsInner(design.Nodes[3], t => weak[t])); // KUNDE joined LEFT now
    }

    [Fact]
    public void A_chosen_child_is_an_outer_apply_with_its_conditions_its_parents_and_the_first_by_its_order()
    {
        var position = new[] { Down(PositionAuftrag) };
        var artikel = new[] { Down(PositionAuftrag), Up(PositionArtikel) };
        var state = QueryDesignState.Empty
            .Update([], n => Columns(n, "NR"))
            .Add([], Down(PositionAuftrag))
            .Update(position, n => Columns(n, "MENGE") with
            {
                Filters = [new QueryFilter(FilterCondition.Of("MENGE", FilterOperator.Gt, "0"))],
                Sorts = [new SortSpec("MENGE", true)],
            })
            .Add(position, Up(PositionArtikel))
            .Update(artikel, n => Columns(n, "NAME") with { Filters = [new QueryFilter(FilterCondition.Of("NAME", FilterOperator.NotEquals, "Alt"))] });

        var sql = Build(state);

        Assert.Equal(
            """
            SELECT t0."NR" "NR",
                   t1."POSITION_MENGE" "POSITION_MENGE",
                   t1."POSITION_ARTIKEL_NAME" "POSITION_ARTIKEL_NAME"
              FROM "APP"."AUFTRAG" t0
              OUTER APPLY (SELECT t1."MENGE" "POSITION_MENGE", t2."NAME" "POSITION_ARTIKEL_NAME"
                      FROM "APP"."POSITION" t1
                      LEFT JOIN "APP"."ARTIKEL" t2 ON t1."ARTIKEL_ID" = t2."ID"
                     WHERE t1."AUFTRAG_ID" = t0."ID"
                       AND t1."MENGE" > :p0
                       AND (t2."NAME" <> :p1 OR t2."NAME" IS NULL)
                     ORDER BY t1."MENGE" DESC, t1."AUFTRAG_ID", t1."POS_NR"
                     FETCH FIRST 1 ROW ONLY) t1
             ORDER BY t0."ID"
            """.ReplaceLineEndings("\n"), sql.Sql);
        Assert.Equal([0m, "Alt"], sql.Parameters.Select(p => p.Value));
        Assert.Equal(["NR", "POSITION_MENGE", "POSITION_ARTIKEL_NAME"], sql.Columns.Select(c => c.Name));
    }

    [Fact]
    public void A_chosen_child_without_columns_changes_no_root_row_and_stays_out()
    {
        var state = QueryDesignState.Empty
            .Update([], n => Columns(n, "NR"))
            .Add([], Down(PositionAuftrag))
            .Update([Down(PositionAuftrag)], n => n with { Filters = [new QueryFilter(FilterCondition.Of("MENGE", FilterOperator.Gt, "0"))] });

        Assert.DoesNotContain("APPLY", Build(state).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_writes_values_as_literals_leaves_parameters_out_with_their_group_and_has_no_order()
    {
        var group = new FilterGroup(1, FilterGroupKind.Any);
        var state = QueryDesignState.Empty.Update([], n => Columns(n, "NR") with
        {
            Filters =
            [
                new QueryFilter(FilterCondition.Of("NR", FilterOperator.Gte, "1000")),
                new QueryFilter(FilterCondition.Of("DATUM", FilterOperator.Gte, "1.10.2026"), "ab"),
                new QueryFilter(FilterCondition.Of("NR", FilterOperator.Equals, "1") with { Group = group }),
                new QueryFilter(FilterCondition.Of("NR", FilterOperator.Equals, "2") with { Group = group }, "nummer"),
            ],
        }).Add([], Up(AuftragKunde)).Update([Up(AuftragKunde)], n => Columns(n, "NAME") with
        {
            Filters = [new QueryFilter(FilterCondition.Of("NAME", FilterOperator.StartsWith, "O'B"))],
        });

        var view = Build(state, DesignSqlMode.View);
        var statement = Build(state);

        Assert.Equal(
            """
            SELECT t0."NR" "NR",
                   t1."NAME" "KUNDE_NAME"
              FROM "APP"."AUFTRAG" t0
              JOIN "APP"."KUNDE" t1 ON t0."KUNDE_ID" = t1."ID"
             WHERE t0."NR" >= 1000
               AND UPPER(t1."NAME") LIKE UPPER('O''B%') ESCAPE '\'
            """.ReplaceLineEndings("\n"), view.Sql);
        Assert.Empty(view.Parameters);
        Assert.Equal(["ab", null, "nummer"], view.LeftOut.Select(l => l.Filter.Parameter));
        Assert.Contains("TO_DATE", Build(QueryDesignState.Empty.Update([], n => Columns(n, "NR") with
        {
            Filters = [new QueryFilter(FilterCondition.Of("DATUM", FilterOperator.Gte, "1.10.2026"))],
        }), DesignSqlMode.View).Sql, StringComparison.Ordinal);
        Assert.Equal(5, statement.Parameters.Count); // all of them in the statement: >= 1000, the day, both of the group, the name
        Assert.Empty(statement.LeftOut);
    }

    [Fact]
    public void Column_names_follow_the_path_or_the_name_given_and_stay_unique()
    {
        var state = QueryDesignState.Empty
            .Update([], n => n with { Columns = [new QueryColumn("NR"), new QueryColumn("ID", "KundeName")] })
            .Add([], Up(AuftragKunde)).Update([Up(AuftragKunde)], n => Columns(n, "NAME"));

        Assert.Equal(["NR", "KUNDE_NAME", "KUNDE_NAME_2"], Build(state).Columns.Select(c => c.Name));
    }
}
