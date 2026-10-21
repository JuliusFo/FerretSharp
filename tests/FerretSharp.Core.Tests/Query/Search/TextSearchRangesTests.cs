using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

/// <summary>WP-38: where the words of a query are, to highlight hits in the quick open.</summary>
public sealed class TextSearchRangesTests
{
    private static string Marked(string query, string text)
    {
        var result = text;
        foreach (var range in new TextSearch(query).Ranges(text).Reverse())
        {
            result = result[..range.Start] + "[" + result[range.Start..range.End] + "]" + result[range.End..];
        }

        return result;
    }

    [Theory]
    [InlineData("", "KUNDEN", "KUNDEN")]
    [InlineData("kunde", "V_KUNDEN_KUNDE", "V_[KUNDE]N_[KUNDE]")] // every occurrence
    [InlineData("adr kunde", "KUNDEN_ADRESSEN", "[KUNDE]N_[ADR]ESSEN")] // every word, sorted
    [InlineData("kun unde", "KUNDEN", "[KUNDE]N")] // overlapping merged
    [InlineData("uber", "Über die Einstellungen", "[Über] die Einstellungen")] // accents ignored
    [InlineData("tab schl", "Tab schließen", "[Tab] [schl]ießen")]
    [InlineData("lieferort", "LIEFER_ORT", "LIEFER_ORT")] // only found without the underscore: not marked
    public void Every_occurrence_of_every_word(string query, string text, string marked) =>
        Assert.Equal(marked, Marked(query, text));

    [Fact]
    public void Nothing_in_no_text() => Assert.Empty(new TextSearch("kunde").Ranges(null));
}
