using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using NSubstitute;

namespace FerretSharp.Core.Tests.Query;

public sealed class SqlBindsTests
{
    [Fact]
    public void Variables_become_typed_parameters_for_each_bind()
    {
        var spec = SqlBinds.Bind("SELECT * FROM t WHERE a = :id AND b = :Name AND c = :von AND d = :x AND e = :k AND f = :leer", ["id", "Name", "von", "x", "k", "leer"],
        [
            new SqlVariable("ID", SqlVariableType.Number, "1.234,5"),
            new SqlVariable("name", SqlVariableType.Text, "Meier"),
            new SqlVariable("von", SqlVariableType.Date, "05.10.2026 14:02"),
            new SqlVariable("x", SqlVariableType.Raw, "0xCAFE"),
            new SqlVariable("k", SqlVariableType.Char, "AB"),
            new SqlVariable("leer", SqlVariableType.Null, "egal"),
        ]);

        Assert.Equal(
            [
                new QueryParameter("id", 1234.5m, OracleTypeHint.Number),
                new QueryParameter("Name", "Meier", OracleTypeHint.Varchar2),
                new QueryParameter("von", new DateTime(2026, 10, 5, 14, 2, 0), OracleTypeHint.Date),
                new QueryParameter("k", "AB", OracleTypeHint.Char),
                new QueryParameter("leer", null, OracleTypeHint.Varchar2),
            ],
            spec.Parameters.Where(p => p.Name != "x"));
        Assert.Equal(new byte[] { 0xCA, 0xFE }, spec.Parameters.Single(p => p.Name == "x").Value);
    }

    /// <summary>
    /// R5b (review 3.31.0, rest of A18): a variable compared with an NVARCHAR2 or NCHAR column binds nationally, as the
    /// filter does – in the database character set, characters it lacks became "?" and found nothing.
    /// </summary>
    [Fact]
    public void National_variables_bind_in_the_national_character_set()
    {
        var spec = SqlBinds.Bind("x", ["t", "c"],
        [
            new SqlVariable("t", SqlVariableType.Text, "Łódź") { National = true },
            new SqlVariable("c", SqlVariableType.Char, "ŁD") { National = true },
        ]);

        Assert.Equal([OracleTypeHint.NVarchar2, OracleTypeHint.NChar], spec.Parameters.Select(p => p.Type));
    }

    [Theory]
    [InlineData(SqlVariableType.Number, "abc", ":id: „abc“ ist keine Zahl.")]
    [InlineData(SqlVariableType.Number, "", ":id: Zahl fehlt (für NULL den Typ NULL wählen).")]
    [InlineData(SqlVariableType.Date, "31.02.2026", ":id: „31.02.2026“ ist kein Datum (TT.MM.JJJJ [hh:mm[:ss]] oder ISO).")]
    [InlineData(SqlVariableType.Raw, "XYZ", ":id: „XYZ“ ist kein Hex-Wert (z. B. CAFE01).")]
    public void Values_that_do_not_fit_say_so(SqlVariableType type, string value, string message) =>
        Assert.Equal(message, Assert.Throws<SqlBindException>(() => SqlBinds.Bind("x", ["id"], [new SqlVariable("id", type, value)])).Message);

    [Fact]
    public void A_bind_without_variable_is_an_error() =>
        Assert.Equal("Für :id fehlt ein Wert.", Assert.Throws<SqlBindException>(() => SqlBinds.Bind("x", ["id"], [])).Message);

    [Fact]
    public void Reconcile_keeps_values_types_new_ones_and_unused_ones_at_the_end()
    {
        var existing = new[] { new SqlVariable("alt", SqlVariableType.Number, "1"), new SqlVariable("ID", SqlVariableType.Number, "4711") };

        var variables = SqlBinds.Reconcile(["id", "von"], existing, bind => bind == "von" ? new SqlVariable(bind, SqlVariableType.Date, "") : null);

        Assert.Equal(
            [
                new SqlVariable("ID", SqlVariableType.Number, "4711"),
                new SqlVariable("von", SqlVariableType.Date, ""),
                new SqlVariable("alt", SqlVariableType.Number, "1"),
            ],
            variables);
    }

    [Fact]
    public void Generated_parameters_become_variables_that_bind_the_same_values()
    {
        QueryParameter[] parameters =
        [
            new("p_0", 4711m, OracleTypeHint.Number),
            new("p_1", new DateTime(2026, 10, 5, 0, 0, 0), OracleTypeHint.Date),
            new("p_2", new DateTime(2026, 10, 5, 14, 2, 13, 500), OracleTypeHint.TimeStamp),
            new("p_3", "AB", OracleTypeHint.Char),
            new("p_4", new byte[] { 1, 2 }, OracleTypeHint.Raw),
            new("p_5", "%meier%", OracleTypeHint.Varchar2),
            new("p_6", new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(2)), OracleTypeHint.TimeStampTZ),
            new("p_7", new DateTimeOffset(2026, 10, 8, 12, 0, 0, 250, TimeSpan.FromHours(-5.5)), OracleTypeHint.TimeStampTZ),
            new("p_8", "Łódź", OracleTypeHint.NVarchar2), // R5b: national stays national (A18)
            new("p_9", "ŁD", OracleTypeHint.NChar),
        ];

        var variables = parameters.Select(SqlBinds.FromParameter).ToList();
        var bound = SqlBinds.Bind("x", parameters.Select(p => p.Name).ToList(), variables).Parameters;

        Assert.Equal(parameters.Select(p => (p.Name, p.Value is byte[] b ? Convert.ToHexString(b) : p.Value, p.Type)),
            bound.Select(p => (p.Name, p.Value is byte[] b ? Convert.ToHexString(b) : p.Value, p.Type)));
    }
}
