using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Schema;

public sealed class OracleTypesTests
{
    private static ColumnInfo Column(string type, int? length = null, bool charSemantics = false, int? precision = null, int? scale = null) =>
        new("C", type, length, charSemantics, precision, scale, true, false, null, 1);

    /// <summary>Display and DDL differ only in BYTE: shown it is the usual default, in DDL it is always stated.</summary>
    [Theory]
    [InlineData("VARCHAR2", 50, true, null, null, "VARCHAR2(50 CHAR)", "VARCHAR2(50 CHAR)")]
    [InlineData("VARCHAR2", 50, false, null, null, "VARCHAR2(50)", "VARCHAR2(50 BYTE)")]
    [InlineData("CHAR", 3, false, null, null, "CHAR(3)", "CHAR(3 BYTE)")]
    [InlineData("NVARCHAR2", 20, true, null, null, "NVARCHAR2(20)", "NVARCHAR2(20)")]
    [InlineData("RAW", 16, false, null, null, "RAW(16)", "RAW(16)")]
    [InlineData("NUMBER", null, false, 12, 2, "NUMBER(12,2)", "NUMBER(12,2)")]
    [InlineData("NUMBER", null, false, 10, 0, "NUMBER(10)", "NUMBER(10)")]
    [InlineData("NUMBER", null, false, null, 0, "INTEGER", "INTEGER")]
    [InlineData("NUMBER", null, false, null, 2, "NUMBER(*,2)", "NUMBER(*,2)")]
    [InlineData("NUMBER", null, false, null, null, "NUMBER", "NUMBER")]
    [InlineData("FLOAT", null, false, 126, null, "FLOAT(126)", "FLOAT(126)")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", null, false, null, 6, "TIMESTAMP(6) WITH TIME ZONE", "TIMESTAMP(6) WITH TIME ZONE")]
    public void Display_and_ddl_type(string type, int? length, bool charSemantics, int? precision, int? scale, string display, string ddl)
    {
        var column = Column(type, length, charSemantics, precision, scale);

        Assert.Equal(display, OracleTypes.DisplayType(column));
        Assert.Equal(display, column.DisplayType);
        Assert.Equal(ddl, OracleTypes.DdlType(column));
    }

    [Theory]
    [InlineData("VARCHAR2", false, true)]
    [InlineData("VARCHAR2", true, false)]
    [InlineData("CHAR", false, true)]
    [InlineData("NVARCHAR2", false, false)] // national types always count characters
    [InlineData("RAW", false, true)]
    public void Length_in_bytes(string type, bool charSemantics, bool bytes) =>
        Assert.Equal(bytes, OracleTypes.LengthInBytes(Column(type, 10, charSemantics)));

    [Theory]
    [InlineData("VARCHAR2", "NCHAR", true)]
    [InlineData("NUMBER", "FLOAT", true)]
    [InlineData("DATE", "TIMESTAMP(6)", true)]
    [InlineData("VARCHAR2", "CLOB", false)]
    [InlineData("NUMBER", "VARCHAR2", false)]
    public void Families_that_modify_can_convert_between(string from, string to, bool same) =>
        Assert.Equal(same, OracleTypes.Family(from) == OracleTypes.Family(to));
}
