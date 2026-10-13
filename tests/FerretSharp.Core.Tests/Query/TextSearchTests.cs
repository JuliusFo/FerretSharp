using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

/// <summary>R5b (review 3.31.0, B12): a statement on one line, and its first word without comments, for labels.</summary>
public sealed class SqlLabelTests
{
    [Fact]
    public void One_line() => Assert.Equal("SELECT a FROM t WHERE x = 1", SqlScript.OneLine("  SELECT a\r\n  FROM t\n\tWHERE x =  1 "));

    [Theory]
    [InlineData("select 1 from dual", "SELECT")]
    [InlineData("-- Offene Aufträge\n\nSELECT a.ID FROM AUFTRAG a", "SELECT")] // TagWith()
    [InlineData("/* x */ update t set a = 1", "UPDATE")]
    [InlineData("  ", "")]
    public void First_word(string sql, string word) => Assert.Equal(word, SqlScript.FirstWord(sql));
}

/// <summary>R5b (review 3.31.0, B5): one rule for every search box – words in any order, case and accents ignored.</summary>
public sealed class TextSearchTests
{
    [Theory]
    [InlineData("", "KUNDE", true)]
    [InlineData("kunde", "KUNDE_ADRESSE", true)]
    [InlineData("kunde adr", "KUNDE_ADRESSE", true)]
    [InlineData("adr kunde", "KUNDE_ADRESSE", true)] // any order
    [InlineData("uber", "Über die Einstellungen", true)] // accents ignored
    [InlineData("MULLER", "Müller & Söhne", true)]
    [InlineData("kunde rechnung", "KUNDE_ADRESSE", false)] // every word
    public void Every_word_in_any_order_case_and_accents_ignored(string query, string text, bool matches) =>
        Assert.Equal(matches, new TextSearch(query).Matches(text));

    [Fact]
    public void A_word_may_be_in_any_of_the_texts()
    {
        var search = new TextSearch("erp prod");

        Assert.True(search.Matches("ERP", null, "Prod"));
        Assert.False(search.Matches("ERP", null, "Test"));
    }

    [Fact]
    public void Without_a_query_everything_matches()
    {
        Assert.False(TextSearch.None.IsActive);
        Assert.True(new TextSearch("   ").Matches("x"));
    }

    [Fact]
    public void The_comparisons_of_the_ranking_searches()
    {
        Assert.True(TextSearch.StartsWith("ÄNDERUNG_AM", "anderung"));
        Assert.True(TextSearch.Equal("kunde_id", "KUNDE_ID"));
        Assert.True(TextSearch.Contains("LIEFER_ORT", "ort"));
    }
}
