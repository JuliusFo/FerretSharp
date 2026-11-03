using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;
using FerretSharp.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.ClrModel.Report;

/// <summary>WP-72: the report's exceptions file – shared through a repository, so foreign input, and never broken by a write.</summary>
public sealed class ReportExceptionSourceTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly FakeTimeProvider _clock = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DefaultPath => _folder.Combine("data", "report-exceptions.json");

    private ReportExceptionSource Source(AppSettings settings) =>
        new(new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), settings), DefaultPath, _clock);

    private static ConnectionProfile Profile(string? group) =>
        new(Guid.NewGuid(), "DEV", ConnectionKind.Dev, new HostPortAddress("db", 1521, "FREE", null), "shop", null, ReadOnly: false, Group: group);

    [Theory]
    [InlineData("AUD_*", "AUD_AUFTRAG", true)]
    [InlineData("AUD_*", "aud_auftrag", true)]
    [InlineData("AUD_*", "AUDIT", false)]
    [InlineData("*_BAK", "KUNDE_BAK", true)]
    [InlineData("*_BAK", "KUNDE_BAK2", false)]
    [InlineData("A*B*C", "AxxBxxC", true)]
    [InlineData("A*B*C", "AxxCxxB", false)]
    [InlineData("*", "ANY", true)]
    [InlineData("KUNDE", "KUNDEN", false)]
    public void Patterns_match_with_a_star_ignoring_case(string pattern, string table, bool fits) =>
        Assert.Equal(fits, ReportExceptions.Fits(pattern, table));

    [Fact]
    public void A_name_wins_over_a_pattern_and_blank_or_overlong_entries_are_ignored()
    {
        var exceptions = ReportExceptions.From(new Dictionary<string, string?>
        {
            ["IMPORT_*"] = "staging",
            ["IMPORT_KUNDE"] = " read by the nightly job ",
            ["  "] = "blank",
            [new string('X', ReportExceptions.MaxNameLength + 1)] = "too long",
            ["NOTE"] = new string('r', ReportExceptions.MaxReasonLength + 10),
        });

        Assert.Equal(("IMPORT_KUNDE", "read by the nightly job"), (exceptions.Match("IMPORT_KUNDE")!.Table, exceptions.Match("IMPORT_KUNDE")!.Reason));
        Assert.Equal("IMPORT_*", exceptions.Match("IMPORT_ARTIKEL")!.Table);
        Assert.Null(exceptions.Match("KUNDE"));
        Assert.Equal(3, exceptions.Count);
        Assert.Equal(ReportExceptions.MaxReasonLength, exceptions.Match("NOTE")!.Reason.Length);
    }

    [Fact]
    public async Task Exceptions_are_written_one_sorted_line_each_and_unknown_parts_are_kept()
    {
        var path = _folder.Combine("repo", "report-exceptions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{ "version": 1, "tablesWithoutEntity": { "ZZ_OLD": "" }, "columnsWithoutProperty": { "X": "later" } }""", Ct);
        var source = Source(AppSettings.Default with { ReportExceptionsPath = path });

        await source.SetAsync(null, "AUD_*", "Audit tables", Ct);
        await source.SetAsync(null, "zz_old", "renamed", Ct);
        await source.RemoveAsync(null, "nothing", Ct);

        Assert.Equal(
            """
            {
              "version": 1,
              "tablesWithoutEntity": {
                "AUD_*": "Audit tables",
                "zz_old": "renamed"
              },
              "columnsWithoutProperty": {
                "X": "later"
              }
            }
            """.ReplaceLineEndings(),
            (await File.ReadAllTextAsync(path, Ct)).ReplaceLineEndings());
        Assert.Equal(2, source.For(null).Exceptions.Count);
    }

    [Fact]
    public async Task Invalid_json_is_reported_and_never_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        await File.WriteAllTextAsync(DefaultPath, "{ broken", Ct);
        var source = Source(AppSettings.Default);

        var load = source.For(null);
        var refused = await Assert.ThrowsAsync<RefusedException>(() => source.SetAsync(null, "X", null, Ct));

        Assert.True(load.IsDefault);
        Assert.Contains("kein gültiges JSON", load.Error, StringComparison.Ordinal);
        Assert.Contains("kein gültiges JSON", refused.Message, StringComparison.Ordinal);
        Assert.Equal("{ broken", await File.ReadAllTextAsync(DefaultPath, Ct));
    }

    [Fact]
    public async Task A_file_of_a_newer_version_is_read_but_not_changed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        await File.WriteAllTextAsync(DefaultPath, """{ "version": 2, "tablesWithoutEntity": { "TMP_*": "" } }""", Ct);
        var source = Source(AppSettings.Default);

        var load = source.For(null);

        Assert.NotNull(load.ReadOnly);
        Assert.NotNull(load.Exceptions.Match("TMP_A"));
        await Assert.ThrowsAsync<RefusedException>(() => source.SetAsync(null, "X", null, Ct));
    }

    [Fact]
    public async Task A_group_has_its_own_file_and_the_others_take_the_global_one()
    {
        var global = _folder.Combine("global.json");
        var erp = _folder.Combine("erp.json");
        var source = Source(AppSettings.Default with
        {
            ReportExceptionsPath = global,
            ReportExceptionsGroups = SettingsMap.From([new("ERP", erp)]),
        });

        await source.SetAsync(Profile(" erp "), "ERP_TMP", null, Ct);
        await source.SetAsync(Profile("Kasse"), "KASSE_TMP", null, Ct);

        Assert.Equal(("ERP", erp), (source.For(Profile("ERP")).Group, source.For(Profile("ERP")).Path));
        Assert.Equal(global, source.For(Profile(null)).Path);
        Assert.NotNull(source.For(Profile("ERP")).Exceptions.Match("ERP_TMP"));
        Assert.Null(source.For(Profile("ERP")).Exceptions.Match("KASSE_TMP"));
        Assert.NotNull(source.For(Profile("Kasse")).Exceptions.Match("KASSE_TMP"));
    }

    public void Dispose() => _folder.Dispose();
}
