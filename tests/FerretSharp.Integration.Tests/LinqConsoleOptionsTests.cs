using FerretSharp.Core.ClrModel;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The LINQ console never reaches the database, also for contexts that bring their own connection string (review 3.31.0,
/// A1): with a design-time factory it takes the factory's options and adds its interceptors; a context that configures
/// itself in <c>OnConfiguring</c> is refused. The connection strings of tests/FerretSharp.FakeEfModel point at a port
/// nobody listens on – a command that got through would fail instead of being captured.
/// </summary>
public sealed class LinqConsoleOptionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_context_from_a_design_time_factory_gets_the_interceptors_and_never_connects()
    {
        var link = ModelHostTests.FakeEfModelLink("FactoryContext");
        await using var console = await ModelHostTests.Runner().StartConsoleAsync(link, BuildOutputLocator.Find(link), Ct);

        var delete = await console.RunAsync("return db.Things.Where(t => t.Id > 1).ExecuteDelete();", "", Ct);
        var read = await console.RunAsync("return await db.Things.ToListAsync(ct);", "", Ct);

        Assert.Null(delete.Exception);
        var command = Assert.Single(delete.Commands);
        Assert.Equal(LinqProtocol.NonQuery, command.Kind);
        Assert.Contains("DELETE FROM \"THINGS\"", command.Sql, StringComparison.Ordinal);
        Assert.Null(read.Exception);
        Assert.Equal(LinqProtocol.Reader, Assert.Single(read.Commands).Kind);
    }

    [Fact]
    public async Task A_context_that_configures_itself_is_refused_by_the_console_but_its_model_is_read()
    {
        var link = ModelHostTests.FakeEfModelLink("SelfConfiguredContext");

        var failure = await Assert.ThrowsAsync<ClrModelException>(() => ModelHostTests.Runner("en-US").StartConsoleAsync(link, BuildOutputLocator.Find(link), Ct));
        var model = await ModelHostTests.Runner().ReadModelAsync(link, BuildOutputLocator.Find(link), Ct);

        Assert.Equal(ModelHostErrorKind.ConsoleNeedsOptions, failure.Kind);
        Assert.Contains("SelfConfiguredContext", failure.Message, StringComparison.Ordinal);
        Assert.Equal("parameterless", model.Model?.CreatedBy);
    }

    [Fact]
    public async Task The_model_of_a_factory_context_is_still_read_through_the_factory()
    {
        var link = ModelHostTests.FakeEfModelLink("FactoryContext");

        var model = await ModelHostTests.Runner().ReadModelAsync(link, BuildOutputLocator.Find(link), Ct);

        Assert.Equal("factory", model.Model?.CreatedBy);
    }
}
