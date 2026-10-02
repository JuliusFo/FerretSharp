using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

/// <summary>RAW columns (e.g. GUIDs as RAW(16)) can be compared by hex value, so FK navigation works over them.</summary>
public class RawFilterTests
{
    private static readonly ColumnInfo Guid = new("GUID", "RAW", 16, false, null, null, true, false, null, 0);

    private static readonly TableDetails Table = new(new TableSummary("APP", "T", TableKind.Table), [Guid], [], [], false);

    [Fact]
    public void Raw_offers_equality_and_null_checks()
    {
        Assert.Equal(
            [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.In, FilterOperator.IsNull, FilterOperator.IsNotNull],
            FilterRules.OperatorsFor(ColumnCategory.Raw));
    }

    [Theory]
    [InlineData("CAFE01", new byte[] { 0xCA, 0xFE, 0x01 })]
    [InlineData("0xcafe01", new byte[] { 0xCA, 0xFE, 0x01 })]
    [InlineData(" 00ff ", new byte[] { 0x00, 0xFF })]
    public void Hex_values_are_parsed(string text, byte[] expected)
    {
        Assert.True(FilterRules.TryParseHex(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("CAF")]
    [InlineData("XYZ1")]
    [InlineData("0x")]
    public void Invalid_hex_is_a_validation_error(string text) =>
        Assert.Equal($"„{text}“ ist kein Hex-Wert (z. B. CAFE01).", FilterRules.Validate(Guid, FilterCondition.Of("GUID", FilterOperator.Equals, text)));

    [Fact]
    public void Raw_values_are_bound_as_bytes()
    {
        var query = QueryBuilder.BuildCount(Table, [FilterCondition.Of("GUID", FilterOperator.In, "CAFE", "BEEF")]);

        Assert.Contains("t.\"GUID\" IN (:p0, :p1)", query.Sql);
        Assert.All(query.Parameters, p => Assert.Equal(OracleTypeHint.Raw, p.Type));
        Assert.Equal(new byte[] { 0xCA, 0xFE }, query.Parameters[0].Value);
    }
}
