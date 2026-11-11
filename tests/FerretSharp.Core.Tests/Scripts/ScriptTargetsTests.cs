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

    [Fact]
    public void A_run_gets_the_cells_value_for_the_targets_parameter_and_asks_for_the_others()
    {
        ScriptParameter[] parameters = [new("id", SqlVariableType.Number), new("since", SqlVariableType.Date), new("none", SqlVariableType.Null)];
        var column = new ColumnInfo("CUSTOMER_ID", "NUMBER", null, false, 10, 0, false, false, null, 1);

        var run = ScriptTargetRun.Of(parameters, new ScriptTarget("ID", "CUSTOMER", "ID"), "SELECT * FROM orders WHERE customer_id = :id",
            column, 4711m, new Dictionary<string, string> { ["since"] = "2026-01-01" }, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(run);
        Assert.Equal(
            [new SqlVariable("id", SqlVariableType.Number, "4711"), new SqlVariable("since", SqlVariableType.Date, "2026-01-01"), new SqlVariable("none", SqlVariableType.Null, "")],
            run.Variables);
        Assert.Equal([parameters[1]], run.Missing); // NULL needs no value
        Assert.True(run.ReadOnly);
    }

    [Fact]
    public void A_national_column_binds_its_text_nationally_and_a_date_in_iso_form()
    {
        var nvarchar = new ColumnInfo("NAME", "NVARCHAR2", 50, true, null, null, true, false, null, 1);
        var date = new ColumnInfo("BORN", "DATE", null, false, null, null, true, false, null, 2);
        var empty = new Dictionary<string, string>();

        var text = ScriptTargetRun.Of([new("n", SqlVariableType.Text)], new("n", "T", "NAME"), "SELECT :n FROM dual", nvarchar, "Ünal", empty, out _)!;
        var day = ScriptTargetRun.Of([new("d", SqlVariableType.Date)], new("d", "T", "BORN"), "SELECT :d FROM dual", date, new DateTime(2026, 11, 11, 8, 30, 0), empty, out _)!;

        Assert.True(text.Variables[0].National);
        Assert.Equal("Ünal", text.Variables[0].Value);
        Assert.Equal("2026-11-11 08:30:00", day.Variables[0].Value);
        Assert.True(ValueInput.TryParseDate(day.Variables[0].Value, out var parsed, out _));
        Assert.Equal(new DateTime(2026, 11, 11, 8, 30, 0), parsed);
    }

    [Fact]
    public void Null_and_values_that_cannot_be_compared_are_refused()
    {
        var number = new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1);
        var clob = new ColumnInfo("NOTE", "CLOB", null, false, null, null, true, false, null, 2);

        Assert.Null(ScriptTargetRun.Of([new("id", SqlVariableType.Number)], new("id", "T", "ID"), "SELECT 1 FROM dual", number, null, new Dictionary<string, string>(), out var isNull));
        Assert.NotNull(isNull);
        Assert.NotNull(ScriptTargetRun.Refusal(clob, "text"));
        Assert.Null(ScriptTargetRun.Refusal(number, 1m));
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
