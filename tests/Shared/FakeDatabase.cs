using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Testing;

/// <summary>
/// The database side of a connection as NSubstitute stand-ins, for tests that connect without a database: a schema
/// reader that answers what loading the schema asks, a connection around it and a connector that opens it. Tests add
/// their own answers on top. Linked into every test project.
/// </summary>
internal static class FakeDatabase
{
    /// <summary>A schema reader that lists the tables, no synonym targets, no foreign or primary keys and no PL/SQL.</summary>
    public static ISchemaReader Reader(params TableSummary[] tables)
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(tables);
        reader.GetSynonymTargetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetPrimaryKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        reader.GetPlSqlObjectsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        return reader;
    }

    /// <summary>A connection whose schema is <paramref name="reader"/> (<see cref="Reader"/> without tables if null).</summary>
    public static IDatabaseConnection Connection(ISchemaReader? reader = null, string serverVersion = "23.26.0.0.0")
    {
        reader ??= Reader(); // before: NSubstitute cannot configure a substitute inside Returns()
        var connection = Substitute.For<IDatabaseConnection>();
        connection.Schema.Returns(reader);
        connection.ServerVersion.Returns(serverVersion);
        return connection;
    }

    /// <summary>A connector that opens <paramref name="connection"/> for every profile and action.</summary>
    public static IDatabaseConnector Connector(IDatabaseConnection connection) => Connector(_ => connection);

    /// <summary>A connector that opens what <paramref name="open"/> returns for the action (MODULE/ACTION of the session).</summary>
    public static IDatabaseConnector Connector(Func<string, IDatabaseConnection> open)
    {
        var connector = Substitute.For<IDatabaseConnector>();
        connector.Opens(open);
        return connector;
    }

    /// <summary>From now on every open of the connector returns what <paramref name="open"/> returns for the action.</summary>
    public static void Opens(this IDatabaseConnector connector, Func<string, IDatabaseConnection> open) =>
        connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(open(call.ArgAt<string>(2))));
}
