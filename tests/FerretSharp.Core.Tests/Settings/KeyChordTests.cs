using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Settings;

public sealed class KeyChordTests
{
    [Theory]
    [InlineData("ctrl+shift+o", "Ctrl+Shift+O", "Ctrl Shift O")]
    [InlineData("ctrl+enter", "Ctrl+Enter", "Ctrl ↵")]
    [InlineData("alt+arrowleft", "Alt+←", "Alt ←")]
    [InlineData("f5", "F5", "F5")]
    [InlineData("ctrl+space", "Ctrl+Leertaste", "Ctrl Leertaste")]
    [InlineData("ctrl+plus", "Ctrl++", "Ctrl +")]
    [InlineData("SHIFT+CTRL+O", "Ctrl+Shift+O", "Ctrl Shift O")] // any order and case in, one form out
    public void Parses_and_labels(string text, string label, string kbd)
    {
        var chord = KeyChord.Parse(text)!;

        Assert.Equal(label, chord.Label);
        Assert.Equal(kbd, chord.KbdLabel);
        Assert.Equal(chord, KeyChord.Parse(chord.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+")]
    [InlineData("ctrl+shift")] // only modifiers
    [InlineData("ctrl+ctrl+o")]
    [InlineData("hyper+o")]
    [InlineData("ctrl++")]
    public void Refuses_what_is_no_key_combination(string text) => Assert.Null(KeyChord.Parse(text));

    [Theory]
    [InlineData(" ", "space")]
    [InlineData("+", "plus")]
    [InlineData("O", "o")]
    [InlineData("ArrowLeft", "arrowleft")]
    public void Takes_keys_from_events_like_shortcuts_js(string key, string expected) =>
        Assert.Equal(expected, KeyChord.FromEvent(key, true, false, false)!.Key);

    [Fact]
    public void A_modifier_alone_is_no_chord() => Assert.Null(KeyChord.FromEvent("Control", true, false, false));
}
