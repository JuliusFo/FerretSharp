using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>A column of the "Usage" view (WP-40): its property and where the code uses it, through navigations too.</summary>
/// <param name="Property">Null if the column has no property in the model.</param>
/// <param name="Unused">Mapped, but no use outside tests and mapping code (<see cref="CodeUsageIndex.IsUnused"/>).</param>
public sealed record ColumnUsageRow(string Column, PropertyExport? Property, IReadOnlyList<CodeUsage> Uses, bool Unused)
{
    /// <summary>Uses that count (outside tests and mapping code).</summary>
    public int Counted => Uses.Count(u => !u.IsTest && !u.IsMapping);
}

/// <summary>What the "Usage" view, the columns view and the designer show of the code usage index (WP-40).</summary>
public static class CodeUsageRows
{
    /// <summary>The table's columns in schema order with their uses; empty if the table has no entity.</summary>
    public static IReadOnlyList<ColumnUsageRow> Columns(ClrModelMapping mapping, CodeUsageIndex index, TableDetails details)
    {
        if (mapping.EntityOf(details.Table.Ref) is not { } entity)
        {
            return [];
        }

        return details.Columns.Select(c =>
        {
            var property = mapping.PropertyOf(details.Table.Ref, c.Name);
            return property is null
                ? new ColumnUsageRow(c.Name, null, [], false)
                : new ColumnUsageRow(c.Name, property, index.ForColumn(entity.Entity, property.Name), index.IsUnused(entity.Entity, property));
        }).ToList();
    }

    /// <summary>The column's row, for the columns view and the designer; null without property or entity.</summary>
    public static ColumnUsageRow? Column(ClrModelMapping mapping, CodeUsageIndex index, TableRef table, string column)
    {
        if (mapping.EntityOf(table) is not { } entity || mapping.PropertyOf(table, column) is not { } property)
        {
            return null;
        }

        return new ColumnUsageRow(column, property, index.ForColumn(entity.Entity, property.Name), index.IsUnused(entity.Entity, property));
    }

    /// <summary>
    /// For the designer's warnings and its confirmation (WP-40): each dropped or renamed column that code uses – tests and
    /// mapping code included, their build breaks too – with the first places. A dropped foreign key column also names the
    /// navigations used: the build does not break over them, their queries fail at run time.
    /// </summary>
    /// <param name="changed">Columns as the table has them now, and their new name (null = dropped).</param>
    public static IReadOnlyList<string> DropNotes(ClrModelMapping mapping, CodeUsageIndex index, TableRef table, IEnumerable<(string Column, string? NewName)> changed) =>
        changed
            .Select(c => (c.Column, c.NewName, Row: Column(mapping, index, table, c.Column)))
            .Where(c => c.Row is { Uses.Count: > 0 })
            .Select(c =>
            {
                var row = c.Row!;
                if (c.NewName is not null)
                {
                    return TextFormat.Format(ModelViewText.Usage_RenameUsed, c.Column, row.Property!.Name, row.Uses.Count, Callers(row.Uses));
                }

                var direct = row.Uses.Where(u => u.Navigation is null).ToList();
                var through = row.Uses.Where(u => u.Navigation is not null).ToList();
                var navigations = string.Join(", ", through.Select(u => u.Navigation).Distinct());
                if (direct.Count == 0)
                {
                    return TextFormat.Format(ModelViewText.Usage_DropUsedNavigation, c.Column, navigations, through.Count, Callers(through));
                }

                var note = TextFormat.Format(ModelViewText.Usage_DropUsed, c.Column, row.Property!.Name, direct.Count, Callers(direct));
                return through.Count == 0 ? note : note + " " + TextFormat.Format(ModelViewText.Usage_DropAlsoNavigation, through.Count, navigations);
            })
            .ToList();

    /// <summary>The first three callers: <c>Export.Run, Import.Run, KundenService.Run, …</c>.</summary>
    private static string Callers(IEnumerable<CodeUsage> uses)
    {
        var callers = uses.Select(u => Caller(u.Use)).Distinct().ToList();
        return string.Join(", ", callers.Take(3)) + (callers.Count > 3 ? ", …" : "");
    }

    /// <summary>The counter of the "Columns" sub-tab: mapped columns no code uses.</summary>
    public static int UnusedCount(IReadOnlyList<ColumnUsageRow> rows) => rows.Count(r => r.Unused);

    /// <summary>
    /// The counter of the "Queries" sub-tab: queries in application code, navigations to the table included (tests,
    /// mapping code and the DbSet property left out).
    /// </summary>
    public static int QueryCount(IEnumerable<CodeUsage> queries) => queries.Count(u => !u.IsTest && !u.IsMapping);

    public static string Kind(CodeUseKind kind) => kind switch
    {
        CodeUseKind.Read => ModelViewText.Usage_KindRead,
        CodeUseKind.Write => ModelViewText.Usage_KindWrite,
        CodeUseKind.Expression => ModelViewText.Usage_KindExpression,
        _ => ModelViewText.Usage_KindQuery,
    };

    /// <summary><c>CustomerService.GetOffene</c>.</summary>
    public static string Caller(CodeUse use) => $"{ClrModelMapping.ShortName(use.CallerType)}.{use.CallerMethod}";

    /// <summary><c>CustomerService.cs:142</c>; empty without a PDB.</summary>
    public static string Location(CodeUse use) =>
        use.Document is null ? "" : use.Line is { } line ? $"{FileName(use.Document)}:{line}" : FileName(use.Document);

    /// <summary>What "Copy name" copies: <c>Customer.Email</c> (for Ctrl+T in Visual Studio).</summary>
    public static string Name(EntityExport entity, string property) => $"{ClrModelMapping.ShortName(entity.ClrType)}.{property}";

    /// <summary>What "Copy name" copies for a use: the navigation it goes through, else the property or the entity.</summary>
    public static string Name(CodeUsage use, EntityExport entity, string? property) =>
        use.Navigation ?? (property is null ? ClrModelMapping.ShortName(entity.ClrType) : Name(entity, property));

    /// <summary>"12 projects · 2 not built · 1 out of date".</summary>
    public static string Projects(CodeUsageResult result)
    {
        var parts = new List<string> { TextFormat.Format(ModelViewText.Usage_Projects, result.Projects.Count) };
        var notBuilt = result.Projects.Count(p => p.Build is { IsBuilt: false });
        var stale = result.Projects.Count(p => p.Build is { IsStale: true });
        var failed = result.Projects.Count(p => p.Error is not null);
        if (notBuilt > 0)
        {
            parts.Add(TextFormat.Format(ModelViewText.Usage_ProjectsNotBuilt, notBuilt));
        }

        if (stale > 0)
        {
            parts.Add(TextFormat.Format(ModelViewText.Usage_ProjectsStale, stale));
        }

        if (failed > 0)
        {
            parts.Add(TextFormat.Format(ModelViewText.Usage_ProjectsFailed, failed));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The projects behind <see cref="Projects"/> that are not built, out of date or unreadable, one per line (tooltip).</summary>
    public static string ProjectDetails(CodeUsageResult result) =>
        string.Join("\n", result.Projects
            .Select(p => p.Error is { } error ? $"{p.Name}: {error}"
                : p.Build is { IsBuilt: false } ? $"{p.Name}: {ModelViewText.Usage_NotBuilt}"
                : p.Build is { NewerSource: { } newer } ? $"{p.Name}: {TextFormat.Format(ModelViewText.Usage_Stale, FileName(newer))}"
                : p.Uses is { HasPdb: false } ? $"{p.Name}: {ModelViewText.Usage_NoPdb}"
                : null)
            .OfType<string>());

    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
}
