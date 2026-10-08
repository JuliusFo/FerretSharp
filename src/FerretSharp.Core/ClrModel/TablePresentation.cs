using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.ClrModel;

/// <summary>How a column is labelled.</summary>
/// <param name="Name">The main label: the column name, or the property name if C# names come first.</param>
/// <param name="AlternateName">The other name, shown subdued beside it; null without a property (or with C# names off).</param>
/// <param name="ClrType">C# type of the property, e.g. <c>int?</c>, <c>Kundenart</c>; null without one.</param>
public sealed record ColumnLabel(string Name, string? AlternateName, string? ClrType);

/// <summary>
/// How a table, its columns and its values are presented (CLAUDE.md section 2: the exchangeable presentation layer).
/// Without a C# model it is the plain database view; with one (WP-12) it adds entity and property names according to
/// the setting, and shows enum and converted bool values by their C# names. SQL, filters and edits keep database
/// names and values – only what the user sees changes.
/// </summary>
public sealed class TablePresentation
{
    private readonly PropertyExport?[] _properties;
    private readonly ValueTable?[] _values;

    private TablePresentation(TableDetails details, ClrModelMapping? mapping, ClrNameDisplay names)
    {
        Details = details;
        Entity = mapping?.EntityOf(details.Table.Ref);
        Names = Entity is null ? ClrNameDisplay.Off : names;
        // Owned types and table splitting: several entities share the table, each with its own properties.
        _properties = details.Columns.Select(c => Entity is null ? null : mapping!.PropertyOf(details.Table.Ref, c.Name)).ToArray();
        _values = details.Columns.Select((c, i) => ValueTable.For(_properties[i], c)).ToArray();
    }

    public TableDetails Details { get; }

    /// <summary>The entity mapped to the table; null without a model or if none maps it.</summary>
    public EntityMapping? Entity { get; }

    /// <summary>Where C# names appear; <see cref="ClrNameDisplay.Off"/> if the table has no entity.</summary>
    public ClrNameDisplay Names { get; }

    /// <summary>The entity's short name (<c>Kunde</c>); null without one.</summary>
    public string? EntityName => Entity is null ? null : ClrModelMapping.ShortName(Entity.Entity.ClrType);

    /// <summary>Whether column labels carry C# names (the grid header then has a line more).</summary>
    public bool ShowsClrNames => Names != ClrNameDisplay.Off;

    /// <summary>The plain database view of a table.</summary>
    public static TablePresentation Plain(TableDetails details) => new(details, null, ClrNameDisplay.Off);

    public static TablePresentation Create(TableDetails details, ClrModelMapping? mapping, ClrNameDisplay names) =>
        new(details, mapping, names);

    public PropertyExport? PropertyOf(int column) => _properties[column];

    /// <summary>
    /// The column's property belongs to <see cref="Entity"/> itself – not to an owned type or another entity sharing the
    /// table (generated code can only set the entity's own properties).
    /// </summary>
    public bool IsEntityProperty(int column) =>
        _properties[column] is { } property
        && Entity!.Properties.TryGetValue(Details.Columns[column].Name, out var own)
        && ReferenceEquals(own, property);

    /// <summary>Members of an enum or converted bool column; null for other columns.</summary>
    public ValueTable? ValuesOf(int column) => _values[column];

    public ColumnLabel LabelOf(int column)
    {
        var name = Details.Columns[column].Name;
        if (!ShowsClrNames || _properties[column] is not { } property)
        {
            return new ColumnLabel(name, null, null);
        }

        var type = ClrTypeText(property);
        return Names == ClrNameDisplay.Front
            ? new ColumnLabel(property.Name, name, type)
            : new ColumnLabel(name, property.Name, type);
    }

    /// <summary>The property name of a column if C# names are shown (for the column search); null otherwise.</summary>
    public string? SearchNameOf(int column) => ShowsClrNames ? _properties[column]?.Name : null;

    /// <summary>A cell's display text: enum members and bools by name, everything else as <see cref="CellFormatter"/> has it.</summary>
    public PresentedValue Present(int column, object? value) =>
        _values[column] is { } values ? values.Present(value) : new PresentedValue(CellFormatter.Format(Details.Columns[column], value));

    /// <summary>
    /// A filter value for the tab title: the member name for an enum or bool column (<c>Gewerbe</c>), else as entered.
    /// </summary>
    public string FilterValueText(string column, string value)
    {
        var index = Details.IndexOf(column);
        return index >= 0 && _values[index]?.FindOption(value) is { } option ? option.Name : value;
    }

    /// <summary>The C# type as written in the entity: <c>int?</c>, <c>string</c>, <c>Kundenart</c>.</summary>
    public static string ClrTypeText(PropertyExport property) =>
        property.ClrType + (property.Nullable && property.ClrType != "string" && !property.ClrType.EndsWith("[]", StringComparison.Ordinal) ? "?" : "");
}
