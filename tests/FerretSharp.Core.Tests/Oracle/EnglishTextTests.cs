using FerretSharp.Core.Compare;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Oracle;

/// <summary>
/// The English texts of the Oracle layer, the DDL proposal and the shortcuts (WP-29), where code composes them: key names,
/// literal braces, placeholders filled in. The German ones are covered by the older tests (they run in German).
/// </summary>
public sealed class EnglishTextTests
{
    private static readonly ColumnInfo Amount = new("BETRAG", "NUMBER", null, false, 10, 2, Nullable: false, IsIdentity: false, Default: null, Position: 1);

    [Fact]
    public void Key_names_follow_the_UI_language()
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Ctrl+Space", KeyChord.Parse("ctrl+space")!.Label);
            Assert.Equal("Shift+Del", KeyChord.Parse("shift+delete")!.Label);
            Assert.Equal("Alt+PgDn", KeyChord.Parse("alt+pagedown")!.Label);
        }

        Assert.Equal("Ctrl+Leertaste", KeyChord.Parse("ctrl+space")!.Label);
        Assert.Equal("Shift+Entf", KeyChord.Parse("shift+delete")!.Label);
    }

    [Fact]
    public void Shortcut_checks_read_in_English_with_the_literal_brace()
    {
        var map = new ShortcutMap();

        using (UiCulture.Use("en"))
        {
            Assert.Equal("Ctrl+Alt is AltGr on German keyboards (@, €, {) – it would catch characters while typing.",
                map.Check(ShortcutAction.Refresh, KeyChord.Parse("ctrl+alt+r")!).Error);
            Assert.Equal("Ctrl+D is otherwise “Select next match in the editors” – the shortcut wins, that function is lost there.",
                map.Check(ShortcutAction.Refresh, KeyChord.Parse("ctrl+d")!).Warning);
            Assert.Equal("F12 opens the developer tools.", map.Check(ShortcutAction.Refresh, KeyChord.Parse("f12")!).Error);
            Assert.Equal("Data", ShortcutMap.Definition(ShortcutAction.Refresh).Group);
        }

        Assert.Equal("Ctrl+Alt ist auf deutschen Tastaturen AltGr (@, €, {) – das würde Zeichen beim Tippen abfangen.",
            map.Check(ShortcutAction.Refresh, KeyChord.Parse("ctrl+alt+r")!).Error);
        Assert.Equal("Daten", ShortcutMap.Definition(ShortcutAction.Refresh).Group);
    }

    [Fact]
    public void Edit_errors_read_in_English()
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal("“abc” is not a number (e.g. 4711 or 12.5).", OracleTypeMapper.Parse(Amount, "abc").Error);
            Assert.Equal("At most 2 decimal places (NUMBER(10,2)).", OracleTypeMapper.Parse(Amount, "1,234").Error);
            Assert.Equal("BETRAG cannot be empty (NOT NULL).", OracleTypeMapper.Parse(Amount, "").Error);
        }
    }

    [Fact]
    public void The_DDL_script_header_reads_in_English()
    {
        var proposal = new DdlProposal(0, 1, []) { ReferenceOwner = "DEV", TargetOwner = "TEST" };

        using (UiCulture.Use("en"))
        {
            var lines = proposal.Script.ReplaceLineEndings("\n").Split('\n');

            Assert.Equal("-- DDL proposal: align TEST (side 2) with the reference DEV (side 1).", lines[0]);
            Assert.Equal("-- No differences.", lines[2]);
        }
    }
}
