using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// The one place a failed statement goes into the log with its text and binds (review 3.40.0, TE-4): passwords never, and on
/// Prod neither bind values nor literals of the text (SE-6) – the error message stays.
/// </summary>
public sealed class QueryErrorLogTests
{
    private static readonly QuerySpec Statement = new(
        "UPDATE kunden SET name = 'Müller' WHERE id = :id; ALTER USER app IDENTIFIED BY geheim",
        [new QueryParameter("id", 4711m)]);

    private static DatabaseException Failure() =>
        new("ORA-20001: Kunde Müller gesperrt", "ORA-20001") { Statement = Statement };

    [Fact]
    public void On_prod_neither_binds_nor_literals_nor_passwords_go_into_the_log()
    {
        var logger = new ListLogger();

        QueryErrorLog.Log(logger, Failure(), TestApp.Profile("Prod", ConnectionKind.Prod));

        var line = Assert.Single(logger.Lines);
        Assert.DoesNotContain("4711", line);
        Assert.DoesNotContain("'Müller'", line);
        Assert.DoesNotContain("geheim", line);
        Assert.Contains("ORA-20001: Kunde Müller gesperrt", line); // the message says what failed
    }

    [Fact]
    public void Elsewhere_the_values_stay_but_passwords_do_not()
    {
        var logger = new ListLogger();

        QueryErrorLog.Log(logger, Failure(), TestApp.Profile("Test"));

        var line = Assert.Single(logger.Lines);
        Assert.Contains("4711", line);
        Assert.Contains("'Müller'", line);
        Assert.DoesNotContain("geheim", line);
    }

    [Fact]
    public void Without_a_profile_the_values_stay_as_on_test()
    {
        var logger = new ListLogger();

        QueryErrorLog.Log(logger, Failure(), profile: null);

        Assert.Contains("4711", Assert.Single(logger.Lines));
    }
}
