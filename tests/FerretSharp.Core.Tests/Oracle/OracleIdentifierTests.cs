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
