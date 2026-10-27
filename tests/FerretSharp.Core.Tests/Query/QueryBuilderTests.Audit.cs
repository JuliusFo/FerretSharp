using FerretSharp.Core.Audit;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

/// <summary>Audit rows against the version before (WP-63).</summary>
public partial class QueryBuilderTests
{
    /// <summary>An audit table of the built-in template: its own columns, then copies of KUNDEN's; no key of its own.</summary>
    private static readonly TableDetails AuditKunden = new(
        new TableSummary("APP", "AUDIT_KUNDEN", TableKind.Table),
        [
            Col("AUDIT_ID", "NUMBER", nullable: false),
            Col("AUDIT_USER", "VARCHAR2", length: 64),
            Col("AUDIT_TRANS", "CHAR", length: 1),
            Col("AUDIT_TIME", "DATE"),
            Col("AUDIT_SID", "NUMBER"),
            Col("KUNDE_ID", "NUMBER", precision: 10, scale: 0),
            Col("NAME", "VARCHAR2", length: 80),
            Col("NOTIZ", "CLOB"),
            Col("GEBURT", "DATE"),
        ],
        [],
        [],
        false);

    private static QuerySpec AuditChanges(TableDetails table, AuditVersions versions, IReadOnlyList<IReadOnlyList<object>> baseKeys, IReadOnlyList<IReadOnlyList<object>> rowKeys)
    {
        var query = QueryBuilder.BuildAuditChanges(table, versions, baseKeys, rowKeys);
        AssertBindsMatch(query.Sql, query.Parameters);
        Assert.True(OracleSession.IsReadOnlyStatement(query.Sql));
        return query;
    }

    [Fact]
    public void The_audit_view_compares_the_copies_of_comparable_base_columns_by_the_templates_order()
    {
        var view = AuditView.Of(AuditKunden, new TableRef("APP", "KUNDEN"), ["KUNDE_ID"], AuditTemplates.Default);

        Assert.NotNull(view);
        Assert.Equal(new TableRef("APP", "KUNDEN"), view.BaseTable);
        Assert.Equal([5], view.Versions!.KeyColumns);
        Assert.Equal(0, view.Versions.OrderColumn);
        Assert.Equal([6, 8], view.Versions.Compared); // NAME, GEBURT – not the key, the CLOB or the template's own columns
        Assert.Equal(2, view.ActionColumn);
        Assert.Equal(AuditActionKind.Delete, view.ActionOf([1m, "x", "D", null, 1m, 4711m, "A", null, null]));
    }

    [Fact]
    public void Without_version_order_or_base_key_only_the_action_is_shown()
    {
        var withoutOrder = AuditTemplates.Parse(AuditTemplates.DefaultText.ReplaceLineEndings("\n").Replace("-- @version-order AUDIT_ID\n", "", StringComparison.Ordinal));

        Assert.Null(withoutOrder.VersionOrder);
        Assert.Equal(new AuditView(new TableRef("APP", "KUNDEN"), null, 2, AuditTemplates.Default.Action),
            AuditView.Of(AuditKunden, new TableRef("APP", "KUNDEN"), ["KUNDE_ID"], withoutOrder));
        Assert.Null(AuditView.Of(AuditKunden, new TableRef("APP", "KUNDEN"), [], AuditTemplates.Default)!.Versions);
        Assert.Null(AuditView.Of(AuditKunden, new TableRef("APP", "KUNDEN"), ["GONE_ID"], AuditTemplates.Default)!.Versions);
    }

    [Fact]
    public void A_template_with_neither_directive_shows_nothing() =>
        Assert.Null(AuditView.Of(AuditKunden, new TableRef("APP", "KUNDEN"), ["KUNDE_ID"],
            AuditTemplates.Parse("-- @audit-table AUDIT_{table}\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX")));

    [Fact]
    public void Audit_changes_number_the_versions_of_the_base_keys_before_picking_the_rows()
    {
        var query = AuditChanges(AuditKunden, new AuditVersions([5], 0, [6, 8]), [[4711m]], [["AAAR3sAAEAAAACXAAA"]]);

        const string window = "OVER (PARTITION BY t.\"KUNDE_ID\" ORDER BY t.\"AUDIT_ID\", t.ROWID)";
        Assert.Equal(
            "SELECT v.FS_RID, v.FS_N, v.FS_CHANGED\n"
            + "  FROM (SELECT t.ROWID AS FS_RID,\n"
            + $"               ROW_NUMBER() {window} AS FS_N,\n"
            + $"               DECODE(t.\"NAME\", LAG(t.\"NAME\") {window}, '0', '1')\n"
            + $"                 || DECODE(t.\"GEBURT\", LAG(t.\"GEBURT\") {window}, '0', '1') AS FS_CHANGED\n"
            + "          FROM \"APP\".\"AUDIT_KUNDEN\" t\n"
            + "         WHERE t.\"KUNDE_ID\" IN (:k0)) v\n"
            + " WHERE v.FS_RID IN (:r0)",
            query.Sql);
        Assert.Equal([4711m, "AAAR3sAAEAAAACXAAA"], query.Parameters.Select(p => p.Value));
    }

    [Fact]
    public void An_audit_table_with_a_key_of_its_own_is_picked_by_it()
    {
        var keyed = AuditKunden with { PrimaryKey = ["AUDIT_ID"] };

        var query = AuditChanges(keyed, new AuditVersions([5], 0, [6]), [[4711m], [4712m]], [[1m], [2m]]);

        Assert.StartsWith("SELECT v.\"AUDIT_ID\", v.FS_N, v.FS_CHANGED\n  FROM (SELECT t.\"AUDIT_ID\",\n", query.Sql);
        Assert.EndsWith(") v\n WHERE v.\"AUDIT_ID\" IN (:r0, :r1, :r2, :r3, :r4, :r5, :r6, :r7, :r8, :r9)", query.Sql);
    }

    [Fact]
    public void Audit_changes_need_keys_and_a_column_to_compare()
    {
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildAuditChanges(AuditKunden, new AuditVersions([5], 0, []), [[1m]], [["A"]]));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildAuditChanges(AuditKunden, new AuditVersions([5], 0, [6]), [], [["A"]]));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildAuditChanges(AuditKunden, new AuditVersions([5], 0, [6]), [[1m]],
            [.. Enumerable.Range(0, 501).Select(i => (IReadOnlyList<object>)[$"R{i}"])]));
    }
}
