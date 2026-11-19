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
            [new ColumnStatistics("KUNDE_ID", 3212, 0, new DateTime(2026, 11, 17), "NONE")],
            DateTimeOffset.Now);

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
}
