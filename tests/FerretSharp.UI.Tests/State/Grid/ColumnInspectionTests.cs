using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Forms;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-92: what the column inspector shows of a column, from what the tab has loaded and what it read once.</summary>
public sealed class ColumnInspectionTests
{
    private static readonly TableSummary Auftrag = new("APP", "AUFTRAG", TableKind.Table);
    private static readonly TableRef Kunde = new("APP", "KUNDE");
    private static readonly TableRef Position = new("APP", "POSITION");

    private static readonly TableDetails Details = new(Auftrag,
        [Column("ID", 1), Column("KUNDE_ID", 2), Column("STATUS", 3)], ["ID"], [], false);

    private static ColumnInfo Column(string name, int position) => new(name, "NUMBER", null, false, 10, 0, true, false, null, position);

    private static ForeignKeyInfo Fk(string name, TableRef from, string fromColumn, TableRef to, string toColumn, FkSource source = FkSource.Declared) =>
        new(name, from, [fromColumn], to, [toColumn], source);

    private static IndexInfo Index(string name, params string[] columns) =>
        new("APP", name, "NORMAL", false, "VALID", [.. columns.Select(c => new IndexColumn(c, false, false))], "USERS", false, true);

    private static readonly ForeignKeyInfo ToKunde = Fk("FK_AUFTRAG_KUNDE", Auftrag.Ref, "KUNDE_ID", Kunde, "ID");
    private static readonly ForeignKeyInfo FromPosition = Fk("FK_POSITION_AUFTRAG", Position, "AUFTRAG_ID", Auftrag.Ref, "ID", FkSource.ClrModel);

    [Theory]
    [InlineData(0, true, "", "FK_POSITION_AUFTRAG")] // the key: referenced by the positions (from the C# model)
    [InlineData(1, false, "FK_AUFTRAG_KUNDE", "")] // a foreign key: points to the customer
    [InlineData(2, false, "", "")]
    public void A_column_shows_its_key_and_the_relationships_it_is_part_of(int column, bool primaryKey, string references, string referencedBy)
    {
        var inspection = ColumnInspection.Of(Details, column, [ToKunde], [FromPosition], data: null);

        Assert.Equal(primaryKey, inspection.PrimaryKey);
        Assert.Equal(references, string.Join(",", inspection.References.Select(f => f.Name)));
        Assert.Equal(referencedBy, string.Join(",", inspection.ReferencedBy.Select(f => f.Name)));
    }

    [Fact]
    public void Indexes_and_statistics_show_once_read_and_a_column_no_index_starts_with_is_marked()
    {
        var data = new ColumnInspectorData(
            [Index("PK_AUFTRAG", "ID"), Index("IX_AUFTRAG_SUCHE", "STATUS", "KUNDE_ID")],
            [new ColumnStatistics("KUNDE_ID", 3212, 0, new DateTime(2026, 11, 17), "NONE")]);

        var unread = ColumnInspection.Of(Details, 1, [], [], data: null);
        var kunde = ColumnInspection.Of(Details, 1, [], [], data);
        var key = ColumnInspection.Of(Details, 0, [], [], data);

        Assert.Equal((null, null, false), (unread.Indexes, unread.Statistics, unread.NoLeadingIndex)); // nothing known yet: no warning
        Assert.Equal([("IX_AUFTRAG_SUCHE", 2)], kunde.Indexes!.Select(i => (i.Index.Name, i.Position)));
        Assert.Equal(3212, kunde.Statistics?.Distinct);
        Assert.True(kunde.NoLeadingIndex);
        Assert.False(key.NoLeadingIndex);
        Assert.Null(key.Statistics); // not analyzed
    }

    /// <summary>Review WP-92: a view has no indexes or statistics of its own – no warning on every column.</summary>
    [Fact]
    public void A_view_shows_neither_indexes_nor_statistics_and_warns_about_none()
    {
        var view = Details with { Table = new TableSummary("APP", "V_AUFTRAG", TableKind.View) };

        var inspection = ColumnInspection.Of(view, 1, [], [], new ColumnInspectorData([], []));

        Assert.Equal((false, null, null, false), (inspection.OwnIndexes, inspection.Indexes, inspection.Statistics, inspection.NoLeadingIndex));
    }

    /// <summary>Review WP-92: a new structure (DDL in the designer) reads indexes and statistics again.</summary>
    [Fact]
    public void A_new_structure_forgets_what_the_inspector_read()
    {
        var tab = new TableTab(Guid.NewGuid(), Auftrag) { Inspection = new ColumnInspectorData([Index("IX_AUFTRAG_SUCHE", "STATUS")], []) };

        tab.UseStructure(Details);

        Assert.Null(tab.Inspection);
    }

    /// <summary>
    /// Review WP-92: "Filter by this value" takes the text that parses back to exactly the value, as a foreign key jump – none
    /// for a type without equality, none for a value not written yet; NULL as IS NULL.
    /// </summary>
    [Theory]
    [InlineData("NUMBER", "29", null, "Equals 29")]
    [InlineData("NUMBER", null, null, "IsNull ")]
    [InlineData("NUMBER", "29", ChangeStage.Pending, null)] // the database does not have it yet
    [InlineData("NUMBER", "29", ChangeStage.Flushed, "Equals 29")] // written in the workspace's transaction: its session sees it
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "+01 02:00:00", null, null)]
    [InlineData("CLOB", "text", null, null)]
    [InlineData("BINARY_DOUBLE", "1E+20", null, null)]
    public void Filtering_by_a_value_takes_its_exact_text_or_offers_nothing(string type, string? raw, ChangeStage? stage, string? expected)
    {
        var column = new ColumnInfo("WERT", type, null, false, null, null, true, false, null, 1);
        object? value = raw is null ? null : type == "NUMBER" ? decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) : raw;
        var field = new FormField(0, column, new ColumnLabel("WERT", null, null), new PresentedValue(raw), value, raw ?? "", stage, null, false, false, null, [], []);

        var filter = ColumnInspection.FilterOf(field);

        Assert.Equal(expected, filter is null ? null : $"{filter.Op} {filter.Value}");
        Assert.True(filter is null || filter.Column == "WERT");
    }
}
