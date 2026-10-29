using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

public class BindValuesTests
{
    [Theory]
    [MemberData(nameof(Values))]
    public void Values_are_shown_as_literals(object? value, string expected) =>
        Assert.Equal(expected, BindValues.Format(new QueryParameter("p0", value), mask: false));

    public static TheoryData<object?, string> Values() => new()
    {
        { null, "NULL" },
        { "O'Brien", "'O''Brien'" },
        { 1234.5m, "1234.5" },
        { 500, "500" },
        { new DateTime(2026, 10, 1), "2026-10-01" },
        { new DateTime(2026, 10, 1, 13, 45, 7), "2026-10-01 13:45:07" },
        { new byte[] { 0xCA, 0xFE }, "HEXTORAW('CAFE')" },
    };

    [Fact]
    public void Prod_values_are_masked_but_names_stay()
    {
        var statement = new QuerySpec("SELECT * FROM t WHERE c = :p0", [new QueryParameter("p0", "geheim")]);

        var text = BindValues.Describe(statement, mask: true);

        Assert.DoesNotContain("geheim", text);
        Assert.EndsWith("-- :p0 = ‹maskiert›", text);
    }

    [Fact]
    public void Statement_lists_every_bind_variable_each_line_a_comment()
    {
        // Review of R8: a value with a line break left its rest as an executable line in "Copy as SQL".
        var statement = new QuerySpec("SELECT 1 FROM t WHERE a = :p0 AND b = :p1 AND c = :p2", [new("p0", "x"), new("p1", 2m), new("p2", "y\nDROP TABLE t;")]);

        Assert.Equal(
            ["SELECT 1 FROM t WHERE a = :p0 AND b = :p1 AND c = :p2", "-- :p0 = 'x'", "-- :p1 = 2", "-- :p2 = 'y", "-- DROP TABLE t;'"],
            BindValues.Describe(statement, mask: false).Split(Environment.NewLine));
    }

    [Theory]
    [InlineData("ORA-03113", true)]
    [InlineData("ORA-00028", true)]
    [InlineData("ORA-02396", true)]
    [InlineData("ORA-00942", false)]
    [InlineData(null, false)]
    public void Connection_loss_is_recognized_by_code(string? code, bool lost) =>
        Assert.Equal(lost, new DatabaseException("x", code).IsConnectionLost);
}
