using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>Columns without property of one table (the report's grouped list).</summary>
public sealed record ReportColumnGroup(EntityMapping Entity, IReadOnlyList<ColumnWithoutProperty> Columns);

/// <summary>The first rows of a list and how many more there are (the report shows the rest on "show all").</summary>
public sealed record ReportSlice<T>(IReadOnlyList<T> Shown, int More);

/// <summary>
/// What the report page (WP-72) shows of a <see cref="ModelReport"/>: which sections are open, the search and chips of each
/// list, which table groups are open and which lists show all their rows. The open sections are kept for the session
/// (decision of the user: not saved), the rest belongs to the page.
/// </summary>
public sealed class ReportView
{
    /// <summary>Rows (or table groups) a list shows before "show all".</summary>
    public const int FirstRows = 25;

    private static readonly HashSet<ReportPart> s_open = [ReportPart.Tables, ReportPart.Columns, ReportPart.Relations];
    private static readonly Lock s_lock = new();

    private readonly HashSet<ReportPart> _showAll = [];
    private readonly HashSet<TableRef> _openGroups = [];

    public bool IsOpen(ReportPart part)
    {
        lock (s_lock)
        {
            return s_open.Contains(part);
        }
    }

    public void Toggle(ReportPart part)
    {
        lock (s_lock)
        {
            if (!s_open.Remove(part))
            {
                s_open.Add(part);
            }
        }
    }

    /// <summary>A tile or a link leads to the section: it opens.</summary>
    public void Open(ReportPart part)
    {
        lock (s_lock)
        {
            s_open.Add(part);
        }
    }

    public void ShowAll(ReportPart part) => _showAll.Add(part);

    /// <summary>The first <see cref="FirstRows"/> of <paramref name="rows"/>, all after <see cref="ShowAll"/>.</summary>
    public ReportSlice<T> Slice<T>(ReportPart part, IReadOnlyList<T> rows) =>
        _showAll.Contains(part) || rows.Count <= FirstRows ? new(rows, 0) : new([.. rows.Take(FirstRows)], rows.Count - FirstRows);

    // Tables without entity.

    public string TableSearch { get; set; } = "";

    /// <summary>Views and materialized views too (by default only tables, decision of the user).</summary>
    public bool TableViews { get; set; }

    /// <summary>Only tables with a foreign key to or from a mapped table: likely part of the domain.</summary>
    public bool TableLinked { get; set; }

    /// <summary>The audit tables and exceptions too.</summary>
    public bool TableHidden { get; set; }

    public IReadOnlyList<TableWithoutEntity> Tables(ModelReport report, ClrModelMapping mapping)
    {
        var search = new TextSearch(TableSearch);
        return [.. report.Tables.Where(t =>
            (TableHidden || !t.IsHidden)
            && (TableViews || t.Table.Kind == TableKind.Table)
            && (!TableLinked || t.Outgoing.Any(f => mapping.EntityOf(f.To) is not null) || t.Incoming.Any(f => mapping.EntityOf(f.From) is not null))
            && search.Matches(t.Table.DisplayName))];
    }

    /// <summary>Views, materialized views and hidden tables, counted for the chips.</summary>
    public static (int Views, int Hidden) TableCounts(ModelReport report) =>
        (report.Tables.Count(t => t.Table.Kind != TableKind.Table && !t.IsHidden), report.Tables.Count(t => t.IsHidden));

    // Columns without property.

    public string ColumnSearch { get; set; } = "";

    /// <summary>One flat list instead of groups by table.</summary>
    public bool ColumnsFlat { get; set; }

    /// <summary>Only NOT NULL columns without default (an insert through EF fails).</summary>
    public bool ColumnsRequired { get; set; }

    public IReadOnlyList<ColumnWithoutProperty> Columns(ModelReport report)
    {
        var search = new TextSearch(ColumnSearch);
        return [.. report.Columns.Where(c =>
            (!ColumnsRequired || c.RequiredWithoutDefault)
            && search.Matches($"{c.Entity.Table.DisplayName}.{c.Column.Name} {ClrModelMapping.ShortName(c.Entity.Entity.ClrType)}"))];
    }

    /// <summary><see cref="Columns"/> by table, in the order of the list.</summary>
    public IReadOnlyList<ReportColumnGroup> ColumnGroups(ModelReport report) =>
        [.. Columns(report).GroupBy(c => c.Entity.Table.Ref).Select(g => new ReportColumnGroup(g.First().Entity, [.. g]))];

    /// <summary>Groups start closed; the search opens the ones it finds (the user looks for a column).</summary>
    public bool IsGroupOpen(TableRef table) => _openGroups.Contains(table) || ColumnSearch.Trim().Length > 0;

    public void ToggleGroup(TableRef table)
    {
        if (!_openGroups.Remove(table))
        {
            _openGroups.Add(table);
        }
    }

    public void OpenGroups(IEnumerable<ReportColumnGroup> groups) => _openGroups.UnionWith(groups.Select(g => g.Entity.Table.Ref));

    public void CloseGroups() => _openGroups.Clear();

    public bool AnyGroupOpen => _openGroups.Count > 0;

    // Relationships.

    /// <summary>Only relationships of the model a constraint could be created for as they are.</summary>
    public bool RelationsConstraintOnly { get; set; }

    public IReadOnlyList<ModelOnlyRelation> ModelOnly(ModelReport report) =>
        [.. report.ModelOnly.Where(r => !RelationsConstraintOnly || r.ConstraintPossible)];

    // Mismatches (moved from the model page).

    public HashSet<MismatchSeverity> Severities { get; } = [MismatchSeverity.Error, MismatchSeverity.Warning, MismatchSeverity.Hint];

    public string MismatchSearch { get; set; } = "";

    public IReadOnlyList<ColumnMismatch> Mismatches(ModelReport report)
    {
        var search = new TextSearch(MismatchSearch);
        return [.. report.Mismatches.Where(m => Severities.Contains(m.Severity) && search.Matches(
            $"{ClrModelMapping.ShortName(m.Entity.ClrType)}.{m.Property.Name} {m.Table.DisplayName}.{m.Column.Name}"))];
    }

    // Unused columns.

    public string UnusedSearch { get; set; } = "";

    /// <summary>Also those test code uses.</summary>
    public bool UnusedWithTests { get; set; } = true;

    public IReadOnlyList<UnusedProperty> Unused(IReadOnlyList<UnusedProperty> rows)
    {
        var search = new TextSearch(UnusedSearch);
        return [.. rows.Where(u => (UnusedWithTests || !u.UsedInTests)
            && search.Matches($"{ClrModelMapping.ShortName(u.Entity.ClrType)}.{u.Property.Name} {u.Located?.Table.DisplayName}.{u.Property.Column}"))];
    }
}
