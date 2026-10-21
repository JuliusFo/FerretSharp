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
