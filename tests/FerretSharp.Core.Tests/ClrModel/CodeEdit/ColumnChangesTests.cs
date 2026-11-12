using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.ClrModel.CodeGenerationModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>What changed between two states of a table, for the C# project (WP-41).</summary>
public sealed class ColumnChangesTests
{
    private static ColumnInfo Column(string name) => Kunden.Columns.Single(c => c.Name == name);

    private static readonly ColumnInfo NewLegacyCode = new("LEGACY_CODE", "NUMBER", null, false, 3, 0, true, false, null, 13);

    /// <summary>
    /// Renames are matched before the same names, and only when they ran. Second review of WP-41 (N1): "drop STATUS, rename
    /// LEGACY_CODE to STATUS" gave the new STATUS to the old one – its property stayed and took the other column's facets.
    /// Third review (R1): a swap, and a rename with a new column under the old name, were taken as not run.
    /// </summary>
    [Theory]
    // drop STATUS, LEGACY_CODE → STATUS
    [InlineData("onto a dropped name", "Dropped STATUS, Renamed LEGACY_CODE→STATUS")]
    // the same, but nothing ran: both names still there
    [InlineData("not run", "")]
    // STATUS ↔ LEGACY_CODE
    [InlineData("swap", "Renamed STATUS→LEGACY_CODE, Renamed LEGACY_CODE→STATUS")]
    // LEGACY_CODE → CODE, and a new column LEGACY_CODE
    [InlineData("new column under the old name", "Renamed LEGACY_CODE→CODE, Added LEGACY_CODE")]
    public void A_rename_is_matched_before_the_same_name_when_it_ran(string @case, string expected)
    {
        var status = Column("STATUS");
        var legacy = Column("LEGACY_CODE");
        var others = Kunden.Columns.Where(c => c.Name is not "STATUS" and not "LEGACY_CODE").ToList();
        var (after, renames) = @case switch
        {
            "onto a dropped name" => ((IReadOnlyList<ColumnInfo>)[.. others, legacy with { Name = "STATUS" }], new Dictionary<string, string> { ["LEGACY_CODE"] = "STATUS" }),
            "not run" => (Kunden.Columns, new Dictionary<string, string> { ["LEGACY_CODE"] = "STATUS" }),
            "swap" => ([.. others, status with { Name = "LEGACY_CODE" }, legacy with { Name = "STATUS" }],
                new Dictionary<string, string> { ["STATUS"] = "LEGACY_CODE", ["LEGACY_CODE"] = "STATUS" }),
            _ => ([.. others, status, legacy with { Name = "CODE" }, NewLegacyCode], new Dictionary<string, string> { ["LEGACY_CODE"] = "CODE" }),
        };

        var changes = ColumnChanges.Of(Kunden, after, renames);

        Assert.Equal(expected, string.Join(", ", changes.Select(c => c.Kind + " " + c.Column + (c.Kind == ColumnChangeKind.Renamed ? "→" + c.After!.Name : ""))));
    }
}
