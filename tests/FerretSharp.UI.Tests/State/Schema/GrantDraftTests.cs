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

    /// <summary>Review of WP-71: a grantee created in quotes became the upper-case name – another grantee, or ORA-01917.</summary>
    [Fact]
    public void A_grantee_created_in_quotes_is_suggested_and_granted_as_it_is()
    {
        var draft = Draft(users: ["U1", "app_ro"], roles: ["Leser"]);

        Assert.Equal(["PUBLIC", "\"Leser\"", "U1", "\"app_ro\""], draft.Suggestions);

        draft.Grantee = GrantDraft.Typed("app_ro");
        draft.Choose("SELECT", true);
        Assert.Equal("GRANT SELECT ON \"APP\".\"KUNDEN\" TO \"app_ro\"", Assert.Single(draft.Steps).Sql);
        Assert.Equal(("PUBLIC", "U1"), (GrantDraft.Typed("PUBLIC"), GrantDraft.Typed("U1")));

        // second pass: an unclosed quote ("app_ro without the end) is no grantee – shown as it is, never thrown while rendering
        Assert.Equal("\"APP_RO", GrantDraft.Typed("\"APP_RO"));
        Assert.Equal("", GrantDraft.Typed(""));
    }

    /// <summary>
    /// The grantee's synonym (idea of a colleague, 2026-11-01): off unless chosen, only for a user other than the owner, not
    /// when it has one on the object already, after the GRANT; a failure of the synonym alone gets its own hint.
    /// </summary>
    [Fact]
    public void A_synonym_of_the_grantee_follows_the_grant_when_chosen()
    {
        var draft = Draft("u1");
        draft.Choose("SELECT", true);
        Assert.Single(draft.Steps); // off by default

        draft.CreateSynonym = true;
        Assert.Equal(
            ["GRANT SELECT ON \"APP\".\"KUNDEN\" TO \"U1\"", "CREATE SYNONYM \"U1\".\"KUNDEN\" FOR \"APP\".\"KUNDEN\""],
            draft.Steps.Select(s => s.Sql));

        draft.SynonymName = "";
        Assert.Equal(["Das Synonym braucht einen Namen."], draft.Problems);
        draft.SynonymName = "\"Kunden\"";
        Assert.Contains(draft.Warnings, w => w.Contains("Anführungszeichen", StringComparison.Ordinal));

        // what Oracle says about the synonym alone: the grant ran
        var synonymSql = draft.Steps[1].Sql;
        Assert.Contains("CREATE ANY SYNONYM", draft.FailureHint("ORA-01031", synonymSql, loginUser: "APP"), StringComparison.Ordinal);
        Assert.Contains("hat den Namen schon", draft.FailureHint("ORA-00955", synonymSql, "APP"), StringComparison.Ordinal);
        Assert.Contains("GRANT ANY OBJECT PRIVILEGE", draft.FailureHint("ORA-01031", draft.Steps[0].Sql, "APP"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PUBLIC")]   // would be a public synonym
    [InlineData("APP")]      // the owner
    [InlineData("APP_ROLE")] // a role has no schema
    public void No_synonym_is_offered_where_there_is_no_schema_of_the_grantee_for_it(string grantee)
    {
        var draft = Draft(grantee);
        draft.Choose("SELECT", true);
        draft.CreateSynonym = true;

        Assert.False(draft.SynonymOffered);
        Assert.DoesNotContain(draft.Steps, s => s.Kind == Core.Ddl.DdlStepKind.Synonym);
    }

    [Fact]
    public void A_grantee_with_a_synonym_on_the_object_gets_none_more()
    {
        var draft = new GrantDraft(Table, GrantObjectKind.Table, [], ["U1"], [], [new SynonymInfo("U1", "KUNDE"), new SynonymInfo("PUBLIC", "KUNDEN")])
        {
            Grantee = "U1",
            CreateSynonym = true,
        };
        draft.Choose("SELECT", true);

        Assert.Equal(new SynonymInfo("U1", "KUNDE"), draft.ExistingSynonym);
        Assert.Single(draft.Steps);
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

        // the grantee's last privilege goes, its synonym stays and leads nowhere; with another privilege left it still works
        var select = new ObjectGrant("U1", "SELECT", [], "APP", false, false);
        SynonymInfo[] synonyms = [new("U1", "KUNDEN")];
        Assert.Equal(["U1 behält das Synonym KUNDEN; ohne Recht zeigt es ins Leere – in der Ansicht „Synonyme“ löschen."],
            GrantDraft.RevokeNotes(select, [select], "APP", synonyms));
        Assert.Empty(GrantDraft.RevokeNotes(select, [select, select with { Privilege = "INSERT" }], "APP", synonyms));
    }

    /// <summary>
    /// Review of WP-71, measured against Oracle 23: REVOKE takes only the executing user's grant – on someone else's row it
    /// took the login user's grant of the same privilege and left the chosen row.
    /// </summary>
    [Fact]
    public void Someone_else_s_grant_is_not_revoked_while_the_login_user_gave_the_same()
    {
        var theirs = new ObjectGrant("U1", "SELECT", [], "OTHER", false, false);
        var mine = theirs with { Grantor = "APP" };

        Assert.Equal(["U1 hat SELECT auch von dir. Dieses REVOKE entzöge dein Recht, nicht das von OTHER – diese Zeile kann nur OTHER entziehen."],
            GrantDraft.RevokeProblems(theirs, [theirs, mine], loginUser: "APP"));
        Assert.Empty(GrantDraft.RevokeProblems(theirs, [theirs], "APP")); // Oracle refuses it itself (ORA-01927)
        Assert.Empty(GrantDraft.RevokeProblems(mine, [theirs, mine], "APP"));
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
