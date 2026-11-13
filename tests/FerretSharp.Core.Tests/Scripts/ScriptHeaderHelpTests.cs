using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Scripts;
using NSubstitute;

namespace FerretSharp.Core.Tests.Scripts;

/// <summary>WP-89: the help with a script's header in the SQL editor – marks while typing, hover, completion.</summary>
public sealed class ScriptHeaderHelpTests
{
    private const string Owner = "APP";

    private static readonly TableDetails Customer = new(new TableSummary(Owner, "CUSTOMER", TableKind.Table),
        [new("ID", "NUMBER", null, false, 10, 0, false, false, null, 1), new("NAME", "VARCHAR2", 100, true, null, null, false, false, null, 2)],
        ["ID"], [], false);

    private static readonly TableDetails Orders = new(new TableSummary(Owner, "ORDERS", TableKind.Table),
        [new("ID", "NUMBER", null, false, 10, 0, false, false, null, 1), new("CUSTOMER_ID", "NUMBER", null, false, 10, 0, false, false, null, 2)],
        ["ID"], [], false);

    private static readonly TableSummary Country = new("OTHER", "COUNTRIES", TableKind.Table, new SynonymInfo(Owner, "COUNTRY"));

    private static readonly TableDetails Countries = new(Country,
        [new("CODE", "VARCHAR2", 2, false, null, null, false, false, null, 1), new("NAME", "VARCHAR2", 100, true, null, null, false, false, null, 2)],
        ["CODE"], [], false);
    private static readonly TableSummary Foreign = new("OTHER", "PRICES", TableKind.View);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<SchemaCache> SchemaAsync()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[Customer.Table, Orders.Table, Foreign]);
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(new SynonymTargets([Country], []));
        reader.GetForeignKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(
            (IReadOnlyList<ForeignKeyInfo>)[new("FK_ORDERS_CUSTOMER", Orders.Table.Ref, ["CUSTOMER_ID"], Customer.Table.Ref, ["ID"], FkSource.Declared)]);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "CUSTOMER"), Arg.Any<CancellationToken>()).Returns(Customer);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "ORDERS"), Arg.Any<CancellationToken>()).Returns(Orders);
        reader.GetDetailsAsync(Arg.Is<TableSummary>(t => t.Name == "COUNTRIES"), Arg.Any<CancellationToken>()).Returns(Countries);
        var schema = new SchemaCache(reader, Owner);
        await schema.LoadAsync(Token);
        return schema;
    }

    private static string Marked(ScriptHeaderMarker marker, string text) => text.Substring(marker.Start, marker.Length);

    [Fact]
    public async Task Lines_saving_reports_are_marked_where_they_stand_also_with_CRLF()
    {
        const string text = "-- @target customer_id CUSTOMER.ID\r\n-- @param gone number\r\n\r\nSELECT * FROM orders WHERE id = :id";

        var markers = await ScriptHeaderHelp.MarkersAsync(text, null, Token);

        Assert.Equal(2, markers.Count);
        Assert.Equal("-- @target customer_id CUSTOMER.ID", Marked(markers[0], text));
        Assert.Equal(TextFormat.Format(ScriptText.TargetUnknownBind, "customer_id"), markers[0].Message);
        Assert.Equal("-- @param gone number", Marked(markers[1], text));
        Assert.All(markers, m => Assert.Equal(ScriptMarkerSeverity.Warning, m.Severity));
    }

    [Fact]
    public async Task A_text_without_header_lines_has_no_marks()
    {
        Assert.Empty(await ScriptHeaderHelp.MarkersAsync("-- a comment\nSELECT * FROM orders WHERE id = :id", await SchemaAsync(), Token));
        Assert.Empty(await ScriptHeaderHelp.MarkersAsync("", null, Token));
    }

    [Fact]
    public async Task An_unknown_directive_is_a_hint_and_a_typo_of_a_known_one_a_warning()
    {
        const string text = "-- @owner team-a\n-- @tagret id ORDERS.ID\n-- @parm id number\nSELECT * FROM orders WHERE id = :id";

        var markers = await ScriptHeaderHelp.MarkersAsync(text, null, Token);

        Assert.Equal(
            [
                (ScriptMarkerSeverity.Info, TextFormat.Format(ScriptText.Marker_Unknown, "owner")),
                (ScriptMarkerSeverity.Warning, TextFormat.Format(ScriptText.Marker_UnknownSimilar, "tagret", "target")),
                (ScriptMarkerSeverity.Warning, TextFormat.Format(ScriptText.Marker_UnknownSimilar, "parm", "param")),
            ],
            markers.Select(m => (m.Severity, m.Message)));
    }

    [Theory]
    [InlineData("SELECT 1 FROM dual;\n-- @target id ORDERS.ID\nSELECT * FROM orders WHERE id = :id", "-- @target id ORDERS.ID")]
    [InlineData("-- what it does\n-- @param id number\nSELECT * FROM orders WHERE id = :id", "-- @param id number")]
    [InlineData("-- @description Orders\n\n-- @target id ORDERS.ID\nSELECT * FROM orders WHERE id = :id", "-- @target id ORDERS.ID")]
    public async Task A_known_directive_below_the_header_is_marked_as_without_effect(string text, string line)
    {
        var markers = await ScriptHeaderHelp.MarkersAsync(text, null, Token);

        var marker = Assert.Single(markers);
        Assert.Equal(line, Marked(marker, text));
        Assert.Equal(ScriptText.Marker_NotHeader, marker.Message);
    }

    [Fact]
    public async Task With_a_schema_a_missing_table_is_a_hint_and_a_missing_column_a_warning()
    {
        const string text = "-- @target id CUSTOMER.ID\n-- @target id PRODUCT.ID\n-- @target id ORDERS.NOPE\n-- @target id country.code\nSELECT * FROM orders WHERE id = :id";
        var schema = await SchemaAsync();

        var markers = await ScriptHeaderHelp.MarkersAsync(text, schema, Token);

        Assert.Equal(
            [
                (ScriptMarkerSeverity.Info, TextFormat.Format(ScriptText.Target_TableMissing, "PRODUCT"), "-- @target id PRODUCT.ID"),
                (ScriptMarkerSeverity.Warning, TextFormat.Format(ScriptText.Target_ColumnMissing, "ORDERS", "NOPE"), "-- @target id ORDERS.NOPE"),
            ],
            markers.Select(m => (m.Severity, m.Message, Marked(m, text))));
    }

    [Fact]
    public async Task The_hover_over_a_target_says_where_the_script_shows()
    {
        const string text = "-- @target id CUSTOMER.ID\nSELECT * FROM orders WHERE customer_id = :id";

        var hover = await ScriptHeaderHelp.HoverAsync(text, 5, await SchemaAsync(), Token);

        Assert.NotNull(hover);
        Assert.Equal((0, "-- @target id CUSTOMER.ID".Length), (hover.Start, hover.Length));
        Assert.Equal(ScriptText.Hover_Target, hover.Paragraphs[0]);
        Assert.Equal(
            TextFormat.Format(ScriptText.Hover_TargetFound, "`CUSTOMER.ID`", "NUMBER(10)") + " "
            + TextFormat.Plural(1, ScriptText.Hover_TargetShowsOnOne, ScriptText.Hover_TargetShowsOnOther, "`ORDERS.CUSTOMER_ID`"),
            hover.Paragraphs[1]);
        Assert.Equal(ScriptText.Hover_More, hover.Paragraphs[^1]);
    }

    [Fact]
    public async Task The_hover_over_a_target_names_a_missing_column_and_one_nothing_refers_to()
    {
        var schema = await SchemaAsync();

        var missing = await ScriptHeaderHelp.HoverAsync("-- @target id ORDERS.NOPE\nSELECT :id FROM dual", 3, schema, Token);
        var alone = await ScriptHeaderHelp.HoverAsync("-- @target id ORDERS.CUSTOMER_ID\nSELECT :id FROM dual", 3, schema, Token);

        Assert.Equal(TextFormat.Format(ScriptText.Target_ColumnMissing, "`ORDERS`", "`NOPE`"), missing!.Paragraphs[1]);
        Assert.EndsWith(ScriptText.Hover_TargetAlone, alone!.Paragraphs[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hover_explains_each_directive_and_nothing_on_SQL()
    {
        const string text = "-- @description Orders\n-- @param id number\n-- @tagret id ORDERS.ID\nSELECT * FROM orders WHERE id = :id\n-- @param x text";

        Assert.Equal(ScriptText.Hover_Description, (await ScriptHeaderHelp.HoverAsync(text, 4, null, Token))!.Paragraphs[0]);
        Assert.StartsWith("**@param**", (await ScriptHeaderHelp.HoverAsync(text, 30, null, Token))!.Paragraphs[0], StringComparison.Ordinal);
        Assert.Equal(TextFormat.Format(ScriptText.Hover_UnknownSimilar, "`@tagret`", "`@target`"), (await ScriptHeaderHelp.HoverAsync(text, 50, null, Token))!.Paragraphs[0]);
        Assert.Null(await ScriptHeaderHelp.HoverAsync(text, text.IndexOf("FROM", StringComparison.Ordinal), null, Token));
        Assert.Equal([ScriptText.Hover_NotHeader], (await ScriptHeaderHelp.HoverAsync(text, text.Length - 2, null, Token))!.Paragraphs);
    }

    [Fact]
    public void A_name_in_a_code_span_cannot_end_it()
    {
        Assert.Equal("`a'b c`", ScriptHeaderHelp.Code("a`b\nc"));
    }

    private static async Task<IReadOnlyList<SqlCompletionItem>?> CompleteAsync(string marked, SchemaCache? schema = null)
    {
        var cursor = marked.IndexOf('|', StringComparison.Ordinal);
        return await ScriptHeaderHelp.CompleteAsync(marked.Remove(cursor, 1), cursor, schema, Token);
    }

    [Fact]
    public async Task After_the_at_sign_the_directives_are_offered()
    {
        var items = await CompleteAsync("-- @|\nSELECT 1 FROM dual");

        Assert.Equal(["description", "param", "target"], items!.Select(i => i.Label));
        Assert.Equal("param ", items![1].InsertText);
        Assert.Equal(["description", "param", "target"], (await CompleteAsync("-- @description x\r\n--@ta|"))!.Select(i => i.Label));
    }

    [Fact]
    public async Task A_param_gets_its_types_then_list()
    {
        Assert.Equal(["text", "char", "number", "date", "raw", "null"], (await CompleteAsync("-- @param id |\nSELECT :id FROM dual"))!.Select(i => i.Label));
        Assert.Equal(["text", "char", "number", "date", "raw", "null"], (await CompleteAsync("-- @param \"a b\" nu|\nSELECT :id FROM dual"))!.Select(i => i.Label));
        Assert.Equal(["list"], (await CompleteAsync("-- @param id number |\nSELECT :id FROM dual"))!.Select(i => i.Label));
    }

    [Fact]
    public async Task Param_and_target_offer_the_binds_of_the_SQL_param_only_those_without_one()
    {
        const string sql = "\nSELECT * FROM orders WHERE id = :id AND customer_id = :customer AND x = :\"a b\"";

        Assert.Equal(["id", "customer", "\"a b\""], (await CompleteAsync("-- @target |" + sql))!.Select(i => i.InsertText));
        Assert.Equal(["customer", "a b"], (await CompleteAsync("-- @param id number\n-- @param cu|" + sql))!.Select(i => i.Label));
        Assert.All((await CompleteAsync("-- @target |" + sql))!, i => Assert.Equal(SqlCompletionKind.Variable, i.Kind));
    }

    [Fact]
    public async Task A_target_offers_tables_without_owner_then_the_columns_of_the_one_named()
    {
        var schema = await SchemaAsync();

        var tables = await CompleteAsync("-- @target id |\nSELECT :id FROM dual", schema);
        var columns = await CompleteAsync("-- @target id customer.|\nSELECT :id FROM dual", schema);
        var synonym = await CompleteAsync("-- @target id country.n|\nSELECT :id FROM dual", schema);

        Assert.Equal(["COUNTRY", "CUSTOMER", "ORDERS"], tables!.Select(i => i.Label).Order());
        Assert.Equal(["ID", "NAME"], columns!.Select(i => i.Label));
        Assert.Equal("NUMBER(10)", columns![0].Detail);
        Assert.Equal(["CODE", "NAME"], synonym!.Select(i => i.Label));
        Assert.Empty((await CompleteAsync("-- @target id |\nSELECT :id FROM dual"))!); // no schema
    }

    [Theory]
    [InlineData("SELECT |")]
    [InlineData("SELECT 1 FROM dual;\n-- @|")]
    [InlineData("-- a comment\n-- @|")]
    [InlineData("-- @param id number\n\n-- @|")]
    [InlineData("-- not a header @|")]
    public async Task Outside_a_header_line_the_SQL_completion_answers(string marked)
    {
        Assert.Null(await CompleteAsync(marked));
    }

    [Fact]
    public async Task On_a_header_line_without_a_completion_nothing_is_offered()
    {
        Assert.Empty((await CompleteAsync("-- @description Orders of |\nSELECT 1 FROM dual"))!);
    }

    [Theory]
    [InlineData("-- @target id ORDERS.ID\n\nSELECT * FROM orders WHERE id = :id", "SELECT * FROM orders WHERE id = :id")]
    [InlineData("-- @target id ORDERS.ID\r\n-- @x y\r\nSELECT *\r\n  FROM orders", "SELECT *\n  FROM orders")]
    [InlineData("-- a comment\nSELECT 1 FROM dual", "-- a comment\nSELECT 1 FROM dual")]
    [InlineData("SELECT 1 FROM dual", "SELECT 1 FROM dual")]
    [InlineData("-- @target id ORDERS.ID", "-- @target id ORDERS.ID")]
    public void A_statement_is_shown_without_its_header_lines(string statement, string shown)
    {
        Assert.Equal(shown, ScriptHeaderHelp.WithoutHeader(statement));
    }

    [Fact]
    public void Lines_split_where_the_header_reader_splits()
    {
        const string text = "a\r\nb\rc\nd\u0085e\u2028f\u2029g\fh";

        var (spans, lines) = ScriptHeaderHelp.Lines(text);

        Assert.Equal(text.ReplaceLineEndings("\n").Split('\n'), lines);
        Assert.All(spans.Zip(lines), s => Assert.Equal(s.Second, text.Substring(s.First.Start, s.First.Length)));
    }

    [Theory]
    [InlineData("tagret", "target")]
    [InlineData("parm", "param")]
    [InlineData("descripton", "description")]
    [InlineData("owner", null)]
    [InlineData("ta", null)]
    public void A_typo_is_matched_to_the_directive_it_is_close_to(string typed, string? meant)
    {
        Assert.Equal(meant, ScriptHeaderHelp.Similar(typed));
    }
}
