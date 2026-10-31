using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Tests.Connections;

namespace FerretSharp.Core.Tests.Oracle;

public class OracleIdentifierTests
{
    [Theory]
    [InlineData("KUNDEN", "\"KUNDEN\"")]
    [InlineData("MixedCase", "\"MixedCase\"")]
    [InlineData("raw col", "\"raw col\"")]
    public void Quote_keeps_the_name_exactly(string name, string expected)
    {
        Assert.Equal(expected, OracleIdentifier.Quote(name));
    }

    [Fact]
    public void Quote_rejects_embedded_double_quotes()
    {
        Assert.Throws<ArgumentException>(() => OracleIdentifier.Quote("bad\"name"));
    }

    /// <summary>Plain where Oracle reads the name as written – also in PL/SQL, whose reserved words (END, TYPE …) need quotes.</summary>
    [Theory]
    [InlineData("KUNDEN", "KUNDEN")]
    [InlineData("K$1#_X", "K$1#_X")]
    [InlineData("Kunden", "\"Kunden\"")]
    [InlineData("1A", "\"1A\"")]
    [InlineData("_A", "\"_A\"")]
    [InlineData("A B", "\"A B\"")]
    [InlineData("ÄNDERUNG", "\"ÄNDERUNG\"")]
    [InlineData("DATE", "\"DATE\"")]
    [InlineData("END", "\"END\"")]
    [InlineData("TYPE", "\"TYPE\"")]
    [InlineData("NAME", "NAME")]
    public void QuoteIfNeeded_quotes_only_what_oracle_would_misread(string name, string expected)
    {
        Assert.Equal(expected, OracleIdentifier.QuoteIfNeeded(name));
        Assert.Equal(expected == name, OracleIdentifier.IsPlain(name));
    }

    [Fact]
    public void QualifyIfNeeded_quotes_each_part_on_its_own()
    {
        Assert.Equal("APP.KUNDEN", OracleIdentifier.QualifyIfNeeded("APP", "KUNDEN"));
        Assert.Equal("APP.\"Kunden\"", OracleIdentifier.QualifyIfNeeded("APP", "Kunden"));
        Assert.Equal("\"app\".\"DATE\"", OracleIdentifier.QualifyIfNeeded("app", "DATE"));
        Assert.Equal("\"KUNDEN\n\"", OracleIdentifier.QuoteIfNeeded("KUNDEN\n")); // review of WP-70: $ matched before the line break
        Assert.True(OracleIdentifier.NeedsQuotes("KUNDEN\n"));
    }

    /// <summary>R5b (review 3.31.0, B3): one check for typed names – designer, indexes, constraints, audit templates.</summary>
    [Theory]
    [InlineData("KUNDE", null)]
    [InlineData("Kunde mit Leerzeichen", null)] // quoted exactly: fine
    [InlineData("A\"B", "A\"B: Ein Name darf keine doppelten Anführungszeichen enthalten.")]
    [InlineData("A\0B", "A\0B: Ein Name darf keine doppelten Anführungszeichen enthalten.")]
    public void Problem_names_what_keeps_a_name_from_being_created(string name, string? problem) =>
        Assert.Equal(problem, OracleIdentifier.Problem(name));

    [Fact]
    public void A_name_may_have_128_bytes_not_characters()
    {
        Assert.Null(OracleIdentifier.Problem(new string('A', 128)));
        Assert.Equal($"{new string('Ä', 65)}: Der Name ist länger als 128 Byte.", OracleIdentifier.Problem(new string('Ä', 65))); // 130 bytes
    }

    /// <summary>A warning for typed names: upper case of any script is plain to Oracle, a reserved word is not (R5b: DATE warned nothing in the designer).</summary>
    [Theory]
    [InlineData("KUNDE", false)]
    [InlineData("ÄNDERUNG", false)]
    [InlineData("K$1#_X", false)]
    [InlineData("Kunde", true)]
    [InlineData("1A", true)]
    [InlineData("A B", true)]
    [InlineData("DATE", true)]
    [InlineData("END", true)]
    public void NeedsQuotes_warns_where_oracle_needs_them(string name, bool needsQuotes) =>
        Assert.Equal(needsQuotes, OracleIdentifier.NeedsQuotes(name));

    [Theory]
    [InlineData("ABCDEF", 4, "ABCD")]
    [InlineData("ÄÖÜ", 5, "ÄÖ")] // 2 bytes each
    [InlineData("A😀B", 4, "A")] // never half of a surrogate pair
    [InlineData("A😀B", 5, "A😀")]
    [InlineData("KURZ", 128, "KURZ")]
    public void Cut_keeps_whole_characters_within_the_bytes(string text, int bytes, string expected) =>
        Assert.Equal(expected, OracleIdentifier.Cut(text, bytes));

    [Fact]
    public void Qualify_quotes_owner_and_name()
    {
        Assert.Equal("\"APP\".\"Kunden\"", OracleIdentifier.Qualify("APP", "Kunden"));
    }

    [Theory]
    [InlineData("erp", "ERP")]
    [InlineData("  Erp_App ", "ERP_APP")]
    [InlineData("\"Erp\"", "Erp")]
    public void Normalize_upper_cases_unquoted_names_only(string typed, string expected)
    {
        Assert.Equal(expected, OracleIdentifier.Normalize(typed));
    }

    [Fact]
    public void Effective_schema_uses_normalized_default_schema()
    {
        Assert.Equal("ERP_APP", (TestProfiles.HostPort() with { DefaultSchema = "erp_app" }).EffectiveSchema);
        Assert.Equal("Mixed", (TestProfiles.HostPort() with { DefaultSchema = "\"Mixed\"" }).EffectiveSchema);
    }
}
