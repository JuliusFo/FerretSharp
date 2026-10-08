namespace FerretSharp.Core.Settings;

/// <summary>
/// A key combination as the WebView reports it (WP-25): modifiers plus <c>KeyboardEvent.key</c> in lower case. Its text
/// form <c>ctrl+shift+o</c> is what <c>shortcuts.js</c> builds from a key event (same order, <c>space</c> and <c>plus</c>
/// for the keys that would clash with the format) and what <c>settings.json</c> stores.
/// </summary>
/// <remarks>
/// The key is the character of the keyboard layout, not the physical key: on a German keyboard Ctrl+Shift+7 arrives as
/// <c>ctrl+shift+/</c>. Recording and matching both use it, so they agree; only the label may look unusual.
/// </remarks>
public sealed record KeyChord(bool Ctrl, bool Shift, bool Alt, string Key)
{
    private static readonly HashSet<string> ModifierKeys = ["control", "shift", "alt", "meta", "altgraph", "os"];

    /// <summary>A key alone, without Ctrl or Alt, would get in the way of typing – except the function keys.</summary>
    public bool IsFunctionKey => Key.Length is 2 or 3 && Key[0] == 'f' && int.TryParse(Key.AsSpan(1), out var n) && n is >= 1 and <= 24;

    /// <summary>The text form, e.g. <c>ctrl+shift+o</c>.</summary>
    public string Text => string.Concat(Ctrl ? "ctrl+" : "", Shift ? "shift+" : "", Alt ? "alt+" : "", Key);

    /// <summary>For tooltips and messages: <c>Ctrl+Shift+O</c>, <c>Alt+←</c>, <c>F5</c>.</summary>
    public string Label => string.Join("+", Parts());

    /// <summary>For <c>&lt;kbd&gt;</c>: <c>Ctrl Shift O</c>, <c>Ctrl ↵</c>.</summary>
    public string KbdLabel => string.Join(" ", Parts().Select(p => p == "Enter" ? "↵" : p));

    public override string ToString() => Text;

    /// <summary>The chord of a text form; null if it is empty, malformed or only modifiers.</summary>
    public static KeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().ToLowerInvariant().Split('+');
        var key = parts[^1];
        if (key.Length == 0 || key.Contains(' ') || ModifierKeys.Contains(key))
        {
            return null;
        }

        bool ctrl = false, shift = false, alt = false;
        foreach (var modifier in parts[..^1])
        {
            switch (modifier)
            {
                case "ctrl" when !ctrl:
                    ctrl = true;
                    break;
                case "shift" when !shift:
                    shift = true;
                    break;
                case "alt" when !alt:
                    alt = true;
                    break;
                default:
                    return null;
            }
        }

        return new KeyChord(ctrl, shift, alt, key);
    }

    /// <summary>The chord of a key event as Blazor reports it (<c>KeyboardEventArgs</c>), normalized like <c>shortcuts.js</c>.</summary>
    public static KeyChord? FromEvent(string key, bool ctrl, bool shift, bool alt) =>
        key.Length == 0 || ModifierKeys.Contains(key.ToLowerInvariant()) ? null : new KeyChord(ctrl, shift, alt, NormalizeKey(key));

    /// <summary><c>KeyboardEvent.key</c> as the text form uses it (keep in step with <c>keyOf</c> in shortcuts.js).</summary>
    public static string NormalizeKey(string key) => key switch
    {
        " " => "space",
        "+" => "plus",
        _ => key.ToLowerInvariant(),
    };

    private IEnumerable<string> Parts()
    {
        if (Ctrl)
        {
            yield return "Ctrl";
        }

        if (Shift)
        {
            yield return "Shift";
        }

        if (Alt)
        {
            yield return "Alt";
        }

        yield return KeyLabel(Key);
    }

    private static string KeyLabel(string key) => key switch
    {
        "arrowleft" => "←",
        "arrowright" => "→",
        "arrowup" => "↑",
        "arrowdown" => "↓",
        "enter" => "Enter",
        "escape" => "Esc",
        "delete" => "Entf",
        "backspace" => "Rücktaste",
        "insert" => "Einfg",
        "home" => "Pos1",
        "end" => "Ende",
        "pageup" => "Bild↑",
        "pagedown" => "Bild↓",
        "tab" => "Tab",
        "space" => "Leertaste",
        "plus" => "+",
        _ when key.Length == 1 => key.ToUpperInvariant(),
        _ when key.Length is 2 or 3 && key[0] == 'f' && char.IsAsciiDigit(key[1]) => key.ToUpperInvariant(),
        _ => char.ToUpperInvariant(key[0]) + key[1..],
    };
}
