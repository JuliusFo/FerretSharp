using FerretSharp.Core.ClrModel;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>One entry of the value picker: <paramref name="Value"/> is the database value the filter gets.</summary>
public sealed record ValuePickerItem(string Name, string Value, string? Detail, bool Unknown);

/// <summary>
/// The entries of the value picker of an enum or converted bool column in the filter bar (WP-12): the members, then the
/// selected values without a member (typed before the model was loaded, from an FK jump). Typed text filters them like a
/// menu (<see cref="MenuFilter"/>: each word in the name, the C# member or the value). A typed value the column can hold
/// that no member has comes first as an entry of its own, so a value the C# model does not know can be filtered for.
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

        var shown = all.Where(i => filter.Matches(i.Name, i.Unknown ? i.Value : i.Detail)).ToList();
        if (values.TypedValue(typed) is { } value && values.FindOption(typed) is null && all.All(i => i.Value != value)
            && !values.Options.Any(o => Names(o, typed.Trim())))
        {
            shown.Insert(0, Unknown(value));
        }

        return shown;
    }

    private static ValuePickerItem Unknown(string value) => new(value, value, GridText.ValuePicker_NoMember, true);

    /// <summary>The text is a member's name (<c>offen</c> for <c>Offen</c> stored as <c>OFFEN</c>): it means the member, no other value.</summary>
    private static bool Names(ValueOption option, string text) =>
        string.Equals(option.Name, text, StringComparison.CurrentCultureIgnoreCase)
        || string.Equals(option.Member, text, StringComparison.CurrentCultureIgnoreCase);
}
