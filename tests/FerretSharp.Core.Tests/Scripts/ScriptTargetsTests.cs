using FerretSharp.Core.Resources;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Scripts;

namespace FerretSharp.Core.Tests.Scripts;

/// <summary>WP-84: which scripts the cell menu of a column offers, and what a run from a cell gets.</summary>
public sealed class ScriptTargetsTests
{
    private static readonly TableRef Customer = new("APP", "CUSTOMER");
    private static readonly TableRef Orders = new("APP", "ORDERS");
    private static readonly TableRef Invoice = new("APP", "INVOICE");
    private static readonly TableRef Person = new("APP", "PERSON");
    private static readonly TableRef Supplier = new("APP", "SUPPLIER");

    private static readonly ForeignKeyInfo[] Links =
    [
        new("FK_ORDERS_CUSTOMER", Orders, ["CUSTOMER_ID"], Customer, ["ID"], FkSource.Declared),
        new("FK_INVOICE_ORDER", Invoice, ["ORDER_ID"], Orders, ["ID"], FkSource.Convention),
        new("FK_INVOICE_CUSTOMER", Invoice, ["TENANT", "CUSTOMER_NO"], Customer, ["TENANT", "NO"], FkSource.Manual),
        new("FK_CUSTOMER_PERSON", Customer, ["ID"], Person, ["ID"], FkSource.ClrModel),
        new("FK_SUPPLIER_PERSON", Supplier, ["ID"], Person, ["ID"], FkSource.Declared),
    ];

    private static IEnumerable<ForeignKeyInfo> LinksOf(TableRef table) => Links.Where(l => l.From == table);

    private static ScriptEntry Script(string name, params ScriptTarget[] targets) =>
        new(new ScriptRoot(ScriptRootKind.Personal, "C:\\scripts"), name + ".sql", null, [new("id", SqlVariableType.Number)], [], DateTime.UnixEpoch, 1)
        {
            Targets = targets,
        };

    private static TableRef? Resolve(string table) => table switch
    {
        "CUSTOMER" => Customer,
        "ORDERS" => Orders,
        "PERSON" => Person,
        _ => null,
    };

    [Fact]
    public void A_column_reaches_itself_and_what_it_refers_to_along_every_source_and_position()
    {
        var reached = ScriptTargets.Reached(new ColumnRef(Invoice, "ORDER_ID"), LinksOf);

        Assert.Equal(
            new HashSet<ColumnRef> { new(Invoice, "ORDER_ID"), new(Orders, "ID") },
            reached.ToHashSet());
        // The second column of a relationship over two maps to the second column on the other side.
        Assert.Contains(new ColumnRef(Customer, "NO"), ScriptTargets.Reached(new ColumnRef(Invoice, "CUSTOMER_NO"), LinksOf));
        // Two steps: a customer's key is also a person's.
        Assert.Contains(new ColumnRef(Person, "ID"), ScriptTargets.Reached(new ColumnRef(Orders, "CUSTOMER_ID"), LinksOf));
    }

    [Fact]
    public void The_columns_reaching_a_target_are_exactly_those_whose_cell_menu_offers_it()
    {
        TableRef[] tables = [Customer, Orders, Invoice, Person, Supplier];
        var columns = Links.SelectMany(l => l.FromColumns.Select(c => new ColumnRef(l.From, c)).Concat(l.ToColumns.Select(c => new ColumnRef(l.To, c))))
            .Distinct().ToList();

        foreach (var target in columns)
        {
            var reaching = ScriptTargets.ReachedBy(target, tables, LinksOf);

            Assert.Equal(target, reaching[0]);
            Assert.Equal(columns.Where(c => ScriptTargets.Reached(c, LinksOf).Contains(target)).ToHashSet(), reaching.ToHashSet());
        }
    }

    [Fact]
    public void A_cycle_of_relationships_ends()
    {
        ForeignKeyInfo[] cycle =
        [
            new("A_B", Orders, ["X"], Invoice, ["Y"], FkSource.Manual),
            new("B_A", Invoice, ["Y"], Orders, ["X"], FkSource.Manual),
        ];

        var reached = ScriptTargets.Reached(new ColumnRef(Orders, "X"), t => cycle.Where(c => c.From == t));

        Assert.Equal(2, reached.Count);
    }

