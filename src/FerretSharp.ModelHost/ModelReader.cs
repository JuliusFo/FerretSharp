using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using FerretSharp.Core.ClrModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FerretSharp.ModelHost;

/// <summary>The EF Core model as <see cref="ModelExport"/>: what FerretSharp needs, in plain data.</summary>
internal static class ModelReader
{
    public static ModelExport Read(IModel model, Type contextType, string createdBy)
    {
        var efVersion = typeof(DbContext).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
            ?? typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "?";

        var entities = model.GetEntityTypes()
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(ReadEntity)
            .ToList();
        return new ModelExport(ModelExport.CurrentFormatVersion, efVersion, contextType.FullName ?? contextType.Name, createdBy,
            model.GetDefaultSchema(), entities);
    }

    private static EntityExport ReadEntity(IEntityType entity)
    {
        var table = entity.GetTableName();
        var schema = entity.GetSchema();
        var view = entity.GetViewName();
        var viewSchema = entity.GetViewSchema();
        // An entity can be mapped to a table and a view at once: queries use the view, SaveChanges the table. Column names
        // may differ between them, so both are read.
        StoreObjectIdentifier? store = table is not null ? StoreObjectIdentifier.Table(table, schema)
            : view is not null ? StoreObjectIdentifier.View(view, viewSchema)
            : null;
        StoreObjectIdentifier? viewStore = view is not null ? StoreObjectIdentifier.View(view, viewSchema) : null;

        var properties = entity.GetProperties().Select(p => ReadProperty(p, store, viewStore)).ToList();
        var foreignKeys = entity.GetForeignKeys().Select(fk => new ForeignKeyExport(
            fk.Properties.Select(p => p.Name).ToList(),
            fk.PrincipalEntityType.Name,
            fk.PrincipalKey.Properties.Select(p => p.Name).ToList(),
            fk.DependentToPrincipal?.Name,
            fk.PrincipalToDependent?.Name,
            fk.IsUnique)).ToList();

        return new EntityExport(
            entity.Name,
            entity.ClrType.FullName ?? entity.ClrType.Name,
            entity.IsOwned(),
            entity.BaseType?.Name,
            table,
            schema,
            view,
            viewSchema,
            properties,
            entity.FindPrimaryKey()?.Properties.Select(p => p.Name).ToList() ?? [],
            foreignKeys);
    }

    private static PropertyExport ReadProperty(IProperty property, StoreObjectIdentifier? store, StoreObjectIdentifier? viewStore)
    {
        var clrType = property.ClrType;
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
        var userConverter = property.GetValueConverter();
        var mapping = TryGet(property.FindTypeMapping);
        // EF converts enums with a converter of its own even without configuration (EnumToNumberConverter).
        var converter = userConverter ?? mapping?.Converter;

        return new PropertyExport(
            property.Name,
            TypeName(underlying),
            underlying.FullName ?? underlying.Name,
            property.IsNullable,
            property.IsShadowProperty(),
            store is { } s ? property.GetColumnName(s) : property.GetColumnName(),
            TryGet(property.GetColumnType),
            userConverter is null ? null : TypeName(userConverter.GetType()),
            converter is null ? null : TypeName(converter.ProviderClrType),
            underlying.IsEnum && underlying.IsDefined(typeof(FlagsAttribute), false),
            Values(underlying, userConverter, converter),
            viewStore is { } v ? property.GetColumnName(v) : null);
    }

    /// <summary>Every enum member (and true/false of a converted bool) with the value the project stores for it.</summary>
    private static List<ValueMapping>? Values(Type type, ValueConverter? userConverter, ValueConverter? converter)
    {
        if (type.IsEnum)
        {
            var integral = Enum.GetUnderlyingType(type);
            return Enum.GetValues(type).Cast<object>()
                .Select(value => Enum.GetName(type, value) ?? value.ToString()!)
                .Select(name => (Name: name, Value: Enum.Parse(type, name)))
                .Select(member => new ValueMapping(
                    member.Name,
                    Invariant(Convert.ChangeType(member.Value, integral, CultureInfo.InvariantCulture)) ?? "",
                    Provider(converter, member.Value, () => Convert.ChangeType(member.Value, integral, CultureInfo.InvariantCulture)),
                    DisplayName(type, member.Name)))
                .ToList();
        }

        if (type == typeof(bool) && userConverter is not null)
        {
            return [new ValueMapping("false", "False", Provider(userConverter, false, () => false)),
                    new ValueMapping("true", "True", Provider(userConverter, true, () => true))];
        }

        return null;
    }

    /// <summary>
    /// <c>[Display(Name = …)]</c> of an enum member; with <c>ResourceType</c> the text comes from the project's resources
    /// in the current UI culture (satellite assemblies included). Null without the attribute or if the resource is missing.
    /// </summary>
    private static string? DisplayName(Type type, string member)
    {
        try
        {
            var text = type.GetField(member, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<DisplayAttribute>()?.GetName();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            return null; // ResourceType without that property, resources not loadable: the member name stays
        }
    }

    private static string? Provider(ValueConverter? converter, object value, Func<object> fallback)
    {
        try
        {
            return Invariant(converter is null ? fallback() : converter.ConvertToProvider(value));
        }
        catch (Exception)
        {
            return null; // a converter that cannot convert this value: shown as unknown, not fatal
        }
    }

    private static string? Invariant(object? value) => value switch
    {
        null => null,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static T? TryGet<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>C# spelling: keywords for built-in types, generic arguments, arrays.</summary>
    internal static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return TypeName(inner) + "?";
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }

        if (Keywords.TryGetValue(type, out var keyword))
        {
            return keyword;
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(short)] = "short",
        [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long",
        [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal",
        [typeof(char)] = "char", [typeof(string)] = "string", [typeof(object)] = "object",
    };
}
