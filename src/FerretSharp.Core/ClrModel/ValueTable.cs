using System.Globalization;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>A member of an enum (or true/false of a converted bool) to pick in the filter or the cell editor.</summary>
/// <param name="Name">The name shown: the member's <c>[Display]</c> text if it has one (<c>Fertigungsauftrag</c>), else the C# name (<c>Gewerbe</c>, <c>true</c>).</param>
/// <param name="Value">The database value as filter and edit text (invariant numbers, texts as stored): <c>2</c>, <c>J</c>.</param>
/// <param name="Label">Shown in lists: <c>Gewerbe (2)</c>, <c>true</c>.</param>
/// <param name="Member">The C# member name if <paramref name="Name"/> is a display text (<c>ProductionOrder</c>); null otherwise.</param>
public sealed record ValueOption(string Name, string Value, string Label, string? Member = null);

/// <summary>A cell value as the C# model sees it.</summary>
/// <param name="Text">Display text; null for NULL.</param>
/// <param name="Unknown">The value is no member of the enum (or neither true nor false): shown raw and marked.</param>
/// <param name="Tooltip">More about the value, e.g. the C# member behind a display text (<c>AuftragStatus.ProductionOrder</c>).</param>
public sealed record PresentedValue(string? Text, bool Unknown = false, string? Tooltip = null);

/// <summary>
/// The values of an enum or converted bool property (<see cref="PropertyExport.Values"/>, computed with the project's own
/// converters) by what the database stores: "2" → <c>Gewerbe</c>, 'J' → <c>true</c>. Numbers compare by value (2 = 2.0),
/// CHAR values without their blank padding.
/// </summary>
public sealed class ValueTable
{
    private readonly ColumnInfo _column;
    private readonly ColumnCategory _category;
    private readonly Dictionary<object, ValueMapping> _byValue = [];
    private readonly Dictionary<string, ValueOption> _byMember = new(StringComparer.Ordinal);
    private readonly List<(long Value, string Name)>? _flags;

    private ValueTable(PropertyExport property, ColumnInfo column)
    {
        Property = property;
        _column = column;
        _category = ColumnCategories.Of(column);
        var options = new List<ValueOption>();
        foreach (var mapping in property.Values ?? [])
        {
            if (mapping.ProviderValue is not { } provider || KeyOf(provider) is not { } key || !_byValue.TryAdd(key, mapping))
            {
                continue; // a member stored as NULL, a value the column cannot hold, or an alias of another member
            }

            var option = new ValueOption(Shown(mapping), ValueText(key), IsBool ? Shown(mapping) : $"{Shown(mapping)} ({Format(key)})",
                mapping.DisplayName is null ? null : mapping.Name);
            options.Add(option);
            _byMember.TryAdd(mapping.Name, option);
        }

        Options = options;
        _flags = FlagsOf(property);
    }

    public PropertyExport Property { get; }

    /// <summary>A converted bool (true/false) rather than an enum.</summary>
    public bool IsBool => Property.ClrType == "bool";

    /// <summary>Members in declaration order (by value for enums, as <c>Enum.GetValues</c> returns them).</summary>
    public IReadOnlyList<ValueOption> Options { get; }

    /// <summary>A table for the property's values, or null if it has none (no enum, bool without converter).</summary>
    public static ValueTable? For(PropertyExport? property, ColumnInfo column)
    {
        if (property?.Values is not { Count: > 0 })
        {
            return null;
        }

        var table = new ValueTable(property, column);
        return table.Options.Count > 0 ? table : null;
    }

    /// <summary>The member a raw database value stands for; null for NULL or a value without a member.</summary>
    public ValueMapping? Find(object? value) => KeyOf(value) is { } key ? _byValue.GetValueOrDefault(key) : null;

    /// <summary>The option for a filter or edit text (as written by <see cref="ValueOption.Value"/> or typed).</summary>
    public ValueOption? FindOption(string text)
    {
        object? key = _category == ColumnCategory.Number
            ? FilterRules.TryParseNumber(text, out var number) ? number : null
            : KeyOf(text);
        return key is null || !_byValue.TryGetValue(key, out var mapping) ? null : _byMember.GetValueOrDefault(mapping.Name);
    }

