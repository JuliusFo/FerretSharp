using FerretSharp.Core.ClrModel;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>One entry of the value picker: <paramref name="Value"/> is the database value the filter gets.</summary>
public sealed record ValuePickerItem(string Name, string Value, string? Detail, bool Unknown);

/// <summary>
/// The entries of the value picker of an enum or converted bool column in the filter bar (WP-12): the members, then the
/// selected values without a member (typed before the model was loaded, from an FK jump). Typed text filters them like a
/// menu (<see cref="MenuFilter"/>: each word in the name, the C# member or the value). The entry the text stands for comes
/// first, also when written another way. A typed value the column can hold that is no entry becomes an entry of its own, so a
/// value the C# model does not know can be filtered for: first in a number column, after the members found in a text column
/// (there the text is mostly a search, and Enter takes the first entry).
/// </summary>
public static class ValuePickerItems
{
    public static IReadOnlyList<ValuePickerItem> Of(ValueTable values, IReadOnlyList<string> selected, string typed)
    {
        // A display text shows its C# member beside the stored value, so it can be found in the code.
        var all = values.Options
            .Select(o => new ValuePickerItem(o.Name, o.Value, o.Member is { } member ? $"{member} · {o.Value}" : o.Value, false))
            .Concat(selected.Where(v => values.Options.All(o => o.Value != v)).Select(Unknown))
            .ToList();
        var filter = new MenuFilter();
        filter.Set(typed);
        if (!filter.Active)
        {
            return all;
        }

        var text = typed.Trim();
        var shown = all.Where(i => filter.Matches(i.Name, i.Unknown ? i.Value : i.Detail)).ToList();
        var meant = Meant(values, all, text);
        if (meant is not null)
        {
            shown.Remove(meant);
            shown.Insert(0, meant);
        }

        // A value of its own: in no entry, no member's name (offen means Offen), without the separator (an "in" list would
        // split it). A text only differing in case from a member's value stays pickable after the member ("j" beside J).
        if (values.TypedValue(typed) is not { } value || value.Contains(FilterRow.ListSeparator) || all.Any(i => i.Value == value)
            || values.Options.Any(o => Names(o, text)))
        {
            return shown;
        }

        if (!values.HoldsNumbers)
        {
            shown.Add(Unknown(value));
        }
        else if (meant is null)
        {
            shown.Insert(0, Unknown(value));
        }

        return shown;
    }

    private static ValuePickerItem Unknown(string value) => new(value, value, GridText.ValuePicker_NoMember, true);

    /// <summary>
    /// The entry the text stands for: a member by its value (<c>2,0</c> for 2, <c>versandt</c> for <c>VERSANDT</c>) or by its
    /// name (<c>offen</c> for <c>Offen</c> stored as <c>OFFEN</c>), or a selected value without a member; null for none.
    /// </summary>
    private static ValuePickerItem? Meant(ValueTable values, List<ValuePickerItem> all, string text)
    {
        var value = values.FindOption(text)?.Value
            ?? values.Options.FirstOrDefault(o => Names(o, text) || string.Equals(o.Value, text, StringComparison.OrdinalIgnoreCase))?.Value
            ?? values.TypedValue(text);
        return value is null ? null : all.FirstOrDefault(i => i.Value == value);
    }

    private static bool Names(ValueOption option, string text) =>
        string.Equals(option.Name, text, StringComparison.CurrentCultureIgnoreCase)
        || string.Equals(option.Member, text, StringComparison.CurrentCultureIgnoreCase);
}