    [Fact]
    public void A_script_shows_on_its_target_and_on_every_column_that_refers_to_it_but_not_on_siblings_or_parents()
    {
        var byCustomer = Script("Orders of a customer", new ScriptTarget("id", "CUSTOMER", "ID"));
        var byOrder = Script("Positions", new ScriptTarget("id", "ORDERS", "ID"));
        var elsewhere = Script("Other product", new ScriptTarget("id", "MISSING", "ID"));
        ScriptEntry[] scripts = [byCustomer, byOrder, elsewhere];

        Assert.Equal([byCustomer], ScriptTargets.For(new(Customer, "ID"), scripts, Resolve, LinksOf).Select(m => m.Script));
        Assert.Equal([byCustomer], ScriptTargets.For(new(Orders, "CUSTOMER_ID"), scripts, Resolve, LinksOf).Select(m => m.Script));
        Assert.Equal([byOrder], ScriptTargets.For(new(Invoice, "ORDER_ID"), scripts, Resolve, LinksOf).Select(m => m.Script));
        // A supplier is a person too, but no customer: the shared base table does not make the script fit.
        Assert.Empty(ScriptTargets.For(new(Supplier, "ID"), scripts, Resolve, LinksOf));
        Assert.Empty(ScriptTargets.For(new(Person, "ID"), scripts, Resolve, LinksOf));
        Assert.Empty(ScriptTargets.For(new(Orders, "AMOUNT"), scripts, Resolve, LinksOf));
    }

    [Fact]
    public void A_script_is_offered_once_by_its_first_fitting_target()
    {
        var script = Script("Both", new ScriptTarget("id", "ORDERS", "ID"), new ScriptTarget("id", "CUSTOMER", "ID"));

        var matches = ScriptTargets.For(new(Invoice, "ORDER_ID"), [script], Resolve, LinksOf);

        Assert.Equal([new ScriptMatch(script, script.Targets[0])], matches);
    }

    [Fact]
    public void A_target_names_an_own_object_first_and_else_the_one_a_synonym_leads_to()
    {
        TableSummary[] tables =
        [
            new("OTHER", "CUSTOMER", TableKind.Table, new SynonymInfo("APP", "CUSTOMER")),
            new("APP", "CUSTOMER", TableKind.Table),
            new("SHARED", "COUNTRIES", TableKind.View, new SynonymInfo(SynonymInfo.PublicOwner, "COUNTRY")),
        ];

        Assert.Equal(new TableRef("APP", "CUSTOMER"), ScriptTargets.Resolve("CUSTOMER", tables, "APP"));
        Assert.Equal(new TableRef("SHARED", "COUNTRIES"), ScriptTargets.Resolve("COUNTRY", tables, "APP"));
        Assert.Null(ScriptTargets.Resolve("COUNTRIES", tables, "APP")); // only by the name users know it by
        Assert.Null(ScriptTargets.Resolve("customer", tables, "APP"));
    }

    private static readonly ColumnInfo Number = new("CUSTOMER_ID", "NUMBER", null, false, 10, 0, true, false, null, 1);
    private static readonly Dictionary<string, string> NoneTyped = new();

    private static ScriptHeader Header(params ScriptParameter[] parameters) =>
        new(null, parameters, []) { Targets = [new(parameters[0].Name, "CUSTOMER", "ID")] };

