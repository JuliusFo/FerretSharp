using System.Globalization;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>"Last used" on the connections page and in the connection's menu (WP-66; review 3.54.0, C6: no test).</summary>
public sealed class ConnectionLastUsedTests
{
    private static readonly DateTime Today = new(2026, 10, 29);

    private static DateTimeOffset At(DateTime local) => new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    [Theory]
    [InlineData(0, "heute 09:12")]
    [InlineData(1, "gestern 09:12")]
    [InlineData(3, "vor 3 Tagen")]
    public void Recent_days_are_named_older_ones_dated(int daysAgo, string expected)
    {
        var used = At(Today.AddDays(-daysAgo).AddHours(9).AddMinutes(12));

        Assert.Equal(expected, ConnectionLastUsed.Text(used, Today));
    }

    [Fact]
    public void A_week_and_more_gives_the_date_and_never_nothing()
    {
        var used = At(Today.AddDays(-7).AddHours(9));

        Assert.Equal(used.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture), ConnectionLastUsed.Text(used, Today));
        Assert.Equal("", ConnectionLastUsed.Text(new Dictionary<Guid, DateTimeOffset>(), Guid.NewGuid(), Today));
    }
}
