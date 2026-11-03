using FerretSharp.Core.Query;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// WP-56: SQL and LINQ tabs keep their form beside the result like table tabs (WP-21), in the same file format; since WP-73
/// also the pinned result columns.
/// </summary>
public sealed class FormTabsTests
{
    [Fact]
    public void Sql_and_linq_tabs_save_and_restore_their_form()
    {
        var sql = new SqlTab(Guid.NewGuid(), "SQL 1") { Text = "SELECT 1 FROM DUAL", FormOpen = true, FormWidth = 520, ResultPins = [new("ID", 1)] };
        var linq = new LinqTab(Guid.NewGuid(), "LINQ 1") { FormHideEmpty = true, ResultPins = [new("Name")] };

        var sqlState = sql.Save([sql, linq]);
        var linqState = linq.Save([sql, linq]);

        Assert.Equal(new FormTabState(true, 520, false), sqlState.Form);
        Assert.Equal(new FormTabState(false, null, true), linqState.Form);
        Assert.Equal([new ResultColumnPin("ID", 1)], sqlState.ResultPins);
        var restoredSql = SqlTab.Restore(Guid.NewGuid(), sqlState.Sql!, sqlState.Form, sqlState.ResultPins);
        Assert.Equal(sqlState.ResultPins, restoredSql.ResultPins);
        Assert.Equal((true, 520, false), (restoredSql.FormOpen, restoredSql.FormWidth, restoredSql.FormHideEmpty));
        Assert.Equal("SELECT 1 FROM DUAL", restoredSql.Text);
        var restoredLinq = LinqTab.Restore(Guid.NewGuid(), linqState.Linq!, linqState.Form, linqState.ResultPins);
        Assert.Equal([new ResultColumnPin("Name")], restoredLinq.ResultPins);
        Assert.Equal((false, null, true), (restoredLinq.FormOpen, restoredLinq.FormWidth, restoredLinq.FormHideEmpty));
    }

    [Fact]
    public void A_form_never_used_is_not_saved()
    {
        var sql = new SqlTab(Guid.NewGuid(), "SQL 1") { Variables = [new SqlVariable("id", SqlVariableType.Number, "1")] };

        Assert.Null(sql.Save([sql]).Form);
        Assert.Null(sql.Save([sql]).ResultPins); // nothing pinned: nothing in the file
        Assert.False(SqlTab.Restore(Guid.NewGuid(), new SqlTabState("SQL 1", "", []), form: null).FormOpen);
    }
}
