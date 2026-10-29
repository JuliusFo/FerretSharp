using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>The colour of a constraint's badge in the designer and the constraints view (review 3.54.0, B5).</summary>
public static class ConstraintBadges
{
    /// <summary><c>pk</c>, <c>uk</c>, <c>fk</c>; none for the rest.</summary>
    public static string Css(ConstraintType type) => type switch
    {
        ConstraintType.PrimaryKey => "pk",
        ConstraintType.Unique => "uk",
        ConstraintType.ForeignKey => "fk",
        _ => "",
    };
}