    /// <summary>The option of a raw database value (to preselect it in the cell editor); null without a member.</summary>
    public ValueOption? OptionOf(object? value) => Find(value) is { } mapping ? _byMember.GetValueOrDefault(mapping.Name) : null;

    /// <summary>
    /// <c>Gewerbe (2)</c> for an enum, <c>true</c>/<c>false</c> for a bool; flags combinations as <c>Lesen | Schreiben (3)</c>.
    /// A value without a member stays as the database has it and is marked unknown.
    /// </summary>
    public PresentedValue Present(object? value)
    {
        var raw = CellFormatter.Format(_column, value);
        if (raw is null)
        {
            return new PresentedValue(null);
        }

        if (Find(value) is { } mapping)
        {
            return new PresentedValue(IsBool ? Shown(mapping) : $"{Shown(mapping)} ({raw})",
                Tooltip: mapping.DisplayName is null ? null : $"{Property.ClrType}.{mapping.Name}");
        }

        return FlagNames(value) is { } names ? new PresentedValue($"{names} ({raw})") : new PresentedValue(raw, Unknown: true);
    }

    /// <summary>The member's display text (<c>[Display(Name = …)]</c>) if it has one, else its C# name.</summary>
    private static string Shown(ValueMapping mapping) => mapping.DisplayName ?? mapping.Name;

    /// <summary>Why a value is marked: for the tooltip of an unknown cell.</summary>
    public string UnknownText => IsBool
        ? $"Weder true noch false für {Property.Converter ?? "den Converter"}"
        : $"Kein Wert von {Property.ClrType}";

    /// <summary>Lookup key: decimal for numbers, the text (CHAR without padding) otherwise.</summary>
    private object? KeyOf(object? value) => value switch
    {
        null or DBNull => null,
        decimal d => d,
        int or long or short or byte or sbyte or ushort or uint => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        BigNumber big => decimal.TryParse(big.Invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : big.Invariant,
        string s when _category == ColumnCategory.Number =>
            decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        string s when _column.DataType is "CHAR" or "NCHAR" => s.TrimEnd(' '),
        string s => s,
        bool b => b ? 1m : 0m,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string ValueText(object key) => key is decimal d ? d.ToString(CultureInfo.InvariantCulture) : (string)key;

    private string Format(object key) => CellFormatter.Format(_column, key) ?? "";

    /// <summary>Members of a flags enum stored as its number (EF's default), to name combinations.</summary>
    private static List<(long Value, string Name)>? FlagsOf(PropertyExport property)
    {
        if (!property.IsFlagsEnum || property.Values is not { } values)
        {
            return null;
        }

        var flags = new List<(long, string)>();
        foreach (var mapping in values)
        {
            if (!long.TryParse(mapping.ClrValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clr)
                || mapping.ProviderValue is not { } provider
                || !decimal.TryParse(provider, NumberStyles.Float, CultureInfo.InvariantCulture, out var stored)
                || stored != clr)
            {
                return null; // stored some other way: combinations cannot be read
            }

            if (clr != 0)
            {
                flags.Add((clr, Shown(mapping)));
            }
        }

        return flags.OrderByDescending(f => f.Item1).ToList();
    }

    private string? FlagNames(object? value)
    {
        if (_flags is null || KeyOf(value) is not decimal d || d != decimal.Truncate(d) || d <= 0 || d > long.MaxValue)
        {
            return null;
        }

        var rest = (long)d;
        var names = new List<string>();
        foreach (var (flag, name) in _flags)
        {
            if ((rest & flag) == flag)
            {
                names.Add(name);
                rest &= ~flag;
            }
        }

        return rest == 0 && names.Count > 0 ? string.Join(" | ", Enumerable.Reverse(names)) : null;
    }
}
