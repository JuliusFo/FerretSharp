namespace FerretSharp.UI.State;

/// <summary>
/// A name that follows its suggestion – the kind and columns of a new constraint, the columns of a new index – until the
/// user types one; clearing the field lets it follow again (WP-23; one rule for both since R4b, before it was written
/// twice).
/// </summary>
public sealed class SuggestedName
{
    public string Value { get; private set; } = "";

    /// <summary>The user typed it: it no longer follows the suggestion.</summary>
    public bool Typed { get; private set; }

    /// <summary>What the user typed; empty follows the suggestion again (from the next change, or when the field is left).</summary>
    public void Type(string? value)
    {
        Value = value ?? "";
        Typed = Value.Length > 0;
    }

    /// <summary>The suggestion – unless the user typed a name.</summary>
    public void Follow(string suggestion)
    {
        if (!Typed)
        {
            Value = suggestion;
        }
    }
}
