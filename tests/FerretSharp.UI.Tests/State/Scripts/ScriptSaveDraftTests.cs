using FerretSharp.Core.Query;
using FerretSharp.Core.Scripts;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-77: the fields of "Save as script".</summary>
public sealed class ScriptSaveDraftTests
{
    private static readonly ScriptRoot Personal = new(ScriptRootKind.Personal, "/p");
    private static readonly ScriptRoot Erp = new(ScriptRootKind.Group, "/erp", "ERP");
    private static readonly IReadOnlyList<ScriptRoot> Roots = [Personal, Erp];

    /// <summary>
    /// Review WP-89, 7: the save dialog closed for the help gives its entries to the next one – with the text as it is then.
    /// A description typed into the text meanwhile wins over the dialog's (N1); the subfolder comes only with its folder of
    /// the settings, and that only if the connection still sees it (N5).
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_new_draft_takes_the_entries_of_one_closed_for_the_help(bool rootStillSeen, bool descriptionTyped)
    {
        var closed = new ScriptSaveDraft(new ScriptSaveRequest(ScriptSaveSource.Window, "SELECT 1 FROM dual", [], ""), Roots)
        {
            Root = Erp, Name = "Open orders", Folder = "Orders", Description = "By customer",
        };

        // The text as it is now: a target (and maybe a description) typed after reading the help counts.
        var header = (descriptionTyped ? "-- @description Typed in the text\n" : "") + "-- @target id ORDERS.ID\n\n";
        var next = new ScriptSaveDraft(
            new ScriptSaveRequest(ScriptSaveSource.Window, header + "SELECT * FROM orders WHERE id = :id", [], ""),
            rootStillSeen ? Roots : [Personal]);
        next.Take(closed.Inputs);

        Assert.Equal("Open orders", next.Name);
        Assert.Equal(descriptionTyped ? "Typed in the text" : "By customer", next.Description);
        Assert.Equal(rootStillSeen ? (Erp, "Orders") : (Personal, ""), (next.Root, next.Folder));
        Assert.Single(next.Save.Header.Targets);
    }

    [Fact]
    public void Name_and_folder_are_checked_and_give_the_path()
    {
        var draft = new ScriptSaveDraft(new ScriptSaveRequest(ScriptSaveSource.Selection, "SELECT 1 FROM dual", [], ""), Roots);
        Assert.Same(Personal, draft.Root);
        Assert.False(draft.CanSave);
        Assert.NotNull(draft.NameError);

        draft.Name = " Open orders ";
        draft.Folder = "Orders\\Customer/";
        Assert.True(draft.CanSave);
        Assert.Equal("Orders/Customer/Open orders.sql", draft.Path);

        draft.Folder = "../outside";
        Assert.NotNull(draft.FolderError);
        draft.Folder = "";
        draft.Name = "CON";
        Assert.NotNull(draft.NameError);
    }

    [Fact]
    public void The_header_has_the_description_and_the_parameters_of_the_text_with_their_types()
    {
        var request = new ScriptSaveRequest(ScriptSaveSource.Window, "SELECT * FROM t WHERE id = :id",
            [new SqlVariable("id", SqlVariableType.Number, "4711"), new SqlVariable("unused", SqlVariableType.Date, "")], "x");
        var draft = new ScriptSaveDraft(request, Roots) { Description = "  " };

        Assert.Null(draft.Header.Description);
        Assert.Equal([new ScriptParameter("id", SqlVariableType.Number)], draft.Header.Parameters);
    }

    [Fact]
    public void Save_as_starts_in_the_folder_of_the_linked_file_and_keeps_its_unknown_lines()
    {
        var link = new ScriptLink(ScriptRootKind.Group, "erp", "Orders/Open.sql", "F", "S", new ScriptHeader("Open orders", [], ["-- @connections all"]));

        var draft = new ScriptSaveDraft(new ScriptSaveRequest(ScriptSaveSource.Window, "SELECT 1 FROM dual", [], link.Name, link), Roots);

        Assert.Equal((Erp, "Orders", "Open", "Open orders"), (draft.Root, draft.Folder, draft.Name, draft.Description));
        Assert.Equal(["-- @connections all"], draft.Header.Other);
    }
}
