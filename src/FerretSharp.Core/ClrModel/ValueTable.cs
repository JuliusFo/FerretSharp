using System.Globalization;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
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
    private readonly List<(long Value, ValueMapping Mapping)>? _flags;
    private readonly List<(long Value, ValueMapping Mapping)>? _singleFlags;

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
        _flags = _category == ColumnCategory.Number ? FlagsOf(property) : null;
        if (_flags is not null)
        {
            _singleFlags = SingleFlags(_flags);
            Flags = _singleFlags.Select(f => _byMember[f.Mapping.Name]).ToList();
        }
    }

    public PropertyExport Property { get; }

    /// <summary>A converted bool (true/false) rather than an enum.</summary>
    public bool IsBool => Property.ClrType == "bool";

    /// <summary>Members in declaration order (by value for enums, as <c>Enum.GetValues</c> returns them).</summary>
    public IReadOnlyList<ValueOption> Options { get; }

    /// <summary>
    /// A flags enum stored as its number: the single flags to tick (members that are no combination of other members),
    /// by value; null for other enums and bools. Their <see cref="ValueOption.Value"/> is the flag's number.
    /// </summary>
    public IReadOnlyList<ValueOption>? Flags { get; }

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
    /// <c>Gewerbe (2)</c> for an enum, <c>true</c>/<c>false</c> for a bool; a flags enum lists every flag set
    /// (<c>Lesen, Schreiben (3)</c>), also for a value that has a member of its own. A value without a member stays as the
    /// database has it and is marked unknown.
    /// </summary>
    public PresentedValue Present(object? value)
    {
        var raw = CellFormatter.Format(_column, value);
        if (raw is null)
        {
            return new PresentedValue(null);
        }

        if (_singleFlags is not null && FlagsSet(value) is { } set)
        {
            return PresentFlags(set, raw);
        }

        if (Find(value) is { } mapping)
        {
            return new PresentedValue(IsBool ? Shown(mapping) : $"{Shown(mapping)} ({raw})",
                Tooltip: mapping.DisplayName is null ? null : $"{Property.ClrType}.{mapping.Name}");
        }

        return new PresentedValue(raw, Unknown: true);
    }

    /// <summary>
    /// The flags set in a value of a flags enum (single flags, smallest first) and the bits no member stands for; null if
    /// the value is no whole number ≥ 0 or the enum is no flags enum stored as its number.
    /// </summary>
    public FlagsValue? FlagsSet(object? value)
    {
        if (_singleFlags is null || KeyOf(value) is not decimal d || d != decimal.Truncate(d) || d < 0 || d > long.MaxValue)
        {
            return null;
        }

        var bits = (long)d;
        var set = _singleFlags.Where(f => (bits & f.Value) == f.Value).ToList();
        var known = set.Aggregate(0L, (all, f) => all | f.Value);
        return new FlagsValue(bits, set.Select(f => _byMember[f.Mapping.Name]).ToList(), bits & ~known);
    }

    /// <summary>The flags of an edit text (<c>3</c>, also <c>3,0</c>), to tick them in the editor; null if it is no flags value.</summary>
    public FlagsValue? FlagsOfText(string text) =>
        FilterRules.TryParseNumber(text, out var number) ? FlagsSet(number) : null;

    /// <summary>The edit text of a flags value: the number, invariant.</summary>
    public static string FlagsText(long bits) => bits.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// What the flags editor (grid and form) shows for an edit text: a box per single flag, ticked if set, then the bits no
    /// member stands for as a ticked box of their own, and NULL where <paramref name="nullAllowed"/>. Null if the enum is
    /// no flags enum stored as its number.
    /// </summary>
    public FlagsEdit? FlagsEditOf(string text, bool nullAllowed)
    {
        if (Flags is null)
        {
            return null;
        }

        var set = text.Length == 0 ? null : FlagsOfText(text);
        var boxes = Flags.Select(f => new FlagBox(f.Value, f.Label, f.Name, set?.Flags.Contains(f) == true)).ToList();
        if (set is { Rest: not 0 and var rest })
        {
            var bits = FlagsText(rest);
            boxes.Add(new FlagBox(bits, TextFormat.Format(ClrModelText.FlagWithoutMember, bits), bits, Checked: true, Unknown: true));
        }

        var none = nullAllowed ? new FlagBox("", "NULL", "NULL", Checked: text.Length == 0) : null;
        return new FlagsEdit(text, none, boxes, text.Length > 0 && set is null ? text : null);
    }

    /// <summary>
    /// An edit text with a flag (its number as <see cref="ValueOption.Value"/>) ticked or not; NULL or a text that is no
    /// flags value starts from no flag.
    /// </summary>
    public string WithFlag(string text, string flag, bool set)
    {
        var bits = FlagsOfText(text)?.Bits ?? 0;
        var value = long.Parse(flag, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return FlagsText(set ? bits | value : bits & ~value);
    }

    private PresentedValue PresentFlags(FlagsValue set, string raw)
    {
        if (set.Bits == 0)
        {
            // No flag set: the member for 0 if there is one (Keine), else just the number – a valid value of a flags enum.
            return Find(0m) is { } none
                ? new PresentedValue($"{Shown(none)} ({raw})", Tooltip: none.DisplayName is null ? null : $"{Property.ClrType}.{none.Name}")
                : new PresentedValue(raw);
        }

        if (set.Flags.Count == 0)
        {
            return new PresentedValue(raw, Unknown: true);
        }

        var names = set.Flags.Select(f => f.Name).ToList();
        if (set.Rest != 0)
        {
            names.Add(FlagsText(set.Rest));
        }

        var lines = set.Flags.Select(f => f.Member is null ? f.Label : $"{f.Label} · {Property.ClrType}.{f.Member}").ToList();
        if (set.Rest != 0)
        {
            lines.Add(TextFormat.Format(ClrModelText.FlagWithoutMember, FlagsText(set.Rest)));
        }

        return new PresentedValue($"{string.Join(", ", names)} ({raw})", Unknown: set.Rest != 0, Tooltip: string.Join("\n", lines));
    }

    /// <summary>
    /// The C# members of a flags combination (<c>Lesen</c>, <c>Schreiben</c> for 3), smallest first; null if the value is
    /// no combination of members (or the enum is no flags enum stored as its number).
    /// </summary>
    public IReadOnlyList<string>? FlagMembers(object? value) => CombinedMembers(value)?.Select(m => m.Name).ToList();

    /// <summary>
    /// The enum is stored as its number (no converter of the project): every number is a valid C# value then, also one
    /// without a member (<c>(Kundenart)7</c>).
    /// </summary>
    public bool StoredAsNumber => !IsBool && Property.Converter is null && _category == ColumnCategory.Number;

    /// <summary>The member's display text (<c>[Display(Name = …)]</c>) if it has one, else its C# name.</summary>
    private static string Shown(ValueMapping mapping) => mapping.DisplayName ?? mapping.Name;

    /// <summary>Why a value is marked: for the tooltip of an unknown cell.</summary>
    public string UnknownText => IsBool
        ? Property.Converter is { } converter
            ? TextFormat.Format(ClrModelText.UnknownNotTrueOrFalse, converter)
            : ClrModelText.UnknownNotTrueOrFalseConverter
        : TextFormat.Format(ClrModelText.UnknownNotMember, Property.ClrType);

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
    private static List<(long Value, ValueMapping Mapping)>? FlagsOf(PropertyExport property)
    {
        if (!property.IsFlagsEnum || property.Values is not { } values)
        {
            return null;
        }

        var flags = new List<(long, ValueMapping)>();
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
                flags.Add((clr, mapping));
            }
        }

        return flags.OrderByDescending(f => f.Item1).ToList();
    }

    /// <summary>
    /// The members that are no combination of other members (<c>Lesen</c>, <c>Schreiben</c>, not <c>LesenUndSchreiben</c>):
    /// the flags to tick, smallest first. Aliases (the same value) count once.
    /// </summary>
    private static List<(long Value, ValueMapping Mapping)> SingleFlags(List<(long Value, ValueMapping Mapping)> flags)
    {
        var distinct = flags.DistinctBy(f => f.Value).ToList();
        return distinct
            .Where(f => distinct.Where(o => o.Value != f.Value && (o.Value & f.Value) == o.Value).Aggregate(0L, (all, o) => all | o.Value) != f.Value)
            .OrderBy(f => f.Value)
            .ToList();
    }

    /// <summary>The members a flags value combines, smallest first; null if it is no combination of members.</summary>
    private List<ValueMapping>? CombinedMembers(object? value)
    {
        if (_flags is null || KeyOf(value) is not decimal d || d != decimal.Truncate(d) || d <= 0 || d > long.MaxValue)
        {
            return null;
        }

        var rest = (long)d;
        var members = new List<ValueMapping>();
        foreach (var (flag, mapping) in _flags)
        {
            if ((rest & flag) == flag)
            {
                members.Add(mapping);
                rest &= ~flag;
            }
        }

        members.Reverse();
        return rest == 0 && members.Count > 0 ? members : null;
    }
}

