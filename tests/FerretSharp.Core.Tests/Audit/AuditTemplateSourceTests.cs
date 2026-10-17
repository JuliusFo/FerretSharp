using FerretSharp.Core.Audit;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.Audit;

/// <summary>WP-26, WP-51: the template of a connection – its own, its group's, the global one or the built-in one – or why the file cannot be used.</summary>
public sealed class AuditTemplateSourceTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly FakeTimeProvider _clock = new();

    private AuditTemplateSource Source(string? path, SettingsMap? groups = null) =>
        new(new AppSettingsService(new SettingsStore(_folder.Combine("settings.json")),
            AppSettings.Default with { AuditTemplatePath = path, AuditTemplateGroups = groups ?? SettingsMap.Empty }), _clock);

    private static ConnectionProfile Profile(string? group = null, string? template = null) =>
        new(Guid.NewGuid(), "DEV", ConnectionKind.Dev, new HostPortAddress("db", 1521, "ORCL", null), "APP", null, false, group, AuditTemplatePath: template);

    /// <summary>A valid template file whose audit tables are called <paramref name="prefix"/>_&lt;table&gt;.</summary>
    private string TemplateFile(string name, string prefix)
    {
        var path = _folder.Combine(name);
        File.WriteAllText(path, AuditTemplateTests.CompanyTemplate.Replace("AUDIT_{table}", prefix + "_{table}", StringComparison.Ordinal));
        return path;
    }

    private static string AuditTableOf(AuditTemplateLoad load) => load.Template!.NamesFor(new("APP", "KUNDE")).AuditTable.Name;

    [Fact]
    public void Without_a_path_the_built_in_template_is_used()
    {
        var load = Source("  ").For(Profile("ERP"));

        Assert.True(load.IsDefault);
        Assert.Equal(AuditTemplateOrigin.BuiltIn, load.Origin);
        Assert.Same(AuditTemplates.Default, load.Template);
        Assert.Empty(load.Errors);
    }

    [Fact]
    public void A_file_is_read_and_read_again_when_it_changed()
    {
        var path = _folder.Combine("audit.sql");
        File.WriteAllText(path, AuditTemplateTests.CompanyTemplate);
        var source = Source(path);

        var first = source.Load(path);
        Assert.Empty(first.Errors);
        Assert.Equal("AUDIT_KUNDE", AuditTableOf(first));
        Assert.Same(first, source.Load(path)); // fresh: the file is not looked at
        _clock.Advance(AuditTemplateSource.Fresh);
        Assert.Same(first, source.Load(path)); // unchanged on disk: not parsed again
        Assert.Same(first.Template, source.For(null).Template);

        File.WriteAllText(path, AuditTemplateTests.CompanyTemplate.Replace("AUDIT_{table}", "{table}_HIST", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("AUDIT_KUNDE", AuditTableOf(source.For(null))); // not looked at again yet
        _clock.Advance(AuditTemplateSource.Fresh);
        Assert.Equal("KUNDE_HIST", AuditTableOf(source.For(null)));
    }

    /// <summary>WP-51: each group of systems has its own template; the connection's own goes before it, the global one after.</summary>
    [Fact]
    public void The_connection_goes_before_its_group_and_the_group_before_the_global_template()
    {
        var source = Source(TemplateFile("global.sql", "GLOBAL"), SettingsMap.From([new("ERP", TemplateFile("erp.sql", "ERP"))]));

        var own = source.For(Profile("ERP", TemplateFile("own.sql", "OWN")));
        var group = source.For(Profile(" erp "));
        var other = source.For(Profile("Kasse"));
        var none = source.For(Profile());

        Assert.Equal(("OWN_KUNDE", AuditTemplateOrigin.Connection), (AuditTableOf(own), own.Origin));
        Assert.Equal(("ERP_KUNDE", AuditTemplateOrigin.Group, "ERP"), (AuditTableOf(group), group.Origin, group.Group)); // trimmed, case ignored
        Assert.Equal(("GLOBAL_KUNDE", AuditTemplateOrigin.Global), (AuditTableOf(other), other.Origin));
        Assert.Equal(("GLOBAL_KUNDE", AuditTemplateOrigin.Global), (AuditTableOf(none), none.Origin));
    }

    [Fact]
    public void A_group_without_a_global_template_falls_back_to_the_built_in_one()
    {
        var source = Source(null, SettingsMap.From([new("ERP", TemplateFile("erp.sql", "ERP")), new("Kasse", " ")]));

        Assert.Equal(AuditTemplateOrigin.Group, source.For(Profile("ERP")).Origin);
        Assert.Equal(AuditTemplateOrigin.BuiltIn, source.For(Profile("Kasse")).Origin); // an entry without a path is none
        Assert.Same(AuditTemplates.Default, source.For(Profile("Lager")).Template);
    }

    /// <summary>WP-51: a broken template of the connection or group is reported – the next one down may be another system's.</summary>
    [Fact]
    public void A_broken_template_is_reported_not_passed_over()
    {
        var source = Source(TemplateFile("global.sql", "GLOBAL"), SettingsMap.From([new("ERP", _folder.Combine("fehlt.sql"))]));

        var group = source.For(Profile("ERP"));
        var own = source.For(Profile("Kasse", _folder.Combine("auch-fehlt.sql")));

        Assert.Null(group.Template);
        Assert.Equal(AuditTemplateOrigin.Group, group.Origin);
        Assert.Contains("gibt es nicht", Assert.Single(group.Errors));
        Assert.Null(own.Template);
        Assert.Equal(AuditTemplateOrigin.Connection, own.Origin);
    }

    [Fact]
    public void A_missing_or_broken_file_says_why()
    {
        var broken = _folder.Combine("broken.sql");
        File.WriteAllText(broken, "-- @audit-table AUDIT_{table}\n-- @trigger {table}_AUDIT\n");

        var missing = Source(_folder.Combine("fehlt.sql")).For(null);
        var errors = Source(broken).For(null);

        Assert.Null(missing.Template);
        Assert.Contains("gibt es nicht", Assert.Single(missing.Errors));
        Assert.Null(errors.Template);
        Assert.NotEmpty(errors.Errors);
    }

    /// <summary>
    /// R5b (review 3.31.0, B7): a file locked for a moment (an editor saving it) said "cannot be read" until the file
    /// changed on disk; now the next look reads it.
    /// </summary>
    [Fact]
    public void A_file_locked_for_a_moment_is_read_at_the_next_look()
    {
        var path = _folder.Combine("audit.sql");
        File.WriteAllText(path, AuditTemplateTests.CompanyTemplate);
        var source = Source(path);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(source.For(null).Template);
        }

        _clock.Advance(AuditTemplateSource.Fresh);
        Assert.Empty(source.For(null).Errors);
    }

    public void Dispose() => _folder.Dispose();
}
