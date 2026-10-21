using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// "Copy" in the error dialog (review 3.40.0, A8 and A24; review 3.44.0, C1: the text was built in the dialog, untested):
/// never a password, on Prod no bind value.
/// </summary>
public sealed class ErrorReportTests
{
    private static readonly DatabaseException Failure = new("invalid credential", "ORA-01017")
    {
        Statement = new QuerySpec("ALTER USER app IDENTIFIED BY geheim; UPDATE kunden SET name = :name", [new QueryParameter("name", "Müller")]),
    };

    [Fact]
    public void A_password_never_reaches_the_clipboard()
    {
        Assert.DoesNotContain("geheim", ErrorReport.Text(Failure, mask: false));
        Assert.DoesNotContain("geheim", ErrorReport.Text(Failure, mask: true));
    }

    [Fact]
    public void Bind_values_only_where_the_connection_is_not_prod()
    {
        Assert.Contains("Müller", ErrorReport.Text(Failure, mask: false));
        Assert.DoesNotContain("Müller", ErrorReport.Text(Failure, mask: true));
        Assert.StartsWith("ORA-01017: invalid credential", ErrorReport.Text(Failure, mask: true));
    }

    [Fact]
    public void Without_a_statement_the_message_alone() =>
        Assert.Equal("ORA-00942: table or view does not exist", ErrorReport.Text(new DatabaseException("table or view does not exist", "ORA-00942"), mask: true));
}
