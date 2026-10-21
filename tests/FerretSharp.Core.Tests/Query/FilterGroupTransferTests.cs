using FerretSharp.Core.Connections;
using FerretSharp.Core.Links;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Tests.Query;

/// <summary>Filter groups in links and presets (WP-50).</summary>
public class FilterGroupTransferTests
{
    private static ColumnInfo Column(string name, string type, int position) =>
        new(name, type, type == "VARCHAR2" ? 50 : null, false, null, null, true, IsIdentity: false, Default: null, Position: position);

    private static readonly TableDetails Auftrag = new(new TableSummary("ERP", "AUFTRAG", TableKind.Table),
        [Column("ID", "NUMBER", 1), Column("STATUS", "VARCHAR2", 2), Column("KBEZ", "VARCHAR2", 3)], ["ID"], [], false);

    private static readonly ConnectionProfile Profile = new(Guid.NewGuid(), "ERP Test", ConnectionKind.Test, new HostPortAddress("db01", 1521, "ERP", null), "erp", null, false);

    private static readonly FilterGroup Any = new(3, FilterGroupKind.Any);

    [Fact]
    public void A_link_with_groups_is_version_2_and_reads_back_its_groups()
    {
        var link = TableLink.For(Profile, Auftrag.Table.Ref, [
            FilterCondition.Of("ID", FilterOperator.Gt, "5"),
            FilterCondition.Of("KBEZ", FilterOperator.Equals, "NA") with { Group = Any },
            FilterCondition.Of("STATUS", FilterOperator.In, "OFFEN", "NEU") with { Group = Any },
            FilterCondition.Of("STATUS", FilterOperator.IsNull) with { Group = new FilterGroup(4, FilterGroupKind.None, Enabled: false) },
        ], [], TabMode.Data);

        var uri = link.ToUri();
        var read = TableLink.Parse(uri).Link!;

        Assert.StartsWith("ferretsharp://open?v=2&", uri);
        Assert.Contains("&f=ID,gt,5&g=1,any,KBEZ,equals,NA&g=1,any,STATUS,in,OFFEN,NEU", uri, StringComparison.Ordinal);
        Assert.Equal(3, read.Filters.Count); // the disabled group filters nothing and is left out
        Assert.Equal([null, Any with { Id = 1 }, Any with { Id = 1 }], read.Filters.Select(f => f.Group)); // numbered from 1
        Assert.Equal(["OFFEN", "NEU"], read.Filters[2].Values);
    }

    [Fact]
    public void A_link_without_groups_stays_version_1_for_older_ferretsharp()
    {
        var link = TableLink.For(Profile, Auftrag.Table.Ref, [FilterCondition.Of("ID", FilterOperator.Gt, "5")], [], TabMode.Data);

        Assert.StartsWith("ferretsharp://open?v=1&", link.ToUri());
    }

    [Fact]
    public void An_appended_preset_brings_its_groups_with_new_ids()
    {
        var current = new[] { FilterCondition.Of("ID", FilterOperator.Gt, "5") with { Group = Any } };
        var preset = new FilterPreset(Guid.NewGuid(), "NA oder offen", "AUFTRAG",
            [FilterCondition.Of("KBEZ", FilterOperator.Equals, "NA") with { Group = Any }, FilterCondition.Of("STATUS", FilterOperator.IsNull) with { Group = Any }], []);

        var applied = FilterPresets.Apply(preset, Auftrag, current, append: true);

        Assert.Equal([1, 2, 2], applied.Filters.Select(f => f.Group!.Id));
        Assert.False(FilterPresets.Differs(preset, Auftrag, preset.Filters, []));
        Assert.True(FilterPresets.Differs(preset, Auftrag, [preset.Filters[0], preset.Filters[1] with { Group = null }], []));
        Assert.True(FilterPresets.Differs(preset, Auftrag, [.. preset.Filters.Select(f => f with { Group = Any with { Kind = FilterGroupKind.None } })], []));
        // The filter bar numbers groups anew: other ids alone are no change.
        Assert.False(FilterPresets.Differs(preset, Auftrag, [.. preset.Filters.Select(f => f with { Group = Any with { Id = 1 } })], []));
    }

    [Fact]
    public void Group_ids_of_a_link_start_at_1_and_larger_ones_are_refused()
    {
        var link = TableLink.For(Profile, Auftrag.Table.Ref, [FilterCondition.Of("KBEZ", FilterOperator.IsNull) with { Group = Any with { Id = int.MaxValue } }], [], TabMode.Data);

        Assert.Contains("&g=1,any,KBEZ,isNull", link.ToUri(), StringComparison.Ordinal);
        Assert.Contains("ungültigen Filter auf", TableLink.Parse("ferretsharp://open?v=2&s=ERP&t=X&g=51,any,KBEZ,isNull").Error);
        Assert.Contains("ungültigen Filter auf abc", TableLink.Parse("ferretsharp://open?v=2&s=ERP&t=X&g=abc").Error);
    }
}
