using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.ClrModel.CodeGenerationModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>What changed between two states of a table, for the C# project (WP-41).</summary>
public sealed class ColumnChangesTests
{
    private static ColumnInfo Column(string name) => Kunden.Columns.Single(c => c.Name == name);

    /// <summary>
    /// Second review of WP-41, N1: "drop STATUS, rename LEGACY_CODE to STATUS" gave the new STATUS to the old one – its
    /// property stayed and took the other column's facets. A rename that did not run (its source is still there) is no rename.
    /// </summary>
    [Theory]
    [InlineData(true, "Dropped STATUS, Renamed LEGACY_CODE→STATUS")]
    [InlineData(false, "")]
    public void A_rename_onto_the_name_of_a_dropped_column_is_matched_before_the_same_name(bool ran, string expected)
    {
        var legacy = Column("LEGACY_CODE");
        IReadOnlyList<ColumnInfo> after = ran
            ? [.. Kunden.Columns.Where(c => c.Name is not "STATUS" and not "LEGACY_CODE"), legacy with { Name = "STATUS" }]
            : Kunden.Columns;

        var changes = ColumnChanges.Of(Kunden, after, new Dictionary<string, string> { ["LEGACY_CODE"] = "STATUS" });

        Assert.Equal(expected, string.Join(", ", changes.Select(c => c.Kind + " " + c.Column + (c.Kind == ColumnChangeKind.Renamed ? "→" + c.After!.Name : ""))));
    }
}
