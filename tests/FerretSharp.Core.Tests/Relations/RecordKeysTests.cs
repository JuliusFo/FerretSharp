using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.Relations.MemoryTables;

namespace FerretSharp.Core.Tests.Relations;

public class RecordKeysTests
{
    private static readonly TableDetails Mixed = Table("BELEG", ["NR", "TAG", "ART", "HASH"],
        Col("NR", nullable: false), Col("TAG", "DATE", nullable: false), Col("ART", "CHAR", nullable: false, length: 3), Col("HASH", "RAW", nullable: false, length: 16))
        with { PrimaryKeyEnforced = true };

    [Fact]
    public void A_primary_key_goes_to_text_and_back_to_the_same_values()
    {
        RowKey key = new RowKey.PrimaryKey([4711.00m, new DateTime(2026, 10, 23, 14, 5, 9), "AB ", new byte[] { 0xCA, 0xFE }]);

        var text = RecordKeys.ToText(Mixed, key);

        Assert.Equal(["4711.00", "2026-10-23 14:05:09", "AB", "CAFE"], text);
        var back = Assert.IsType<RowKey.PrimaryKey>(RecordKeys.FromText(Mixed, rowId: false, text!));
        Assert.Equal(4711m, back.Values[0]);
        Assert.Equal(new DateTime(2026, 10, 23, 14, 5, 9), back.Values[1]);
        Assert.Equal("AB", back.Values[2]); // bound as CHAR, compared blank-padded
        Assert.Equal(new byte[] { 0xCA, 0xFE }, back.Values[3]);
    }

    [Fact]
    public void A_rowid_stays_as_it_is_while_the_table_has_no_enforced_primary_key()
    {
        var table = Table("PROTOKOLL", [], Col("TEXT", "VARCHAR2"));

        Assert.Equal(["AAAR3sAAEAAAACXAAA"], RecordKeys.ToText(table, new RowKey.RowId("AAAR3sAAEAAAACXAAA")));
        Assert.Equal(new RowKey.RowId("AAAR3sAAEAAAACXAAA"), RecordKeys.FromText(table, rowId: true, ["AAAR3sAAEAAAACXAAA"]));
        Assert.Null(RecordKeys.FromText(Mixed, rowId: true, ["AAAR3sAAEAAAACXAAA"])); // a primary key since
    }

    [Fact]
    public void A_key_that_no_longer_fits_the_table_is_dropped()
    {
        Assert.Null(RecordKeys.FromText(Mixed, rowId: false, ["1", "2026-10-23"])); // columns missing
        Assert.Null(RecordKeys.FromText(Mixed, rowId: false, ["x", "2026-10-23", "AB", "CAFE"])); // not a number
        Assert.Null(RecordKeys.ToText(Table("MESSWERT", ["WERT"], Col("WERT", "BINARY_DOUBLE", nullable: false)) with { PrimaryKeyEnforced = true },
            new RowKey.PrimaryKey([1.5d])));
    }
}
