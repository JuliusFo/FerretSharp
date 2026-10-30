using System.Text.RegularExpressions;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// Keys the settings warn about as the editors' own stay with a Monaco inside a dialog (shortcuts.js, editorKeys): a
/// global shortcut on one of them would otherwise be dead in the LOB editor (review of the LOB editor, finding 6).
/// </summary>
public sealed class DialogEditorKeysTests
{
    [Fact]
    public void Every_key_the_settings_call_an_editor_key_stays_with_a_dialog_editor()
    {
        var script = File.ReadAllText(Path.Combine(Repository.Root, "src", "FerretSharp.UI", "wwwroot", "js", "shortcuts.js"));
        var list = Regex.Match(script, @"const editorKeys = new Set\(\[(.*?)\]\)", RegexOptions.Singleline);
        Assert.True(list.Success, "editorKeys not found in shortcuts.js");
        var editorKeys = Regex.Matches(list.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value).ToHashSet();

        var map = new ShortcutMap();
        var global = ShortcutMap.Definitions.First(d => d.Scope == ShortcutScope.Global).Action;
        string[] keys =
        [
            .. "abcdefghijklmnopqrstuvwxyz0123456789".Select(c => c.ToString()),
            "space", "arrowup", "arrowdown", "arrowleft", "arrowright", "enter", "tab", "delete", "backspace", "home", "end",
            .. Enumerable.Range(1, 12).Select(n => $"f{n}"),
        ];
        var warned = from prefix in new[] { "ctrl+", "ctrl+shift+", "alt+" }
                     from key in keys
                     let combo = prefix + key
                     where KeyChord.Parse(combo) is { } chord && map.Check(global, chord).Warning is not null
                     select combo;

        Assert.Equal<string>([], [.. warned.Where(combo => !editorKeys.Contains(combo))]);
    }
}
