using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Texts of the SQL editor and the LINQ console that are composed or were constants before WP-29.</summary>
public sealed class SqlEditorTextTests
{
    [Fact]
    public void The_snapshot_hint_follows_the_UI_language()
    {
        using (UiCulture.Use("en"))
        {
            Assert.StartsWith("The workspace is read-only", SnapshotTexts.Moved, StringComparison.Ordinal);
        }

        Assert.StartsWith("Der Workspace ist schreibgeschützt", SnapshotTexts.Moved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_script_names_the_failed_statement_and_how_many_did_not_run()
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal("The script stopped at statement 2 – one more statement not run.",
                TextFormat.Plural(1, SqlEditorText.SqlView_ScriptStoppedOne, SqlEditorText.SqlView_ScriptStoppedOther, 2));
            Assert.Equal("The script stopped at statement 2 – 3 more statements not run.",
                TextFormat.Plural(3, SqlEditorText.SqlView_ScriptStoppedOne, SqlEditorText.SqlView_ScriptStoppedOther, 2));
        }

        Assert.Equal("Das Skript hat bei Statement 2 angehalten – 3 weitere Statements nicht ausgeführt.",
            TextFormat.Plural(3, SqlEditorText.SqlView_ScriptStoppedOne, SqlEditorText.SqlView_ScriptStoppedOther, 2));
    }
}
