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
