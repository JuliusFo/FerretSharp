using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// WP-71: the grant draft runs only with a grantee and a privilege, never for the owner or a role with grant option;
/// columns go along only for privileges Oracle grants per column; the notes before a revoke and the hints after a refusal
/// follow what Oracle 23 does.
/// </summary>
public sealed class GrantDraftTests
{
    private static readonly TableRef Table = new("APP", "KUNDEN");

    private static GrantDraft Draft(string grantee = "", IReadOnlyList<string>? users = null, IReadOnlyCollection<string>? roles = null) =>
        new(Table, GrantObjectKind.Table, ["ID", "NAME"], users ?? ["APP", "U1"], roles ?? ["APP_ROLE"]) { Grantee = grantee };

    [Fact]
    public void Runs_with_a_grantee_and_a_privilege_in_the_order_offered_and_columns_only_where_they_apply()
    {
        var draft = Draft("u1");
        Assert.Equal(["Mindestens ein Recht wählen."], draft.Problems);

        draft.Choose("UPDATE", true);
        draft.Choose("SELECT", true);
        draft.ChooseColumn("NAME", true);
        Assert.Empty(draft.Problems);
        Assert.Equal("GRANT SELECT, UPDATE (\"NAME\") ON \"APP\".\"KUNDEN\" TO \"U1\"", Assert.Single(draft.Steps).Sql);

        draft.Choose("UPDATE", false);
        Assert.False(draft.ColumnsApply);
        Assert.Equal("GRANT SELECT ON \"APP\".\"KUNDEN\" TO \"U1\"", Assert.Single(draft.Steps).Sql);
    }

    [Theory]
    [InlineData("", false, "Empfänger fehlt: ein User, eine Rolle oder PUBLIC.")]
    [InlineData("app", false, "APP ist Owner des Objekts und hat alle Rechte daran.")]
    [InlineData("APP_ROLE", true, "APP_ROLE ist kein User: Eine Rolle bekommt kein Recht WITH GRANT OPTION (ORA-01926).")]
    [InlineData("NEW_ROLE", true, "NEW_ROLE ist kein User: Eine Rolle bekommt kein Recht WITH GRANT OPTION (ORA-01926).")]
    public void Never_for_no_grantee_the_owner_or_a_role_with_grant_option(string grantee, bool grantOption, string problem)
    {
        var draft = Draft(grantee);
        draft.Choose("SELECT", true);
        draft.GrantOption = grantOption;

        Assert.Equal([problem], draft.Problems);
    }

    [Fact]
    public void PUBLIC_and_a_user_take_the_grant_option_and_an_unknown_grantee_is_only_a_warning()
    {
        var draft = Draft("public");
        draft.Choose("SELECT", true);
        draft.GrantOption = true;
        Assert.Empty(draft.Problems);
        Assert.Equal(["PUBLIC sind alle Benutzer der Datenbank."], draft.Warnings);
        Assert.EndsWith("TO PUBLIC WITH GRANT OPTION", Assert.Single(draft.Steps).Sql, StringComparison.Ordinal);

        draft.Grantee = "NEW_ROLE";
        draft.GrantOption = false;
        Assert.Empty(draft.Problems);
        Assert.Contains("ORA-01917", Assert.Single(draft.Warnings), StringComparison.Ordinal);

        // without the users nothing is checked against them
        var unchecked_ = Draft("NEW_ROLE", users: [], roles: []);
        unchecked_.Choose("SELECT", true);
        unchecked_.GrantOption = true;
        Assert.Empty(unchecked_.Problems);
        Assert.Empty(unchecked_.Warnings);
    }

    [Fact]
    public void A_revoke_names_what_it_takes_along()
    {
        var update = new ObjectGrant("U1", "UPDATE", ["NAME"], "APP", Grantable: true, false);
        var references = new ObjectGrant("PUBLIC", "REFERENCES", [], "OTHER", false, false);

        Assert.Equal(
        [
            "Oracle entzieht UPDATE nur für das ganze Objekt: U1 verliert es auf allen Spalten.",
            "Mit GRANT OPTION vergeben: Wer es von U1 bekommen hat, verliert es mit.",
        ], GrantDraft.RevokeNotes(update, [update], loginUser: "APP"));

        var notes = GrantDraft.RevokeNotes(references, [references], loginUser: "APP");
        Assert.Equal(3, notes.Count);
        Assert.Contains("PUBLIC", notes[0], StringComparison.Ordinal);
        Assert.StartsWith("Vergeben von OTHER", notes[1], StringComparison.Ordinal);
        Assert.Contains("ORA-01981", notes[2], StringComparison.Ordinal);

        // a privilege on the whole object and on columns from the same grantor: both go
        var whole = update with { Columns = [], Grantable = false };
        Assert.Contains(GrantDraft.RevokeNotes(whole, [whole, update], "APP"), n => n.Contains("allen Spalten", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ORA-01031", false, "GRANT ANY OBJECT PRIVILEGE")]
    [InlineData("ORA-01720", false, "Tabellen dahinter")]
    [InlineData("ORA-01927", true, "Entziehen kann es nur OTHER")]
    [InlineData("ORA-01981", true, "CASCADE CONSTRAINTS")]
    [InlineData("ORA-01926", false, "Eine Rolle bekommt kein Recht")]
    public void A_refusal_gets_a_hint_by_its_code(string code, bool revoke, string part)
    {
        var grant = revoke ? new ObjectGrant("U1", "REFERENCES", [], "OTHER", false, false) : null;

        Assert.Contains(part, GrantDraft.ErrorHint(code, grant), StringComparison.Ordinal);
        Assert.Null(GrantDraft.ErrorHint("ORA-00942", grant));
    }
}
