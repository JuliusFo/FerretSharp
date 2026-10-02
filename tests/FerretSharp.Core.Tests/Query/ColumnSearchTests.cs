using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public class ColumnSearchTests
{
    private static readonly ColumnInfo[] Columns = [.. new[]
    {
        "VERTRAG_ID", "SORTE", "RECHNUNG_ORT", "ORT_ZUSATZ", "LIEFER_ORT", "ORT", "ENDE_AM", "KUENDIGUNG_AM", "Mixed_Case",
    }.Select((name, i) => new ColumnInfo(name, "VARCHAR2", 50, true, null, null, true, false, null, i))];

    private static IReadOnlyList<string> Find(string? query) =>
        ColumnSearch.Find(Columns, query).Select(i => Columns[i].Name).ToList();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_query_returns_all_columns_in_schema_order(string? query) =>
        Assert.Equal(Columns.Select(c => c.Name), Find(query));

    [Fact]
    public void Exact_name_first_then_prefix_then_word_start_then_contains() =>
        Assert.Equal(["ORT", "ORT_ZUSATZ", "RECHNUNG_ORT", "LIEFER_ORT", "SORTE"], Find("ort"));

    [Fact]
    public void Every_term_must_occur() =>
        Assert.Equal(["LIEFER_ORT"], Find("liefer ort"));

    [Fact]
    public void Terms_may_match_in_any_order() =>
        Assert.Equal(["LIEFER_ORT"], Find("ort liefer"));

    [Theory]
    [InlineData("lieferort", "LIEFER_ORT")]
    [InlineData("LIEFER_ORT", "LIEFER_ORT")]
    [InlineData("kuendigungam", "KUENDIGUNG_AM")]
    public void Underscores_may_be_left_out(string query, string expected) =>
        Assert.Equal(expected, Find(query)[0]);

    [Fact]
    public void Terms_separated_by_spaces_match_the_exact_name_with_underscores() =>
        Assert.Equal(["ENDE_AM"], Find("ende am"));

    [Fact]
    public void Case_is_ignored_also_for_quoted_names() =>
        Assert.Equal(["Mixed_Case"], Find("MIXED"));

    [Fact]
    public void No_match_returns_nothing() =>
        Assert.Empty(Find("xyz"));
}
