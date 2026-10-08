using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Schema;

public class PlSqlObjectListTests
{
    [Fact]
    public void Package_specification_and_body_become_one_entry_sorted_by_name()
    {
        PlSqlObjectRow[] rows =
        [
            new("PKG_RECHNUNG", "PACKAGE BODY", "INVALID"),
            new("PKG_RECHNUNG", "PACKAGE", "VALID"),
            new("BERECHNE_RABATT", "FUNCTION", "VALID"),
            new("PKG_NUR_SPEC", "PACKAGE", "VALID"),
            new("TRG_KUNDEN_HIST", "TRIGGER", "VALID"),
            new("TRG_ALT", "TRIGGER", "VALID"),
            new("AUFRAEUMEN", "PROCEDURE", "INVALID"),
        ];

        var list = PlSqlObjectList.Combine("APP", rows, new HashSet<string> { "TRG_ALT" });

        Assert.Equal(["AUFRAEUMEN", "BERECHNE_RABATT", "PKG_NUR_SPEC", "PKG_RECHNUNG", "TRG_ALT", "TRG_KUNDEN_HIST"], list.Select(o => o.Name));
        var package = list.Single(o => o.Name == "PKG_RECHNUNG");
        Assert.Equal((PlSqlKind.Package, "VALID", "INVALID"), (package.Kind, package.Status, package.BodyStatus));
        Assert.True(package.IsInvalid);
        Assert.Null(list.Single(o => o.Name == "PKG_NUR_SPEC").BodyStatus);
        Assert.True(list.Single(o => o.Name == "TRG_ALT").IsDisabled);
        Assert.False(list.Single(o => o.Name == "TRG_KUNDEN_HIST").IsDisabled);
        Assert.True(list.Single(o => o.Name == "AUFRAEUMEN").IsInvalid);
    }

    [Fact]
    public void A_body_without_visible_specification_is_left_out()
    {
        var list = PlSqlObjectList.Combine("APP", [new PlSqlObjectRow("PKG", "PACKAGE BODY", "VALID")], new HashSet<string>());

        Assert.Empty(list);
    }

    [Theory]
    [InlineData("PACKAGE", PlSqlKind.Package, PlSqlPart.Spec)]
    [InlineData("PACKAGE BODY", PlSqlKind.Package, PlSqlPart.Body)]
    [InlineData("PROCEDURE", PlSqlKind.Procedure, PlSqlPart.Spec)]
    [InlineData("FUNCTION", PlSqlKind.Function, PlSqlPart.Spec)]
    [InlineData("TRIGGER", PlSqlKind.Trigger, PlSqlPart.Spec)]
    public void Object_types_map_both_ways(string objectType, PlSqlKind kind, PlSqlPart part)
    {
        Assert.Equal((kind, part), PlSqlKinds.Of(objectType));
        Assert.Equal(objectType, PlSqlKinds.ObjectType(kind, part));
    }

    [Theory]
    [InlineData("TABLE")]
    [InlineData("TYPE")]
    [InlineData("SYNONYM")]
    public void Other_object_types_are_no_PlSql_units(string objectType) => Assert.Null(PlSqlKinds.Of(objectType));

    [Fact]
    public void A_procedure_has_no_body_type()
    {
        var unit = new PlSqlObjectSummary("APP", "P", PlSqlKind.Procedure, "VALID");

        Assert.Equal("PROCEDURE", unit.ObjectType(PlSqlPart.Body));
    }
}

public class PlSqlSourceTests
{
    [Theory]
    [InlineData("  x := 1;\n", "  x := 1;")]
    [InlineData("  x := 1;\r\n", "  x := 1;")]
    [InlineData("  x := 1;\r", "  x := 1;")]
    [InlineData("END;", "END;")]
    [InlineData("\n", "")]
    [InlineData(null, "")]
    public void A_line_loses_only_its_line_break(string? text, string expected) => Assert.Equal(expected, PlSqlSourceText.LineOf(text));

    [Fact]
    public void Text_has_one_line_per_dictionary_line()
    {
        var source = new PlSqlSource(["PROCEDURE P IS", "BEGIN", "  NULL;", "END;"]);

        Assert.Equal("PROCEDURE P IS\nBEGIN\n  NULL;\nEND;", source.Text);
        Assert.False(source.IsWrapped);
        Assert.False(source.IsEmpty);
    }

    [Fact]
    public void Wrapped_source_is_recognized_from_its_header()
    {
        // Wrapped units hold many text lines per dictionary line.
        var source = new PlSqlSource(["PACKAGE BODY pkg_geheim wrapped \na000000\n369\nabcd\nabcd\n", "b\n2d 6d\nyQ3"]);

        Assert.True(source.IsWrapped);
    }

    [Fact]
    public void Wrapped_needs_the_second_header_line()
    {
        // A procedure that happens to be called WRAPPED.
        Assert.False(new PlSqlSource(["PROCEDURE wrapped", "IS BEGIN NULL; END;"]).IsWrapped);
        Assert.True(new PlSqlSource(["PROCEDURE \"App\".\"P\" wrapped", "a000000", "1"]).IsWrapped);
        Assert.False(new PlSqlSource([]).IsWrapped);
        Assert.True(new PlSqlSource([]).IsEmpty);
    }
}

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

public class PlSqlSchemaCacheTests
{
    private const string Owner = "APP";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PlSqlObjectSummary Rechnung = new(Owner, "PKG_RECHNUNG", PlSqlKind.Package, "VALID", "VALID");
    private static readonly PlSqlObjectSummary Trigger = new(Owner, "KUNDEN", PlSqlKind.Trigger, "VALID");

    [Fact]
    public async Task Loads_own_units_and_synonym_targets_one_entry_each()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns([new TableSummary(Owner, "KUNDEN", TableKind.Table)]);
        reader.GetPlSqlObjectsAsync(Owner, Arg.Any<CancellationToken>()).Returns([Rechnung, Trigger]);
        var viaPublic = new PlSqlObjectSummary("ERP", "PKG_DRUCK", PlSqlKind.Package, "VALID", null, new SynonymInfo("PUBLIC", "PKG_DRUCK"));
        var viaPrivate = viaPublic with { Synonym = new SynonymInfo(Owner, "DRUCK") };
        var toOwn = Rechnung with { Synonym = new SynonymInfo("PUBLIC", "RECHNUNG") };
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(new SynonymTargets([], [viaPublic, viaPrivate, toOwn]));
        reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var cache = new SchemaCache(reader, Owner);

        await cache.LoadAsync(Ct);

        Assert.Equal(["DRUCK", "KUNDEN", "PKG_RECHNUNG"], cache.PlSqlObjects.Select(o => o.DisplayName));
        Assert.Same(Trigger, cache.FindPlSql(Trigger.Ref));
        Assert.Equal("KUNDEN", Assert.Single(cache.Tables).Name); // a trigger may share its name with a table
        Assert.Same(Rechnung, cache.FindPlSql(Owner, "PKG_RECHNUNG", "PACKAGE BODY"));
        Assert.Equal("DRUCK", cache.FindPlSql("ERP", "PKG_DRUCK", "PACKAGE")!.DisplayName);
        Assert.Null(cache.FindPlSql(Owner, "KUNDEN", "TABLE"));
    }
}

public class LikePatternTests
{
    [Theory]
    [InlineData("KUNDEN_ID", "%KUNDEN\\_ID%")]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a\\b", "%a\\\\b%")]
    public void Wildcards_of_the_text_are_escaped(string text, string expected) => Assert.Equal(expected, LikePattern.Contains(text));
}
