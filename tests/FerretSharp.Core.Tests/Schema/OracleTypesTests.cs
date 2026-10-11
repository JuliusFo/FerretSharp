using System.Globalization;
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

    /// <summary>
    /// Each row is a case of the WP-23 experiment against Oracle 23 with a value in the column (empty columns take every
    /// change of the same kind except to or from a LOB).
    /// </summary>
    [Theory]
    [InlineData("VARCHAR2(20 BYTE)", "VARCHAR2(20 BYTE)", ColumnTypeChange.Same)]
    [InlineData("VARCHAR2(20 BYTE)", "VARCHAR2(50 BYTE)", ColumnTypeChange.Free)]
    [InlineData("VARCHAR2(20 BYTE)", "VARCHAR2(20 CHAR)", ColumnTypeChange.Free)]
    [InlineData("VARCHAR2(20 BYTE)", "NVARCHAR2(20)", ColumnTypeChange.Free)]
    [InlineData("VARCHAR2(20 BYTE)", "CHAR(20 BYTE)", ColumnTypeChange.Free)]
    [InlineData("CHAR(1 BYTE)", "VARCHAR2(1 BYTE)", ColumnTypeChange.Free)]
    [InlineData("VARCHAR2(20 BYTE)", "VARCHAR2(10 BYTE)", ColumnTypeChange.MayFailOnValues)] // ORA-01441 only if a value is longer
    [InlineData("VARCHAR2(20 CHAR)", "VARCHAR2(20 BYTE)", ColumnTypeChange.MayFailOnValues)]
    [InlineData("RAW(16)", "RAW(8)", ColumnTypeChange.MayFailOnValues)]
    [InlineData("NVARCHAR2(20)", "VARCHAR2(20 BYTE)", ColumnTypeChange.NeedsEmptyColumn)] // ORA-01439
    [InlineData("VARCHAR2(20 BYTE)", "NUMBER(10)", ColumnTypeChange.NeedsEmptyColumn)]
    [InlineData("NUMBER(10)", "VARCHAR2(20 BYTE)", ColumnTypeChange.NeedsEmptyColumn)]
    [InlineData("VARCHAR2(20 BYTE)", "CLOB", ColumnTypeChange.NotPossible)] // ORA-22858, also when empty
    [InlineData("CLOB", "VARCHAR2(4000 BYTE)", ColumnTypeChange.NotPossible)]
    [InlineData("NUMBER(10)", "NUMBER(12)", ColumnTypeChange.Free)]
    [InlineData("NUMBER(10)", "NUMBER(12,2)", ColumnTypeChange.Free)]
    [InlineData("NUMBER(12,2)", "NUMBER(14,4)", ColumnTypeChange.Free)]
    [InlineData("NUMBER(10)", "NUMBER", ColumnTypeChange.Free)]
    [InlineData("NUMBER(10)", "FLOAT", ColumnTypeChange.Free)]
    [InlineData("NUMBER(10)", "NUMBER(6)", ColumnTypeChange.NeedsEmptyColumn)] // ORA-01440
    [InlineData("NUMBER(10)", "NUMBER(10,2)", ColumnTypeChange.NeedsEmptyColumn)] // fewer integer digits
    [InlineData("NUMBER(12,2)", "NUMBER(12,3)", ColumnTypeChange.NeedsEmptyColumn)]
    [InlineData("NUMBER(12,2)", "NUMBER(12)", ColumnTypeChange.NeedsEmptyColumn)]
    [InlineData("NUMBER(12,2)", "INTEGER", ColumnTypeChange.NeedsEmptyColumn)]
    [InlineData("FLOAT", "NUMBER(10,2)", ColumnTypeChange.NeedsEmptyColumn)]
    [InlineData("DATE", "TIMESTAMP(6)", ColumnTypeChange.Free)]
    [InlineData("DATE", "TIMESTAMP(3)", ColumnTypeChange.Free)]
    [InlineData("TIMESTAMP(6)", "DATE", ColumnTypeChange.Free)]
    [InlineData("TIMESTAMP(3)", "TIMESTAMP(6)", ColumnTypeChange.Free)]
    [InlineData("TIMESTAMP(6)", "TIMESTAMP(3)", ColumnTypeChange.NeedsEmptyColumn)] // ORA-30082
    [InlineData("TIMESTAMP(6)", "TIMESTAMP(6) WITH TIME ZONE", ColumnTypeChange.NeedsEmptyColumn)] // ORA-01439
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "TIMESTAMP(6)", ColumnTypeChange.NeedsEmptyColumn)]
    public void Type_changes_as_oracle_takes_them_with_data(string from, string to, ColumnTypeChange change) =>
        Assert.Equal(change, OracleTypes.ChangeOf(Parse(from), Parse(to)));

    /// <summary><c>VARCHAR2(20 BYTE)</c>, <c>NUMBER(12,2)</c>, <c>INTEGER</c>, <c>FLOAT</c>, <c>TIMESTAMP(6) WITH TIME ZONE</c> as the dictionary reports them.</summary>
    private static ColumnInfo Parse(string type)
    {
        if (type.StartsWith("TIMESTAMP", StringComparison.Ordinal) || type is "DATE" or "CLOB")
        {
            return Column(type);
        }

        switch (type)
        {
            case "INTEGER":
                return Column("NUMBER", scale: 0);
            case "FLOAT":
                return Column("FLOAT", precision: 126);
            case "NUMBER":
                return Column("NUMBER");
        }

        var open = type.IndexOf('(', StringComparison.Ordinal);
        var name = type[..open];
        var facets = type[(open + 1)..^1];
        if (name == "NUMBER")
        {
            var parts = facets.Split(',');
            return Column(name, precision: int.Parse(parts[0], CultureInfo.InvariantCulture), scale: parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0);
        }

        var words = facets.Split(' ');
        return Column(name, int.Parse(words[0], CultureInfo.InvariantCulture), charSemantics: name is "NVARCHAR2" or "NCHAR" || words.Length > 1 && words[1] == "CHAR");
    }
}