    [Fact]
    public void A_run_gets_the_clicked_cells_value_for_the_targets_parameter_and_asks_for_the_others()
    {
        var header = Header(new ScriptParameter("id", SqlVariableType.Number), new ScriptParameter("since", SqlVariableType.Date), new ScriptParameter("none", SqlVariableType.Null));
        const string body = "SELECT * FROM orders WHERE customer_id = :id";

        var run = ScriptTargetRun.Of(header, body, header.Targets[0], Number, [4711m, 4712m],
            new Dictionary<string, string> { ["SINCE"] = "2026-01-01" }, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(run);
        Assert.Equal(body, run.Text);
        Assert.Equal(
            [new SqlVariable("id", SqlVariableType.Number, "4711"), new SqlVariable("since", SqlVariableType.Date, "2026-01-01"), new SqlVariable("none", SqlVariableType.Null, "")],
            run.Variables);
        Assert.Equal([header.Parameters[1]], run.Missing); // NULL needs no value
        Assert.False(run.Spread);
        Assert.True(run.ReadOnly);
    }

    [Fact]
    public void A_list_parameter_takes_each_value_of_the_marked_rows_once_and_spreads_the_text()
    {
        var header = Header(new ScriptParameter("ids", SqlVariableType.Number, List: true));

        var run = ScriptTargetRun.Of(header, "SELECT * FROM orders WHERE customer_id IN (:ids)", header.Targets[0], Number, [2m, null, 1m, 2m], NoneTyped, out _)!;
        var single = ScriptTargetRun.Of(header, "SELECT * FROM orders WHERE customer_id IN (:ids)", header.Targets[0], Number, [null, 7m, null], NoneTyped, out _)!;

        Assert.Equal("SELECT * FROM orders WHERE customer_id IN (:ids_1, :ids_2)", run.Text);
        Assert.Equal([new SqlVariable("ids_1", SqlVariableType.Number, "2"), new SqlVariable("ids_2", SqlVariableType.Number, "1")], run.Variables);
        Assert.True(run.Spread);
        Assert.Equal(2, run.Values);
        // One value left: a plain bind, the text as the file has it (the tab stays linked).
        Assert.Equal("SELECT * FROM orders WHERE customer_id IN (:ids)", single.Text);
        Assert.Equal([new SqlVariable("ids", SqlVariableType.Number, "7")], single.Variables);
        Assert.False(single.Spread);
    }

    [Fact]
    public void A_national_column_binds_its_text_nationally_and_a_date_in_iso_form()
    {
        var nvarchar = new ColumnInfo("NAME", "NVARCHAR2", 50, true, null, null, true, false, null, 1);
        var date = new ColumnInfo("BORN", "DATE", null, false, null, null, true, false, null, 2);
        var text = Header(new ScriptParameter("n", SqlVariableType.Text));
        var day = Header(new ScriptParameter("d", SqlVariableType.Date));

        var name = ScriptTargetRun.Of(text, "SELECT :n FROM dual", text.Targets[0], nvarchar, ["Ünal"], NoneTyped, out _)!;
        var born = ScriptTargetRun.Of(day, "SELECT :d FROM dual", day.Targets[0], date, [new DateTime(2026, 11, 11, 8, 30, 0)], NoneTyped, out _)!;

        Assert.True(name.Variables[0].National);
        Assert.Equal("Ünal", name.Variables[0].Value);
        Assert.Equal("2026-11-11 08:30:00", born.Variables[0].Value);
        Assert.True(ValueInput.TryParseDate(born.Variables[0].Value, out var parsed, out _));
        Assert.Equal(new DateTime(2026, 11, 11, 8, 30, 0), parsed);
    }

    [Fact]
    public void Null_values_that_cannot_be_compared_a_target_the_file_lost_and_a_list_out_of_place_are_refused()
    {
        var clob = new ColumnInfo("NOTE", "CLOB", null, false, null, null, true, false, null, 2);
        var plain = Header(new ScriptParameter("id", SqlVariableType.Number));
        var list = Header(new ScriptParameter("ids", SqlVariableType.Number, List: true));
        const string body = "SELECT * FROM t WHERE id = :id";

        Assert.Null(ScriptTargetRun.Of(plain, body, plain.Targets[0], Number, [null, 1m], NoneTyped, out var isNull)); // the clicked cell counts
        Assert.Equal(ScriptText.Target_ValueIsNull, isNull);
        Assert.Null(ScriptTargetRun.Of(plain, body, plain.Targets[0], clob, ["text"], NoneTyped, out var lob));
        Assert.Equal(ScriptText.Target_ValueNotPassable, lob);
        Assert.Null(ScriptTargetRun.Of(plain, body, new ScriptTarget("id", "ORDERS", "ID"), Number, [1m], NoneTyped, out var gone));
        Assert.Equal(ScriptText.Target_Gone, gone);
        Assert.Null(ScriptTargetRun.Of(list, "SELECT * FROM t WHERE id = :ids", list.Targets[0], Number, [1m, 2m], NoneTyped, out var placement));
        Assert.Contains(":ids", placement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT 1 FROM dual", true)]
    [InlineData("-- note\nWITH a AS (SELECT 1 x FROM dual) SELECT * FROM a;\nSELECT 2 FROM dual;", true)]
    [InlineData("SELECT * FROM t FOR UPDATE", false)]
    [InlineData("SELECT 1 FROM dual;\nDELETE FROM t", false)]
    [InlineData("BEGIN NULL; END;", false)]
    [InlineData("-- only a comment", false)]
    public void Only_a_script_of_plain_queries_runs_at_once(string body, bool readOnly) =>
        Assert.Equal(readOnly, ScriptTargetRun.IsReadOnly(body));
}
