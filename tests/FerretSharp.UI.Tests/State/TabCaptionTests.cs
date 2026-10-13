using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R5b (review 3.31.0, B12): one name for a tab – in the tab bar, the dialogs and the change overview, which named PL/SQL tabs with nothing.</summary>
public sealed class TabCaptionTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    [Fact]
    public void Every_kind_of_tab_has_its_name()
    {
        Assert.Equal("KUNDEN", new TableTab(Workspace, TestApp.Kunden).Caption);
        Assert.Equal("PKG_RECHNUNG", new PlSqlTab(Workspace, TestApp.Rechnung).Caption);
        Assert.Equal("SQL 1", new SqlTab(Workspace, "SQL 1").Caption);
        Assert.Equal("Kunden suchen", new LinqTab(Workspace, "Kunden suchen").Caption);
        Assert.Equal("Neue Tabelle", new NewTableTab(Workspace, "APP_USER").Caption);
        Assert.Equal("RECHNUNG", new NewTableTab(Workspace, "APP_USER") { Name = "RECHNUNG" }.Caption);
    }
}
