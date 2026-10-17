using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Data;

/// <summary>WP-52: session tags, long operations and remembering missing rights on the V$ views.</summary>
public sealed class SessionActivityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Session_tags_are_unique_ascii_and_survive_the_session_attribute_rules()
    {
        var tags = Enumerable.Range(0, 100).Select(_ => OracleSessionDefaults.NewSessionTag()).ToList();

        Assert.Equal(100, tags.Distinct().Count());
        Assert.All(tags, tag =>
        {
            Assert.StartsWith("FerretSharp ", tag);
            Assert.Equal("FerretSharp ".Length + 12, tag.Length);
            Assert.Equal(tag, OracleSession.ToSessionAttribute(tag));
        });
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(43, 100, 43)]
    [InlineData(996, 1000, 99)] // never 100 % while it runs
    [InlineData(5, 0, 0)]
    public void Long_operations_show_whole_percent(long done, long total, int percent) =>
        Assert.Equal(percent, new LongOperation("Table Scan", "APP.KUNDEN", done, total, "Blocks", null).Percent);

    [Fact]
    public async Task Without_rights_on_the_v_views_the_connection_asks_only_once()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetSessionActivityAsync("tag", Arg.Any<CancellationToken>()).Returns((SessionActivity?)null);
        var cache = new SchemaCache(reader, "APP");

        Assert.Null(await cache.GetSessionActivityAsync("tag", Ct));
        Assert.Null(await cache.GetSessionActivityAsync("tag", Ct));

        Assert.True(cache.SessionActivityUnavailable);
        await reader.Received(1).GetSessionActivityAsync("tag", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_rights_every_look_goes_to_the_database()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetSessionActivityAsync("tag", Arg.Any<CancellationToken>()).Returns(SessionActivity.None);
        var cache = new SchemaCache(reader, "APP");

        Assert.Same(SessionActivity.None, await cache.GetSessionActivityAsync("tag", Ct));
        Assert.Same(SessionActivity.None, await cache.GetSessionActivityAsync("tag", Ct));

        Assert.False(cache.SessionActivityUnavailable);
        await reader.Received(2).GetSessionActivityAsync("tag", Arg.Any<CancellationToken>());
    }
}
