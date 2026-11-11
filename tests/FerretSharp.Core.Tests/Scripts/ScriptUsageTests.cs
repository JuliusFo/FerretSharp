using FerretSharp.Core.Query;
using FerretSharp.Core.Scripts;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.Scripts;

/// <summary>WP-84: the scripts last started from a cell come first; typed values stay in memory only.</summary>
public sealed class ScriptUsageTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static ScriptEntry Script(ScriptRootKind kind, string path, string? group = null) =>
        new(new ScriptRoot(kind, "root", group), path, null, [new("id", SqlVariableType.Number)], [], DateTime.UnixEpoch, 1);

    private static ScriptMatch Match(ScriptEntry script) => new(script, new ScriptTarget("id", "T", "ID"));

    [Fact]
    public async Task Scripts_used_last_come_first_and_the_file_holds_no_values()
    {
        var file = _folder.Combine("script-usage.json");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-12T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var a = Script(ScriptRootKind.Personal, "a.sql");
        var b = Script(ScriptRootKind.Group, "orders/b.sql", "ERP");
        var c = Script(ScriptRootKind.General, "c.sql");
        var usage = new ScriptUsage(file, time);

        await usage.UsedAsync(b, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await usage.UsedAsync(c, TestContext.Current.CancellationToken);
        usage.Remember(c, [new("since", "2026-01-01")]);

        var loaded = new ScriptUsage(file);
        await loaded.LoadAsync(TestContext.Current.CancellationToken);
        // The same file in another group's folder, or by another case, is the same script only where it is (group case ignored).
        var sameB = Script(ScriptRootKind.Group, "ORDERS/B.sql", " erp ");
        var otherGroup = Script(ScriptRootKind.Group, "orders/b.sql", "Shop");
        Assert.Equal(
            [c, sameB, a, otherGroup],
            loaded.Ordered([Match(a), Match(sameB), Match(otherGroup), Match(c)]).Select(m => m.Script));
        Assert.DoesNotContain("2026-01-01", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Empty(loaded.ValuesOf(c));
        Assert.Equal("2026-01-01", usage.ValuesOf(c)["SINCE"]);
    }

    [Fact]
    public async Task A_broken_file_means_none_used_and_a_newer_one_is_kept()
    {
        var file = _folder.Combine("script-usage.json");
        var a = Script(ScriptRootKind.Personal, "a.sql");
        File.WriteAllText(file, """{ "version": 1, "uses": [null, { "root": "Personal", "path": "" }, { "root": "Personal", "path": "a.sql", "lastUsed": "2026-11-01T00:00:00Z" }] }""");
        var usage = new ScriptUsage(file);
        await usage.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(a, usage.Ordered([Match(Script(ScriptRootKind.General, "b.sql")), Match(a)])[0].Script);

        const string newer = """{ "version": 99, "somethingNew": true }""";
        File.WriteAllText(file, newer);
        await usage.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(await usage.UsedAsync(a, TestContext.Current.CancellationToken));
        Assert.Equal(newer, File.ReadAllText(file));

        File.WriteAllText(file, "{ not json");
        await usage.LoadAsync(TestContext.Current.CancellationToken);
        var b = Script(ScriptRootKind.General, "b.sql");
        Assert.Equal([b, a], usage.Ordered([Match(b), Match(a)]).Select(m => m.Script));
    }
}
