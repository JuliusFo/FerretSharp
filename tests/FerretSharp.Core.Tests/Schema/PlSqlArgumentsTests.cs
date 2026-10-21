using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Schema;

public class PlSqlArgumentsTests
{
    private static ArgumentRow Row(
        string subprogram, string? name, int position, string? dataType, string? inOut = "IN", string? overload = null, int subprogramId = 1,
        string? plsType = null, string? typeOwner = null, string? typeName = null, string? typeSubname = null, bool defaulted = false) =>
        new(subprogram, overload, subprogramId, name, position, position, inOut, dataType, plsType ?? dataType, typeOwner, typeName, typeSubname, defaulted);

    [Fact]
    public void Overloads_stay_separate_in_declaration_order()
    {
        ArgumentRow[] rows =
        [
            Row("BERECHNE", null, 0, "NUMBER", "OUT", overload: "2", subprogramId: 3),
            Row("BERECHNE", "P_BETRAG", 1, "NUMBER", overload: "2", subprogramId: 3),
            Row("BERECHNE", "P_WAEHRUNG", 2, "VARCHAR2", overload: "2", subprogramId: 3, defaulted: true),
            Row("BERECHNE", null, 0, "NUMBER", "OUT", overload: "1", subprogramId: 2),
            Row("BERECHNE", "P_BETRAG", 1, "NUMBER", overload: "1", subprogramId: 2),
            Row("BUCHE", "P_ID", 1, "NUMBER", subprogramId: 1),
            Row("BUCHE", "P_ERGEBNIS", 2, "VARCHAR2", "OUT", subprogramId: 1),
            Row("BUCHE", "P_ZAEHLER", 3, "NUMBER", "IN/OUT", subprogramId: 1),
        ];

        var subprograms = PlSqlArguments.Group([], rows, "APP");

        Assert.Equal([("BUCHE", (int?)null), ("BERECHNE", 1), ("BERECHNE", 2)], subprograms.Select(s => (s.Name, s.Overload)));
        var buche = subprograms[0];
        Assert.False(buche.IsFunction);
        Assert.Equal(
            [new PlSqlParameter("P_ID", PlSqlDirection.In, "NUMBER", false), new PlSqlParameter("P_ERGEBNIS", PlSqlDirection.Out, "VARCHAR2", false),
             new PlSqlParameter("P_ZAEHLER", PlSqlDirection.InOut, "NUMBER", false)],
            buche.Parameters);
        var second = subprograms[2];
        Assert.Equal("NUMBER", second.ReturnType);
        Assert.Equal(["P_BETRAG", "P_WAEHRUNG"], second.Parameters.Select(p => p.Name));
        Assert.True(second.Parameters[1].HasDefault);
    }

    [Fact]
    public void A_procedure_without_parameters_comes_from_the_declaration_alone()
    {
        // Oracle 23: no ALL_ARGUMENTS row at all for AUFRAEUMEN.
        var subprograms = PlSqlArguments.Group(
            [new SubprogramRow("AUFRAEUMEN", null, 2), new SubprogramRow("BUCHE", null, 1)], [Row("BUCHE", "P_ID", 1, "NUMBER")], "APP");

        Assert.Equal(["BUCHE", "AUFRAEUMEN"], subprograms.Select(s => s.Name));
        Assert.False(subprograms[1].IsFunction);
        Assert.Empty(subprograms[1].Parameters);
    }

    [Fact]
    public void A_placeholder_row_of_older_versions_is_no_parameter()
    {
        var procedure = Assert.Single(PlSqlArguments.Group([new SubprogramRow("AUFRAEUMEN", null, 1)], [Row("AUFRAEUMEN", null, 1, null)], "APP"));

        Assert.False(procedure.IsFunction);
        Assert.Empty(procedure.Parameters);
    }

    [Fact]
    public void A_function_without_parameters_has_only_its_return_value()
    {
        var function = Assert.Single(PlSqlArguments.Group([new SubprogramRow("HEUTE", null, 1)], [Row("HEUTE", null, 0, "DATE", "OUT")], "APP"));

        Assert.Equal("DATE", function.ReturnType);
        Assert.Empty(function.Parameters);
    }

    [Theory]
    [InlineData("NUMBER", "NUMBER", null, null, null, "NUMBER")]
    [InlineData("NUMBER", "PLS_INTEGER", null, null, null, "PLS_INTEGER")]
    [InlineData("PL/SQL BOOLEAN", "BOOLEAN", null, null, null, "BOOLEAN")]
    [InlineData("REF CURSOR", null, null, null, null, "SYS_REFCURSOR")]
    [InlineData("PL/SQL RECORD", null, "APP", "KUNDEN", null, "KUNDEN%ROWTYPE")]
    [InlineData("PL/SQL RECORD", null, "APP", "PKG_RECHNUNG", "T_POSTEN", "PKG_RECHNUNG.T_POSTEN")]
    [InlineData("TABLE", null, "ERP", "T_ID_LISTE", null, "ERP.T_ID_LISTE")]
    [InlineData("OBJECT", null, "APP", "T_ADRESSE", null, "T_ADRESSE")]
    public void Type_text(string dataType, string? plsType, string? typeOwner, string? typeName, string? typeSubname, string expected)
    {
        var row = new ArgumentRow("P", null, 1, "X", 1, 1, "IN", dataType, plsType, typeOwner, typeName, typeSubname, false);

        Assert.Equal(expected, PlSqlArguments.TypeText(row, "APP"));
    }
}
