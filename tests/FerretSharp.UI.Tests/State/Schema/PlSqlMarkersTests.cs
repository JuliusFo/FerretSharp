using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

public class PlSqlMarkersTests
{
    [Fact]
    public void Errors_of_the_part_underline_the_word_they_point_at()
    {
        string[] lines = ["PACKAGE BODY P AS", "  PROCEDURE LAUF IS", "  BEGIN", "    NULL; X_UNBEKANNT := 1;", "  END;", "END;"];
        PlSqlError[] errors =
        [
            new(PlSqlPart.Body, 4, 11, "PLS-00201: identifier 'X_UNBEKANNT' must be declared", false),
            new(PlSqlPart.Body, 4, 11, "PL/SQL: Statement ignored", false),
            new(PlSqlPart.Spec, 1, 1, "PLW-05018: no AUTHID clause", true),
        ];

        var markers = PlSqlMarkers.For(errors, PlSqlPart.Body, lines);

        Assert.Equal(2, markers.Count);
        Assert.Equal((4, 11, 4, 22, "error"), (markers[0].Line, markers[0].Column, markers[0].EndLine, markers[0].EndColumn, markers[0].Severity));
        Assert.Equal("warning", Assert.Single(PlSqlMarkers.For(errors, PlSqlPart.Spec, lines)).Severity);
    }

    [Fact]
    public void Positions_outside_the_text_stay_within_it()
    {
        var marker = Assert.Single(PlSqlMarkers.For([new PlSqlError(PlSqlPart.Spec, 99, 50, "x", false)], PlSqlPart.Spec, ["END;"]));

        Assert.Equal((1, 5, 6), (marker.Line, marker.Column, marker.EndColumn));
    }
}
