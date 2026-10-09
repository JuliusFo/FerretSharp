using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>Texts of Query, Data and Forms that are composed from several resources or pick a plural form (WP-29).</summary>
public class EnglishTextTests
{
    private static ColumnInfo Col(string name, string type, bool identity = false) =>
        new(name, type, null, true, null, null, true, identity, null, 0);

    private static readonly TableDetails Table = new(
        new TableSummary("APP", "CUSTOMERS", TableKind.Table),
        [Col("ID", "NUMBER", identity: true), Col("NOTE", "CLOB"), Col("PICTURE", "BLOB")],
        ["ID"], [], false);

    private static RowData Row(params object?[] values) => new(RowKey.None.Instance, values);

    [Fact]
    public void Export_warnings_and_insert_comments_pick_the_plural_form()
    {
        using (UiCulture.Use("en"))
        {
            var rows = new[] { Row(1m, new LobValue("x", 5000), new LobValue(null, 1)), Row(2m, null, new LobValue(null, 2048)) };

            var insert = InsertExport.Build(Table, rows);

            Assert.Equal(
                ["NOTE (CLOB): 1 value not exported – only the preview was loaded.", "PICTURE (BLOB): 2 values not exported – only the length was loaded."],
                insert.Warnings.Order(StringComparer.Ordinal));
            var lines = insert.Text.Split("\r\n");
            Assert.Equal("-- 2 rows from \"APP\".\"CUSTOMERS\" (FerretSharp export)", lines[0]);
            Assert.Equal("-- ID is an identity column: GENERATED ALWAYS rejects explicit values (ORA-32795).", lines[1]);
            Assert.Contains("NULL /* CLOB (5.000 characters) not exported */", insert.Text);
            Assert.Contains("NULL /* BLOB (1 byte) not exported */", insert.Text);
        }
    }

    [Fact]
    public void Statement_writes_count_their_rows()
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal("UPDATE ORDERS · 1 row", new WriteAction(Guid.NewGuid(), WriteActionKind.Statement, "UPDATE ORDERS", 1, default).Display);
            Assert.Equal("UPDATE ORDERS · 1.234 rows", new WriteAction(Guid.NewGuid(), WriteActionKind.Statement, "UPDATE ORDERS", 1234, default).Display);
        }

        Assert.Equal("UPDATE ORDERS · 1.234 Zeilen", new WriteAction(Guid.NewGuid(), WriteActionKind.Statement, "UPDATE ORDERS", 1234, default).Display);
    }

    [Fact]
    public void Empty_text_hint_names_the_operator_in_the_UI_language()
    {
        var filter = FilterCondition.Of("NAME", FilterOperator.Equals, "");
        var name = Col("NAME", "VARCHAR2");

        using (UiCulture.Use("en"))
        {
            Assert.Equal("Empty text is NULL in Oracle – use “is NULL”.", FilterRules.Validate(name, filter));
        }

        Assert.Equal("Leerer Text ist in Oracle NULL – „ist NULL“ verwenden.", FilterRules.Validate(name, filter));
    }

    [Fact]
    public void Lengths_keep_the_German_number_notation()
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal("12.345 characters", LobContent.Describe(new string('x', 12345)));
            Assert.Equal("1 character", LobContent.CharacterCount(1));
            Assert.Equal("512 bytes", LobContent.Describe(new byte[512]));
        }
    }
}
