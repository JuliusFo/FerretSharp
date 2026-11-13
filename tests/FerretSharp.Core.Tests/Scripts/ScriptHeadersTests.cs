using FerretSharp.Core.Query;
using FerretSharp.Core.Scripts;

namespace FerretSharp.Core.Tests.Scripts;

/// <summary>WP-77: the header of a SQL script – description and parameters in comment lines, nothing else lost.</summary>
public sealed class ScriptHeadersTests
{
    [Fact]
    public void The_header_gives_description_and_typed_parameters_and_the_rest_is_the_body()
    {
        var parsed = ScriptHeaders.Parse(
            "\r\n-- @description Open orders of a customer\r\n-- @param customer_id number\r\n-- @param \"from date\" DATE\r\n\r\nSELECT * FROM orders\r\nWHERE customer_id = :customer_id;\r\n");

        Assert.Equal("Open orders of a customer", parsed.Header.Description);
        Assert.Equal([new ScriptParameter("customer_id", SqlVariableType.Number), new ScriptParameter("from date", SqlVariableType.Date)], parsed.Header.Parameters);
        Assert.Empty(parsed.Errors);
        Assert.Equal("SELECT * FROM orders\nWHERE customer_id = :customer_id;\n", parsed.Body);
    }

    [Fact]
    public void A_script_without_header_is_all_body_and_a_comment_ends_the_header()
    {
        Assert.Equal(new ParsedScript(ScriptHeader.Empty, "-- just a note\nSELECT 1 FROM dual;", []), ScriptHeaders.Parse("-- just a note\nSELECT 1 FROM dual;") with { Header = ScriptHeader.Empty });
        Assert.True(ScriptHeaders.Parse("-- just a note\nSELECT 1 FROM dual;").Header.IsEmpty);

        var parsed = ScriptHeaders.Parse("-- @description x\n-- not a directive\n-- @param a text\nSELECT :a FROM dual");

        Assert.Empty(parsed.Header.Parameters); // the line after the comment belongs to the body
        Assert.StartsWith("-- not a directive", parsed.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_with_errors_are_reported_with_their_line_and_kept_like_unknown_ones()
    {
        var parsed = ScriptHeaders.Parse("-- @param id\n-- @param id number\n-- @param id text\n-- @param x varchar\n-- @connections prod\nSELECT 1");

        Assert.Equal([1, 3, 4], parsed.Errors.Select(e => e.Line));
        Assert.Equal([new ScriptParameter("id", SqlVariableType.Number)], parsed.Header.Parameters);
        Assert.Equal(["-- @param id", "-- @param id text", "-- @param x varchar", "-- @connections prod"], parsed.Header.Other);
        Assert.Contains("number", parsed.Errors[2].Message, StringComparison.Ordinal);
        Assert.StartsWith("Zeile 4:", parsed.Errors[2].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_puts_the_header_above_the_body_and_reads_back_the_same()
    {
        var header = new ScriptHeader("Locks\nof all sessions", [new("sid", SqlVariableType.Number), new("a b", SqlVariableType.Text)], ["-- @connections all"]);
        const string body = "\nSELECT * FROM v$lock WHERE sid = :sid;\n";

        var text = ScriptHeaders.Compose(header, body);

        Assert.Equal(
            "-- @description Locks\n-- @description of all sessions\n-- @param sid number\n-- @param \"a b\" text\n-- @connections all\n\n\nSELECT * FROM v$lock WHERE sid = :sid;\n",
            text);
        var parsed = ScriptHeaders.Parse(text);
        Assert.True(parsed.Header.SameAs(header));
        Assert.Equal(body, parsed.Body);
        Assert.Equal(body, ScriptHeaders.Compose(ScriptHeader.Empty, body)); // without header nothing is added
    }

    /// <summary>
    /// Review 3.70.0, A9: a line break in a parameter's name (a quoted bind) or a separator the reader splits at (U+2028) moved
    /// header text into the shared script's SQL. Every header line stays one line.
    /// </summary>
    [Theory]
    [InlineData("a\nDELETE FROM t")]
    [InlineData("a\r\nDELETE FROM t")]
    [InlineData("a\u2028DELETE FROM t")]
    [InlineData("a\u0085DELETE FROM t")]
    public void A_line_break_in_a_header_line_never_reaches_the_body(string broken)
    {
        var header = new ScriptHeader(null, [new(broken, SqlVariableType.Text)], ["-- @connections " + broken])
        {
            Targets = [new ScriptTarget(broken, "T", "C")],
        };
        const string body = "SELECT 1 FROM dual";

        var parsed = ScriptHeaders.Parse(ScriptHeaders.Compose(header, body));

        Assert.Equal(body, parsed.Body);
    }

    [Fact]
    public void Parameters_are_the_binds_of_the_text_with_the_types_of_their_variables()
    {
        var variables = new[] { new SqlVariable("ID", SqlVariableType.Number, "4711"), new SqlVariable("unused", SqlVariableType.Date, "") };

        var parameters = ScriptHeaders.ParametersOf("SELECT * FROM t WHERE id = :id AND name = :name;\nDELETE FROM u WHERE id = :id;", variables);

        Assert.Equal([new ScriptParameter("id", SqlVariableType.Number), new ScriptParameter("name", SqlVariableType.Text)], parameters);
    }

    [Fact]
    public void Adding_parameters_keeps_the_values_of_the_tab()
    {
        var variables = new[] { new SqlVariable("id", SqlVariableType.Text, "4711") };

        var result = ScriptHeaders.AddTo(variables, [new("ID", SqlVariableType.Number), new("since", SqlVariableType.Date)]);

        Assert.Equal([new SqlVariable("id", SqlVariableType.Text, "4711"), new SqlVariable("since", SqlVariableType.Date, "")], result);

        // A tab that shows the file (opened, reloaded after a pull): the file decides the types, the values stay.
        var applied = ScriptHeaders.Apply([new SqlVariable("id", SqlVariableType.Text, "4711") { National = true }], [new("ID", SqlVariableType.Number)]);
        Assert.Equal([new SqlVariable("id", SqlVariableType.Number, "4711")], applied);
    }

    [Fact]
    public void A_link_sees_changes_of_the_text_and_of_a_parameter_s_type()
    {
        var header = new ScriptHeader("x", [new("id", SqlVariableType.Number)], []);
        const string body = "SELECT * FROM t WHERE id = :id;\n";
        var link = ScriptLink.To(new ScriptRoot(ScriptRootKind.Personal, "/s"), "a.sql", header, body, "hash");
        var variables = new[] { new SqlVariable("id", SqlVariableType.Number, "1") };

        Assert.False(link.IsChanged(body.Replace("\n", "\r\n", StringComparison.Ordinal), variables)); // line breaks do not count
        Assert.True(link.IsChanged(body + "-- more", variables));
        Assert.True(link.IsChanged(body, [new SqlVariable("id", SqlVariableType.Text, "1")]));
    }

    /// <summary>
    /// Review WP-77, M1: the dot showed right after opening a file without header (from VS Code) or with its parameters
    /// in another order or spelling than the binds – and a pull asked instead of reloading quietly.
    /// </summary>
    [Fact]
    public void Only_the_text_and_the_types_of_the_file_s_parameters_count_as_a_change()
    {
        var root = new ScriptRoot(ScriptRootKind.Personal, "/s");
        const string body = "SELECT * FROM t WHERE id = :id AND name = :name;\n";
        var variables = new[] { new SqlVariable("id", SqlVariableType.Number, ""), new SqlVariable("name", SqlVariableType.Text, "") };

        var headerless = ScriptLink.To(root, "a.sql", ScriptHeader.Empty, body, "hash");
        var reordered = ScriptLink.To(root, "a.sql", new ScriptHeader(null, [new("NAME", SqlVariableType.Text), new("ID", SqlVariableType.Number), new("gone", SqlVariableType.Date)], []), body, "hash");

        Assert.False(headerless.IsChanged(body, variables));
        Assert.False(reordered.IsChanged(body, variables));
        Assert.True(reordered.IsChanged(body, [new SqlVariable("id", SqlVariableType.Text, ""), variables[1]]));
    }

    /// <summary>Review WP-77, K1: a header line from a workspace file edited by hand went into the shared script as SQL.</summary>
    [Fact]
    public void Only_directive_lines_are_kept_in_the_header()
    {
        var header = new ScriptHeader(null, [], ["-- @connections all", "GRANT DBA TO scott;", "-- a note"]);

        Assert.Equal("-- @connections all\n\nSELECT 1 FROM dual", ScriptHeaders.Compose(header, "SELECT 1 FROM dual"));
    }

    [Fact]
    public void Targets_and_list_parameters_are_read_and_written_back_the_same()
    {
        var parsed = ScriptHeaders.Parse(
            "-- @target ID customer.id\n-- @param id number\n-- @param ids number LIST\n-- @target ids \"Orders\" . \"Customer Id\"\n-- @target :ids CUSTOMER.ID\nSELECT 1");

        Assert.Empty(parsed.Errors);
        Assert.Equal([new ScriptParameter("id", SqlVariableType.Number), new ScriptParameter("ids", SqlVariableType.Number, List: true)], parsed.Header.Parameters);
        // A target above its parameter counts; the parameter's spelling wins, a name without quotes is upper case.
        Assert.Equal(
            [new ScriptTarget("id", "CUSTOMER", "ID"), new ScriptTarget("ids", "Orders", "Customer Id"), new ScriptTarget("ids", "CUSTOMER", "ID")],
            parsed.Header.Targets);

        var text = ScriptHeaders.Compose(parsed.Header, parsed.Body);

        Assert.Equal(
            "-- @param id number\n-- @param ids number list\n-- @target id CUSTOMER.ID\n-- @target ids \"Orders\".\"Customer Id\"\n-- @target ids CUSTOMER.ID\n\nSELECT 1",
            text);
        Assert.True(ScriptHeaders.Parse(text).Header.SameAs(parsed.Header));
    }

    [Fact]
    public void A_target_with_errors_is_reported_with_its_line_and_kept()
    {
        var parsed = ScriptHeaders.Parse(
            "-- @param id number\n-- @target id APP.CUSTOMER.ID\n-- @target other CUSTOMER.ID\n-- @target id CUSTOMER.ID\n-- @target ID customer.ID\n-- @target id\nSELECT 1");

        Assert.Equal([2, 3, 5, 6], parsed.Errors.Select(e => e.Line));
        Assert.Equal([new ScriptTarget("id", "CUSTOMER", "ID")], parsed.Header.Targets);
        Assert.Equal(["-- @target id APP.CUSTOMER.ID", "-- @target id", "-- @target other CUSTOMER.ID", "-- @target ID customer.ID"], parsed.Header.Other);
    }

    [Fact]
    public void Saving_a_tab_of_an_older_workspace_keeps_a_list_parameter_a_list_and_puts_the_file_s_targets_into_its_text()
    {
        var header = new ScriptHeader(null, [new("ids", SqlVariableType.Text, List: true)], ["-- @connections all"]) { Targets = [new("ids", "CUSTOMER", "ID")] };
        // Written before fix-script-header-in-editor: the tab shows the body only.
        var link = ScriptLink.To(new ScriptRoot(ScriptRootKind.Personal, "/s"), "a.sql", header, "SELECT 1", "hash") with { HeaderInText = false };
        const string body = "SELECT * FROM t WHERE id IN (:ids) AND x = :x";

        var saved = link.SaveOf(body, [new SqlVariable("ids", SqlVariableType.Number, "")]);

        Assert.Equal([new ScriptParameter("ids", SqlVariableType.Number, List: true), new ScriptParameter("x", SqlVariableType.Text)], saved.Header.Parameters);
        Assert.Equal(header.Targets, saved.Header.Targets);
        Assert.Equal(header.Other, saved.Header.Other);
        Assert.Equal(body, saved.Body);
        Assert.Equal("-- @target ids CUSTOMER.ID\n-- @connections all\n\n" + body, saved.Text); // from now on the text holds them
        Assert.True(saved.Joined);

        // Second review N2: typed during the write, the tab kept its text without the targets – its link must not claim the
        // text holds them, or the next save drops them from the shared file.
        var after = ScriptLink.To(new ScriptRoot(ScriptRootKind.Personal, "/s"), "a.sql", saved.Header, saved.Text, "hash2");
        Assert.True(saved.LinkAfter(after, textShown: true).HeaderInText);
        var notShown = saved.LinkAfter(after, textShown: false);
        Assert.False(notShown.HeaderInText);
        Assert.Equal(header.Targets, notShown.SaveOf(body + " -- typed meanwhile", []).Header.Targets);
    }

    [Fact]
    public void Header_lines_typed_at_the_top_of_a_tab_are_saved_as_the_header_and_the_targets_stay_in_the_text()
    {
        // The case of the user (2026-11-13): typed in the SQL editor, the target ended up in the body and was never read.
        const string typed = "-- @description Rows of an order\n-- @param ID text\n-- @param ids number list\n-- @target id orders.id\n"
                             + "SELECT * FROM order_rows WHERE order_id = :id OR order_id IN (:ids);";

        var saved = ScriptHeaders.ForSave(typed, [new SqlVariable("id", SqlVariableType.Number, "7")], null, false);

        Assert.Empty(saved.Problems);
        Assert.Equal("Rows of an order", saved.Header.Description);
        Assert.Equal([new ScriptParameter("id", SqlVariableType.Text), new ScriptParameter("ids", SqlVariableType.Number, List: true)], saved.Header.Parameters);
        Assert.Equal([new ScriptTarget("id", "ORDERS", "ID")], saved.Header.Targets);
        // @param and @description live in the variables and the dialog: they leave the text, the value typed stays.
        Assert.Equal([new SqlVariable("id", SqlVariableType.Text, "7"), new SqlVariable("ids", SqlVariableType.Number, "")],
            ScriptHeaders.Apply([new SqlVariable("id", SqlVariableType.Number, "7")], saved.Typed));
        // The other lines stay as typed; the file writes the target in its form.
        Assert.Equal("-- @target id orders.id\n\nSELECT * FROM order_rows WHERE order_id = :id OR order_id IN (:ids);", saved.Text);
        var file = ScriptHeaders.Parse(ScriptHeaders.Compose(saved.Header, saved.Body));
        Assert.True(file.Header.SameAs(saved.Header));
        Assert.Equal(saved.Body, file.Body);
    }

    [Fact]
    public void A_tab_showing_the_file_saves_it_unchanged_and_its_text_decides_the_targets()
    {
        var header = new ScriptHeader("x", [new("id", SqlVariableType.Number)], ["-- @connections all"]) { Targets = [new("id", "CUSTOMER", "ID")] };
        const string body = "SELECT * FROM orders WHERE customer_id = :id";
        var shown = ScriptHeaders.EditorText(header, body);
        var variables = ScriptHeaders.Apply([], header.Parameters);

        Assert.Equal("-- @target id CUSTOMER.ID\n-- @connections all\n\n" + body, shown);
        var same = ScriptHeaders.ForSave(shown, variables, header, true);
        Assert.True(same.Header.SameAs(header));
        Assert.Equal((body, shown), (same.Body, same.Text));
        Assert.Equal(body, ScriptHeaders.EditorText(ScriptHeader.Empty, body));

        // A line deleted in the text is gone; a target whose bind the SQL lacks is reported with its line in the text and kept.
        var edited = ScriptHeaders.ForSave("-- @target other CUSTOMER.ID\n" + body, variables, header, true);

        Assert.Empty(edited.Header.Targets);
        Assert.Equal(["-- @target other CUSTOMER.ID"], edited.Header.Other);
        // Named by the line, not its number; the bind is missing, not a @param line.
        Assert.Equal(["„-- @target other CUSTOMER.ID“: @target nennt other, aber das SQL hat keinen Bind :other."], edited.Problems);
        Assert.Equal("x", edited.Header.Description); // the file's, nothing typed
        Assert.Equal("-- @target other CUSTOMER.ID\n" + body, edited.Text); // nothing taken out: as typed
    }

    [Fact]
    public void A_typed_param_without_its_bind_is_reported_and_stays_in_the_text_named_by_its_line()
    {
        // Review fix-script-header-in-editor, 2 and 4: the line vanished with its type, and a number pointed elsewhere once
        // the text was rebuilt (the description line leaves it).
        const string typed = "-- @description d\n-- @param customerid number\n-- @target customer_id CUSTOMER.ID\nSELECT :customer_id FROM dual";

        var saved = ScriptHeaders.ForSave(typed, [], null, false);

        Assert.Empty(saved.Typed); // no variable for it
        Assert.Equal([new ScriptParameter("customer_id", SqlVariableType.Text)], saved.Header.Parameters);
        Assert.Equal([new ScriptTarget("customer_id", "CUSTOMER", "ID")], saved.Header.Targets);
        Assert.Empty(saved.Header.Other); // not written (second review N1): the file would read it as a parameter
        Assert.Equal(["„-- @param customerid number“: @param nennt customerid, aber das SQL hat keinen Bind :customerid – nicht gespeichert."], saved.Problems);
        Assert.Equal("-- @param customerid number\n-- @target customer_id CUSTOMER.ID\n\nSELECT :customer_id FROM dual", saved.Text);
        Assert.Equal(saved.Header.Parameters, ScriptHeaders.Parse(ScriptHeaders.Compose(saved.Header, saved.Body)).Header.Parameters);

        // Third review K2: the file lacks the line, so the tab showing it counts as changed – a change on disk asks then.
        Assert.Equal("-- @target customer_id CUSTOMER.ID\n\nSELECT :customer_id FROM dual", saved.SavedText);
        var link = ScriptLink.To(new ScriptRoot(ScriptRootKind.Personal, "/s"), "a.sql", saved.Header, saved.SavedText, "hash");
        Assert.True(link.IsChanged(saved.Text, []));
        Assert.False(link.IsChanged(saved.SavedText, []));
    }

    [Fact]
    public void A_param_of_the_file_kept_with_errors_is_not_shown_and_saving_leaves_the_type()
    {
        // Review fix-script-header-in-editor, 3: shown in the tab, the second line of a parameter (after a merge) was taken as
        // typed on the next save and turned the type unseen.
        var file = ScriptHeaders.Parse("-- @param id number\n-- @param id text\n-- @connections all\nSELECT :id FROM dual").Header;
        var shown = ScriptHeaders.EditorText(file, "SELECT :id FROM dual");

        var saved = ScriptHeaders.ForSave(shown, ScriptHeaders.Apply([], file.Parameters), file, true);

        Assert.Equal("-- @connections all\n\nSELECT :id FROM dual", shown);
        Assert.Equal([new ScriptParameter("id", SqlVariableType.Number)], saved.Header.Parameters);
        Assert.Equal(["-- @connections all", "-- @param id text"], saved.Header.Other); // kept in the file as before
        Assert.Equal(shown, saved.Text);

        // Second review N3: a @param of that name typed settles it – the hidden line goes, else it could never be removed.
        var settled = ScriptHeaders.ForSave("-- @param id text\n" + shown, ScriptHeaders.Apply([], file.Parameters), file, true);

        Assert.Equal([new ScriptParameter("id", SqlVariableType.Text)], settled.Header.Parameters);
        Assert.Equal(["-- @connections all"], settled.Header.Other);

        // Third review K3: hidden @param lines without effect – no name, or no bind in the SQL – go; with a bind they stay.
        var stale = file with { Other = ["-- @param", "-- @param gone numbr", "-- @param id numbr"] };
        Assert.Equal(["-- @connections all", "-- @param id numbr"], ScriptHeaders.ForSave(shown, [], stale, true).Header.Other);
    }

    [Theory]
    [InlineData("-- @param id text\nSELECT :id FROM dual", "SELECT :id FROM dual")] // still what was saved: the rebuilt text
    [InlineData("-- @param id text\r\nSELECT :id FROM dual", "SELECT :id FROM dual")] // line breaks do not count
    [InlineData("-- @param id text\nSELECT :id, 1 FROM dual", null)] // typed during the write: stays (review 1)
    public void After_writing_the_tab_gets_the_rebuilt_text_only_while_it_still_holds_the_saved_one(string current, string? expected)
    {
        const string saved = "-- @param id text\nSELECT :id FROM dual";

        Assert.Equal(expected, ScriptHeaders.ForSave(saved, [], null, false).TextFor(current, saved));
        Assert.Null(ScriptHeaders.ForSave("SELECT 1 FROM dual", [], null, false).TextFor("SELECT 1 FROM dual", "SELECT 1 FROM dual")); // nothing rebuilt
    }
}
