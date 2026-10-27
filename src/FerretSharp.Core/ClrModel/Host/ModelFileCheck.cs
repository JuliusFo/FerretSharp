using FerretSharp.Core.Resources;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// What a model file may hold (WP-64, review): it comes from someone else, and its names end up in generated C# – object
/// initializers, LINQ filters, entity code, comments – that the user copies into a project. From the model host they were
/// compiled names; from a file they are text. So property, navigation and enum member names must be C# identifiers, type
/// names only identifiers with the punctuation of a type (no parentheses, quotes, operators or semicolons), and no text
/// may hold a control character (a line break would leave a <c>//</c> comment). Required fields must be there – a missing
/// list would otherwise fail later with a NullReferenceException instead of a reason.
/// </summary>
internal static class ModelFileCheck
{
    private const int MaxShown = 60;

    /// <summary>Why the model cannot be passed on or read; null if it is fine.</summary>
    public static string? Problem(ModelExport model, ModelFileOrigin? origin)
    {
        if (model.Entities is null || model.EfVersion is null || model.ContextType is null || model.CreatedBy is null
            || origin is { Project: null } or { Configuration: null } or { AppVersion: null })
        {
            return ClrModelText.ModelFileIncomplete;
        }

        var problem = TypeName(model.ContextType, "DbContext") ?? Text(model.DefaultSchema, "DbContext") ?? Text(model.Culture, "DbContext")
                      ?? Text(model.EfVersion, "DbContext") ?? Text(model.CreatedBy, "DbContext");
        if (origin is not null)
        {
            problem ??= Text(origin.Project, origin.Project) ?? Text(origin.Configuration, origin.Project) ?? Text(origin.ContextType, origin.Project)
                        ?? Text(origin.Branch, origin.Project) ?? Text(origin.Commit, origin.Project) ?? Text(origin.AppVersion, origin.Project);
        }

        foreach (var entity in model.Entities)
        {
            problem ??= Entity(entity);
        }

        return problem;
    }

    private static string? Entity(EntityExport? entity)
    {
        if (entity is not { Name: not null, ClrType: not null, Properties: not null, PrimaryKey: not null, ForeignKeys: not null })
        {
            return ClrModelText.ModelFileIncomplete;
        }

        var at = entity.Name;
        var problem = Text(entity.Name, at) ?? TypeName(entity.ClrType, at) ?? TypeName(entity.BaseType, at) ?? Identifier(entity.DbSet, at)
                      ?? Text(entity.Table, at) ?? Text(entity.Schema, at) ?? Text(entity.View, at) ?? Text(entity.ViewSchema, at);
        foreach (var key in entity.PrimaryKey)
        {
            problem ??= Identifier(key ?? "", at);
        }

        foreach (var property in entity.Properties)
        {
            problem ??= Property(property, at);
        }

        foreach (var key in entity.ForeignKeys)
        {
            problem ??= key is not { Properties: not null, PrincipalEntity: not null, PrincipalProperties: not null }
                ? ClrModelText.ModelFileIncomplete
                : key.Properties.Concat(key.PrincipalProperties).Select(p => Identifier(p ?? "", at)).FirstOrDefault(p => p is not null)
                  ?? Text(key.PrincipalEntity, at) ?? Identifier(key.Navigation, at) ?? Identifier(key.InverseNavigation, at);
        }

        return problem;
    }

    private static string? Property(PropertyExport? property, string entity)
    {
        if (property is not { Name: not null, ClrType: not null })
        {
            return ClrModelText.ModelFileIncomplete;
        }

        var at = $"{entity}.{property.Name}";
        var problem = Identifier(property.Name, at) ?? TypeName(property.ClrType, at) ?? TypeName(property.Converter, at) ?? TypeName(property.ProviderClrType, at)
                      ?? Text(property.Column, at) ?? Text(property.ViewColumn, at) ?? Text(property.ColumnType, at);
        foreach (var value in property.Values ?? [])
        {
            problem ??= value is not { Name: not null, ClrValue: not null }
                ? ClrModelText.ModelFileIncomplete
                : Identifier(value.Name, at) ?? Text(value.ClrValue, at) ?? Text(value.ProviderValue, at) ?? Text(value.DisplayName, at);
        }

        return problem;
    }

    /// <summary>A C# identifier (<c>KundeId</c>, <c>@class</c>); null (fine) if there is none.</summary>
    private static string? Identifier(string? name, string at) =>
        name is null || IsIdentifier(name.StartsWith('@') ? name[1..] : name) ? null : Invalid(name, at);

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// A type as the model host writes it: <c>int</c>, <c>Shop.Entities.Kunde</c>, <c>Outer+Inner</c>,
    /// <c>ValueConverter&lt;bool, string&gt;</c>, <c>byte[]</c>, <c>List`1</c>; null (fine) if there is none.
    /// </summary>
    private static string? TypeName(string? name, string at) =>
        name is null || name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '+' or '<' or '>' or ',' or ' ' or '[' or ']' or '?' or '`' or '@')
            ? null
            : Invalid(name, at);

    /// <summary>Any text without control characters (names of tables and columns, display names, stored values).</summary>
    private static string? Text(string? text, string at) => text is null || !text.Any(char.IsControl) ? null : Invalid(text, at);

    private static string Invalid(string value, string at) => TextFormat.Format(ClrModelText.ModelFileInvalidName, Shown(value), Shown(at));

    /// <summary>The value as the message shows it: control characters visible, long values cut.</summary>
    private static string Shown(string value) =>
        new string(value.Select(c => char.IsControl(c) ? '·' : c).Take(MaxShown).ToArray()) + (value.Length > MaxShown ? "…" : "");
}
