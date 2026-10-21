using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public class ColumnSearchAlternateNameTests
{
    private static readonly ColumnInfo[] Columns = [.. new[] { "KUNDE_ID", "NAME", "ANGELEGT", "KZ_GESP" }
        .Select((name, i) => new ColumnInfo(name, "VARCHAR2", 50, true, null, null, true, false, null, i))];

    private static readonly string?[] Properties = ["KundeId", "Name", "ErstelltAm", "Gesperrt"];

    private static IReadOnlyList<string> Find(string query) =>
        ColumnSearch.Find(Columns, query, i => Properties[i]).Select(i => Columns[i].Name).ToList();

    [Fact]
    public void A_property_name_finds_its_column() => Assert.Equal(["KZ_GESP"], Find("gesperrt"));

    [Fact]
    public void C_sharp_word_boundaries_count_as_word_starts() => Assert.Equal(["ANGELEGT", "NAME"], Find("am"));

    [Fact]
    public void The_better_match_of_both_names_ranks_the_column()
    {
        // "gesp" is a prefix of the property "Gesperrt" and only contained in KZ_GESP.
        Assert.Equal(["KZ_GESP"], Find("gesp"));
        Assert.Equal(["KUNDE_ID"], Find("kundeid"));
    }

    [Fact]
    public void Without_alternate_names_only_column_names_count() =>
        Assert.Empty(ColumnSearch.Find(Columns, "gesperrt"));
}