/// <summary>A value of a flags enum taken apart.</summary>
/// <param name="Bits">The whole value.</param>
/// <param name="Flags">The single flags set in it, smallest first.</param>
/// <param name="Rest">Bits no member stands for (0 if none).</param>
public sealed record FlagsValue(long Bits, IReadOnlyList<ValueOption> Flags, long Rest);

/// <summary>
/// The flags editor for an edit text (<see cref="ValueTable.FlagsEditOf"/>): grid and form only show it. The value is the
/// sum of the ticked boxes' values, or NULL.
/// </summary>
/// <param name="Text">The edit text it was made from (the value if nothing is ticked differently).</param>
/// <param name="Null">The NULL box; null where NULL cannot be chosen.</param>
/// <param name="Boxes">The single flags, smallest first, then the bits without a member if the value has some.</param>
/// <param name="Raw">An edit text that is no flags value (kept until a box is ticked); null otherwise.</param>
public sealed record FlagsEdit(string Text, FlagBox? Null, IReadOnlyList<FlagBox> Boxes, string? Raw);

/// <summary>A check box of the flags editor.</summary>
/// <param name="Value">What it adds to the sum: the number, invariant (<c>4</c>); empty for NULL.</param>
/// <param name="Label">Beside the box: <c>Zahlt per Lastschrift (4)</c>, <c>16 (no member)</c>.</param>
/// <param name="Name">In the editor's summary: <c>Zahlt per Lastschrift</c>, <c>16</c>.</param>
/// <param name="Checked">Ticked for the edit text.</param>
/// <param name="Unknown">Bits no member stands for (marked).</param>
public sealed record FlagBox(string Value, string Label, string Name, bool Checked, bool Unknown = false);
