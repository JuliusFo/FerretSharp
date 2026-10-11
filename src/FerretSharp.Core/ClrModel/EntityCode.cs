using System.Text;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>C# for a table the C# model has no entity for (WP-23, stage 3).</summary>
/// <param name="Entity">The entity class.</param>
/// <param name="Configuration">Its <c>IEntityTypeConfiguration</c>.</param>
/// <param name="Notes">Where names, types and converters came from, and what is left to do by hand.</param>
public sealed record EntitySuggestion(string Name, string Entity, string Configuration, IReadOnlyList<string> Notes);

/// <summary>
/// An entity and its configuration from a table, in the style of the linked project as far as the model export shows it
/// (decision of the user, WP-23): every column a property (<see cref="PropertyCode"/>), the key, a navigation for each
/// foreign key to a table the model maps, <c>ToTable</c>/<c>ToView</c> unless the project names its tables by convention.
/// No usings and short type names, like the other generated code (ADR 0013).
/// </summary>
public static class EntityCode
{
    /// <summary>Share of entities whose table follows the convention above which the project counts as using it.</summary>
    private const double ConventionShare = 0.95;

    /// <summary>
    /// The entity name for a table: its name in PascalCase, without the plural ending the project's own tables and
    /// entities show most (<c>KUNDEN</c> → <c>Kunde</c> where the project has that pattern). Only a suggestion: German
    /// plurals do not follow a rule.
    /// </summary>
    public static string SuggestName(ModelExport model, string table)
    {
        ArgumentNullException.ThrowIfNull(model);
        var name = PropertyCode.PascalCase(table);

        // An ending with the letter before it: Kunden → Kunde teaches "en" → "e", so Adressen → Adresse, but Position stays.
        var rules = model.Entities
            .Where(e => e is { Table: not null, IsOwned: false })
            .Select(e => (Table: PropertyCode.PascalCase(e.Table!), Entity: ClrModelMapping.ShortName(e.ClrType)))
            .Where(p => p.Entity.Length > 1 && p.Table.Length > p.Entity.Length && p.Table.StartsWith(p.Entity, StringComparison.Ordinal))
            .GroupBy(p => (From: p.Table[(p.Entity.Length - 1)..], To: p.Entity[^1..]))
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.From.Length)
            .Select(g => g.Key);
        var rule = rules.FirstOrDefault(r => name.EndsWith(r.From, StringComparison.Ordinal) && name.Length > r.From.Length + 2);
        return rule == default ? name : name[..^rule.From.Length] + rule.To;
    }

    /// <param name="mapping">The model laid over the schema: which tables foreign keys point to have entities.</param>
    /// <param name="outgoing">The table's foreign keys.</param>
    public static EntitySuggestion Write(ClrModelMapping mapping, TableDetails table, IReadOnlyList<ForeignKeyInfo> outgoing, string name)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(table);
        name = CSharpCode.Identifier(name.Trim());
        var model = mapping.Model;
        var notes = new List<string>();

        // Properties: one per column, names made unique (two columns may suggest the same name).
        var properties = new List<PropertySuggestion>();
        var used = new HashSet<string>(StringComparer.Ordinal) { name };
        foreach (var column in table.Columns)
        {
            var suggestion = PropertyCode.Suggest(model, column);
            var unique = suggestion.Name;
            for (var n = 2; !used.Add(unique); n++)
            {
                unique = suggestion.Name + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            properties.Add(unique == suggestion.Name ? suggestion : Rename(suggestion, unique));
            notes.AddRange(suggestion.Notes.Select(note => $"{unique}: {note}"));
        }

        string PropertyOf(string column) => properties[table.IndexOf(column)].Name;

        // Navigations: a foreign key to a table the model maps.
        var navigations = new List<(string Declaration, string Configuration)>();
        var declared = outgoing.Where(f => f.Source == FkSource.Declared).ToList();
        foreach (var fk in declared)
        {
            if (mapping.EntityOf(fk.To) is not { } principal || fk.FromColumns.Any(c => table.IndexOf(c) < 0))
            {
                continue;
            }

            var principalName = ClrModelMapping.ShortName(principal.Entity.ClrType);
            var navigation = declared.Count(f => mapping.EntityOf(f.To)?.Entity == principal.Entity) == 1
                ? principalName
                : NavigationName(PropertyOf(fk.FromColumns[0]));
            if (!used.Add(navigation))
            {
                navigation += "Navigation"; // a property has the name already (the foreign key column itself)
                used.Add(navigation);
            }

            var optional = fk.FromColumns.Any(c => table.Columns[table.IndexOf(c)].Nullable);
            var declaration = optional
                ? $"public {principalName}? {navigation} {{ get; set; }}"
                : $"public {principalName} {navigation} {{ get; set; }} = null!;";
            var keys = Lambda(fk.FromColumns.Select(PropertyOf).ToList());
            var relationship = new StringBuilder($"builder.HasOne(x => x.{navigation}).WithMany().HasForeignKey({keys})");
            var principalProperties = fk.ToColumns.Select(c => mapping.PropertyOf(fk.To, c)?.Name).ToList();
            if (principalProperties.All(p => p is not null) && !principalProperties.SequenceEqual(principal.Entity.PrimaryKey, StringComparer.Ordinal))
            {
                relationship.Append($".HasPrincipalKey({Lambda(principalProperties!)})");
            }

            navigations.Add((declaration, relationship.Append(';').ToString()));
        }

        foreach (var fk in declared.Where(f => mapping.EntityOf(f.To) is null))
        {
            notes.Add(TextFormat.Format(ClrModelText.EntityForeignKeyUnmapped, fk.Name, fk.To.Name));
        }

        // The class.
        var entity = new StringBuilder($"public class {name}\n{{\n");
        foreach (var property in properties)
        {
            entity.Append($"    {property.Declaration}\n");
        }

        if (navigations.Count > 0)
        {
            entity.Append('\n');
            foreach (var (declaration, _) in navigations)
            {
                entity.Append($"    {declaration}\n");
            }
        }

        entity.Append('}');

        // The configuration.
        var lines = new List<string>();
        var isView = table.Table.Kind != TableKind.Table;
        if (!UsesConvention(model) || PropertyCode.UpperSnake(name) != table.Table.Name)
        {
            lines.Add(isView ? $"builder.ToView({CSharpCode.String(table.Table.Name)});" : $"builder.ToTable({CSharpCode.String(table.Table.Name)});");
        }

        if (table.PrimaryKey.Count > 0)
        {
            lines.Add($"builder.HasKey({Lambda(table.PrimaryKey.Select(PropertyOf).ToList())});");
        }
        else
        {
            lines.Add("builder.HasNoKey();");
            notes.Add(isView ? ClrModelText.EntityViewWithoutKey : ClrModelText.EntityWithoutKey);
        }

        // A property line says something only beyond builder.Property(x => x.Name);
        lines.AddRange(properties.Select(p => p.Configuration).Where(c => !IsBare(c)));
        lines.AddRange(navigations.Select(n => n.Configuration));

        var configuration = new StringBuilder($"public class {name}Configuration : IEntityTypeConfiguration<{name}>\n{{\n");
        configuration.Append($"    public void Configure(EntityTypeBuilder<{name}> builder)\n    {{\n");
        foreach (var line in lines)
        {
            configuration.Append($"        {line}\n");
        }

        configuration.Append("    }\n}");
        notes.Add(TextFormat.Format(ClrModelText.EntityDbSet, name));
        return new EntitySuggestion(name, entity.ToString(), configuration.ToString(), notes);
    }

    /// <summary><c>x =&gt; x.Id</c> or <c>x =&gt; new { x.A, x.B }</c>.</summary>
    private static string Lambda(IReadOnlyList<string> properties) =>
        properties.Count == 1 ? $"x => x.{properties[0]}" : $"x => new {{ {string.Join(", ", properties.Select(p => "x." + p))} }}";

    /// <summary><c>RechnungsadresseId</c> → <c>Rechnungsadresse</c>.</summary>
    private static string NavigationName(string foreignKeyProperty) =>
        foreignKeyProperty.EndsWith("Id", StringComparison.Ordinal) && foreignKeyProperty.Length > 2 ? foreignKeyProperty[..^2] : foreignKeyProperty + "Navigation";

    private static bool IsBare(string configuration) =>
        configuration.StartsWith("builder.Property(x => x.", StringComparison.Ordinal) && configuration.IndexOf(").", StringComparison.Ordinal) < 0;

    private static PropertySuggestion Rename(PropertySuggestion suggestion, string name) => suggestion with
    {
        Name = name,
        // The name stands right before the accessors – the type may have the same name (R4a: "public Status Status").
        Declaration = suggestion.Declaration.Replace($" {suggestion.Name} {{ get;", $" {name} {{ get;", StringComparison.Ordinal),
        Configuration = suggestion.Configuration.Replace($"x.{suggestion.Name})", $"x.{name})", StringComparison.Ordinal),
    };

    /// <summary>Most entities of the project have the table <see cref="PropertyCode.UpperSnake"/> makes of their name: a naming convention.</summary>
    private static bool UsesConvention(ModelExport model)
    {
        var tables = model.Entities.Where(e => e is { Table: not null, IsOwned: false }).ToList();
        return tables.Count > 0 && tables.Count(e => e.Table == PropertyCode.UpperSnake(ClrModelMapping.ShortName(e.ClrType))) >= tables.Count * ConventionShare;
    }
}
