using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>The group in front of a connection's name: connections of different groups often share a name (DEV, TST, PRD).</summary>
public sealed class ConnectionLabelTests
{
    private static ConnectionProfile Profile(string name, string? group) =>
        new(Guid.NewGuid(), name, ConnectionKind.Dev, new HostPortAddress("db01", 1521, "ERP", null), "erp", null, false, group);

    [Fact]
    public void The_group_comes_first_by_default()
    {
        Assert.True(AppSettings.Default.ShowConnectionGroup);
        Assert.Equal("Kunde A › DEV", ConnectionLabel.Of(Profile("DEV", "Kunde A"), AppSettings.Default));
        Assert.Equal("Kunde A", ConnectionLabel.GroupOf(Profile("DEV", " Kunde A "), AppSettings.Default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_connection_without_a_group_keeps_its_name(string? group)
    {
        Assert.Equal("DEV", ConnectionLabel.Of(Profile("DEV", group), AppSettings.Default));
        Assert.Null(ConnectionLabel.GroupOf(Profile("DEV", group), AppSettings.Default));
    }

    [Fact]
    public void Switched_off_only_the_name_shows()
    {
        var off = AppSettings.Default with { ShowConnectionGroup = false };
        Assert.Equal("DEV", ConnectionLabel.Of(Profile("DEV", "Kunde A"), off));
        Assert.Null(ConnectionLabel.GroupOf(Profile("DEV", "Kunde A"), off));
    }
}
