using FerretSharp.Core.Schema;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Schema;

public class ColumnInfoTests
{
    private static ColumnInfo Column(string type, int? length = null, bool charSemantics = false, int? precision = null, int? scale = null) =>
        new("C", type, length, charSemantics, precision, scale, true, false, null, 1);

    [Theory]
    [InlineData("VARCHAR2", 50, true, null, null, "VARCHAR2(50 CHAR)")]
    [InlineData("VARCHAR2", 50, false, null, null, "VARCHAR2(50)")]
    [InlineData("NVARCHAR2", 20, true, null, null, "NVARCHAR2(20)")]
    [InlineData("RAW", 16, false, null, null, "RAW(16)")]
    [InlineData("NUMBER", null, false, 10, 0, "NUMBER(10)")]
    [InlineData("NUMBER", null, false, 12, 2, "NUMBER(12,2)")]
    [InlineData("NUMBER", null, false, null, 0, "INTEGER")]
    [InlineData("NUMBER", null, false, null, null, "NUMBER")]
    [InlineData("NUMBER", null, false, null, 2, "NUMBER(*,2)")]
    [InlineData("TIMESTAMP(6)", null, false, null, 6, "TIMESTAMP(6)")]
    [InlineData("DATE", null, false, null, null, "DATE")]
    public void Display_type_matches_ddl_notation(string type, int? length, bool charSemantics, int? precision, int? scale, string expected)
    {
        Assert.Equal(expected, Column(type, length, charSemantics, precision, scale).DisplayType);
    }
}
