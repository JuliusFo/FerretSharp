using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Tests.Oracle;

public class OracleConnectionTesterTests
{
    [Theory]
    [InlineData("ORA-01017: invalid credential or not authorized; logon denied", "invalid credential or not authorized; logon denied")]
    [InlineData("ORA-12514: Cannot connect\nhttps://docs.oracle.com/error-help", "Cannot connect")]
    [InlineData("Plain message", "Plain message")]
    public void Clean_message_strips_code_prefix_and_extra_lines(string raw, string expected)
    {
        Assert.Equal(expected, OracleConnectionTester.CleanMessage(raw));
    }
}
