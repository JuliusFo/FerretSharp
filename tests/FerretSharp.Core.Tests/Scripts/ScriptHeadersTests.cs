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
}
