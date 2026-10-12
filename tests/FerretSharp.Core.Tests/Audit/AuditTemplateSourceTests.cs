using FerretSharp.Core.Audit;
using FerretSharp.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.Audit;

/// <summary>WP-26: the template of the settings – built in, a file, or why the file cannot be used.</summary>
public sealed class AuditTemplateSourceTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly FakeTimeProvider _clock = new();

    private AuditTemplateSource Source(string? path) =>
        new(new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")), AppSettings.Default with { AuditTemplatePath = path }), _clock);

    [Fact]
    public void Without_a_path_the_built_in_template_is_used()
    {
        var load = Source("  ").Current();

        Assert.True(load.IsDefault);
        Assert.Same(AuditTemplates.Default, load.Template);
        Assert.Empty(load.Errors);
    }

    [Fact]
    public void A_file_is_read_and_read_again_when_it_changed()
    {
        var path = _folder.Combine("audit.sql");
        File.WriteAllText(path, AuditTemplateTests.CompanyTemplate);
        var source = Source(path);

        var first = source.Current();
        Assert.Empty(first.Errors);
        Assert.Equal("AUDIT_KUNDE", first.Template!.NamesFor(new("APP", "KUNDE")).AuditTable.Name);
        Assert.Same(first, source.Current()); // fresh: the file is not looked at
        _clock.Advance(AuditTemplateSource.Fresh);
        Assert.Same(first, source.Current()); // unchanged on disk: not parsed again

        File.WriteAllText(path, AuditTemplateTests.CompanyTemplate.Replace("AUDIT_{table}", "{table}_HIST", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("AUDIT_KUNDE", source.Current().Template!.NamesFor(new("APP", "KUNDE")).AuditTable.Name); // not looked at again yet
        _clock.Advance(AuditTemplateSource.Fresh);
        Assert.Equal("KUNDE_HIST", source.Current().Template!.NamesFor(new("APP", "KUNDE")).AuditTable.Name);
    }

    [Fact]
    public void A_missing_or_broken_file_says_why()
    {
        var broken = _folder.Combine("broken.sql");
        File.WriteAllText(broken, "-- @audit-table AUDIT_{table}\n-- @trigger {table}_AUDIT\n");

        var missing = Source(_folder.Combine("fehlt.sql")).Current();
        var errors = Source(broken).Current();

        Assert.Null(missing.Template);
        Assert.Contains("gibt es nicht", Assert.Single(missing.Errors));
        Assert.Null(errors.Template);
        Assert.NotEmpty(errors.Errors);
    }

    public void Dispose() => _folder.Dispose();
}
