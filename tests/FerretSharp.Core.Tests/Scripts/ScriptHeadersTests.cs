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
    }

    [Fact]
    public void Header_lines_typed_at_the_top_of_a_tab_are_saved_as_the_header_and_the_targets_stay_in_the_text()
    {
        // The case of the user (2026-11-13): typed in the SQL editor, the target ended up in the body and was never read.
        const string typed = "-- @description Rows of an order\n-- @param ID text\n-- @param ids number list\n-- @target id orders.id\n"
                             + "SELECT * FROM order_rows WHERE order_id = :id OR order_id IN (:ids);";

        var saved = ScriptHeaders.ForSave(typed, [new SqlVariable("id", SqlVariableType.Number, "7")], null, false);

        Assert.Empty(saved.Errors);
        Assert.Equal("Rows of an order", saved.Header.Description);
        Assert.Equal([new ScriptParameter("id", SqlVariableType.Text), new ScriptParameter("ids", SqlVariableType.Number, List: true)], saved.Header.Parameters);
        Assert.Equal([new ScriptTarget("id", "ORDERS", "ID")], saved.Header.Targets);
        // @param and @description live in the variables and the dialog: they leave the text, the value typed stays.
        Assert.Equal([new SqlVariable("id", SqlVariableType.Text, "7"), new SqlVariable("ids", SqlVariableType.Number, "")], saved.Variables);
        Assert.Equal("-- @target id ORDERS.ID\n\nSELECT * FROM order_rows WHERE order_id = :id OR order_id IN (:ids);", saved.Text);
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
        Assert.Equal([1], edited.Errors.Select(e => e.Line));
        Assert.Contains(":other", edited.Errors[0].Message, StringComparison.Ordinal); // the bind is missing, not a @param line
        Assert.Equal("x", edited.Header.Description); // the file's, nothing typed
        Assert.Equal("-- @target other CUSTOMER.ID\n" + body, edited.Text); // nothing taken out: as typed
    }
}
