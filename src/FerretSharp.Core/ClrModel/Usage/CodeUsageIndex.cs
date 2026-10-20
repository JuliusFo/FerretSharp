namespace FerretSharp.Core.ClrModel;

/// <summary>One use of an entity's property or table, as the views show it (WP-40).</summary>
/// <param name="Assembly">The assembly the code is compiled into.</param>
/// <param name="IsTest">The assembly is a test project: shown apart, not counted for "unused".</param>
/// <param name="IsMapping">
/// Mapping code, not use: an <c>IEntityTypeConfiguration&lt;T&gt;</c>, the context's <c>OnModelCreating</c> or its DbSet
/// properties. Not counted for "unused"; WP-41 finds the configuration through it.
/// </param>
/// <param name="Via">The base class or interface the code names (<c>EntityBase.Id</c>); null if it names the entity itself.</param>
public sealed record CodeUsage(CodeUse Use, string Assembly, bool IsTest, bool IsMapping, string? Via);

/// <summary>
/// The uses of the C# model's entities in the solution's compiled code (WP-40, ADR 0030): built from what
/// <see cref="IlUsageScanner"/> found and the model export, which names each entity's CLR type and properties. Inheritance
/// across the scanned assemblies counts: a use of <c>EntityBase.Id</c> or <c>IHasName.Name</c> is a use of the property in
/// every entity deriving from it.
/// </summary>
public sealed class CodeUsageIndex
{
    private const string DbContextType = "Microsoft.EntityFrameworkCore.DbContext";

    private const string ConfigurationType = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1";

    private static readonly string[] ModelMethods = ["OnModelCreating", "ConfigureConventions"];

    private readonly Dictionary<(string Type, string Member), List<CodeUsage>> _byMember = [];
    private readonly Dictionary<string, TypeLink> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> _ancestors = new(StringComparer.Ordinal);

    private CodeUsageIndex(ModelExport model, IReadOnlyList<AssemblyUses> assemblies)
    {
        Model = model;
        foreach (var type in assemblies.SelectMany(a => a.Types))
        {
            _types.TryAdd(type.Type, type);
        }

        foreach (var assembly in assemblies)
        {
            foreach (var use in assembly.Uses)
            {
                var key = (use.Type, use.Member);
                if (!_byMember.TryGetValue(key, out var list))
                {
                    _byMember[key] = list = [];
                }

                list.Add(new CodeUsage(use, assembly.AssemblyName, assembly.IsTest, IsMapping(use), null));
            }
        }
    }

    public ModelExport Model { get; }

    public static CodeUsageIndex Build(ModelExport model, IReadOnlyList<AssemblyUses> assemblies) => new(model, assemblies);

    /// <summary>Reads, writes and expression trees of the property, through the entity or a base class or interface of it.</summary>
    public IReadOnlyList<CodeUsage> ForProperty(EntityExport entity, string property) =>
        Ancestors(entity.ClrType)
            .Prepend(entity.ClrType)
            .SelectMany(type => (_byMember.GetValueOrDefault((type, property)) ?? [])
                .Select(u => type == entity.ClrType ? u : u with { Via = type }))
            .Where(u => u.Use.Kind != CodeUseKind.Query)
            .OrderBy(u => u, Order)
            .ToList();

    /// <summary>Queries of the entity's table: <c>Set&lt;T&gt;()</c> and reads of the context's DbSet property.</summary>
    public IReadOnlyList<CodeUsage> ForEntity(EntityExport entity)
    {
        var sets = _byMember.GetValueOrDefault((entity.ClrType, "")) ?? [];
        var dbSet = entity.DbSet is { } name ? _byMember.GetValueOrDefault((Model.ContextType, name)) ?? [] : [];
        return sets.Concat(dbSet).OrderBy(u => u, Order).ToList();
    }

    /// <summary>
    /// No use outside tests and mapping code. A foreign key property also counts as used through its navigations (EF
    /// joins over the key, <c>auftrag.Kunde</c> needs <c>KundeId</c>). Never "unused": shadow properties (no getter, the
    /// IL cannot see them) and keys – the primary key and a key other entities' foreign keys point to – which EF needs
    /// whether code reads them or not (found in the E2E: every primary key was marked).
    /// </summary>
    public bool IsUnused(EntityExport entity, PropertyExport property) =>
        !property.IsShadow && !IsKey(entity, property.Name) && !UsesCountingFor(entity, property).Any(u => !u.IsTest && !u.IsMapping);

    private bool IsKey(EntityExport entity, string property) =>
        entity.PrimaryKey.Contains(property, StringComparer.Ordinal)
        || Model.Entities.Any(e => e.ForeignKeys.Any(k => k.PrincipalEntity == entity.Name && k.PrincipalProperties.Contains(property, StringComparer.Ordinal)));

    /// <summary>Every mapped, non-shadow property without a use outside tests and mapping code (the report).</summary>
    public IReadOnlyList<(EntityExport Entity, PropertyExport Property)> Unused() =>
        Model.Entities
            .Where(e => !e.IsOwned)
            .SelectMany(e => e.Properties.Where(p => p.Column is not null && IsUnused(e, p)).Select(p => (e, p)))
            .ToList();

    private IEnumerable<CodeUsage> UsesCountingFor(EntityExport entity, PropertyExport property)
    {
        var uses = ForProperty(entity, property.Name).AsEnumerable();
        foreach (var key in entity.ForeignKeys.Where(k => k.Properties.Contains(property.Name, StringComparer.Ordinal)))
        {
            if (key.Navigation is { } navigation)
            {
                uses = uses.Concat(ForProperty(entity, navigation));
            }

            if (key.InverseNavigation is { } inverse && Model.Entities.FirstOrDefault(e => e.Name == key.PrincipalEntity) is { } principal)
            {
                uses = uses.Concat(ForProperty(principal, inverse));
            }
        }

        return uses;
    }

    /// <summary>Base classes and interfaces, transitively, as far as the scanned assemblies know them (outside ones by name only).</summary>
    private IReadOnlySet<string> Ancestors(string type)
    {
        if (_ancestors.TryGetValue(type, out var known))
        {
            return known;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([type]);
        while (pending.TryPop(out var current))
        {
            if (!_types.TryGetValue(current, out var link))
            {
                continue;
            }

            foreach (var parent in link.Interfaces.Prepend(link.BaseType).OfType<string>())
            {
                if (result.Add(parent))
                {
                    pending.Push(parent);
                }
            }
        }

        _ancestors[type] = result;
        return result;
    }

    private bool IsMapping(CodeUse use)
    {
        var caller = Ancestors(use.CallerType);
        return caller.Contains(ConfigurationType)
               || (caller.Contains(DbContextType)
                   && (ModelMethods.Contains(use.CallerMethod, StringComparer.Ordinal) || use.CallerMethod.StartsWith("get_", StringComparison.Ordinal)));
    }

    /// <summary>Application code first, then tests and mapping; within, by file and line.</summary>
    private static readonly Comparer<CodeUsage> Order = Comparer<CodeUsage>.Create((a, b) =>
    {
        var group = (a.IsTest || a.IsMapping).CompareTo(b.IsTest || b.IsMapping);
        if (group != 0)
        {
            return group;
        }

        var file = string.Compare(a.Use.Document, b.Use.Document, StringComparison.OrdinalIgnoreCase);
        return file != 0 ? file : Nullable.Compare(a.Use.Line, b.Use.Line);
    });
}
