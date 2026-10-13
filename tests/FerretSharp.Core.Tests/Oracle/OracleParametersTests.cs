using System.Globalization;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using Oracle.ManagedDataAccess.Types;

namespace FerretSharp.Core.Tests.Oracle;

public class OracleParametersTests
{
    /// <summary>Review 3.31.0, A16: a key beyond 28 digits was bound as the record itself, which ODP.NET cannot convert.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void A_big_number_is_bound_exactly_whatever_the_culture(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var value = OracleParameters.ToOracle(new BigNumber("123456789012345678901234567890.25"));

            Assert.Equal(OracleDecimal.Parse("12345678901234567890123456789025"), value * new OracleDecimal(100));
            Assert.Equal(OracleDecimal.Parse("123456789012345678901234567890"), OracleParameters.ToOracle(new BigNumber("123456789012345678901234567890")));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
