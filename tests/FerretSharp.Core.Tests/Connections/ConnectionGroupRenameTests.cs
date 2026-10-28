using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Connections;

/// <summary>WP-66: renaming a group changes every connection of it and carries the audit template along.</summary>
public sealed class ConnectionGroupRenameTests
{
    private static ConnectionProfile Make(string name, string? group) => TestProfiles.HostPort(name) with { Group = group };

    private static readonly ConnectionProfile ErpDev = Make("ERP Dev", "ERP");
    private static readonly ConnectionProfile ErpTest = Make("ERP Test", " erp ");
    private static readonly ConnectionProfile KasseTest = Make("Kasse Test", "Kasse");
    private static readonly ConnectionProfile Loose = Make("Spielwiese", null);
    private static readonly ConnectionProfile[] All = [ErpDev, ErpTest, KasseTest, Loose];

    [Fact]
    public void Every_connection_of_the_group_gets_the_new_name()
    {
        var rename = ConnectionGroupRename.Plan(All, "ERP", "  Warenwirtschaft ", SettingsMap.Empty);

        Assert.Equal("ERP", rename.From);
        Assert.Equal("Warenwirtschaft", rename.To);
        Assert.False(rename.Merges);
        Assert.Equal([ErpDev.Id, ErpTest.Id], rename.Profiles.Select(p => p.Id));
        Assert.All(rename.Profiles, p => Assert.Equal("Warenwirtschaft", p.Group));
    }

    [Fact]
    public void Only_a_new_spelling_is_a_rename_not_a_merge()
    {
        var rename = ConnectionGroupRename.Plan(All, "ERP", "Erp", SettingsMap.From([new("ERP", @"C:\erp.sql")]));

        Assert.False(rename.Merges);
        Assert.False(rename.IsUnchanged);
        Assert.Equal(@"C:\erp.sql", rename.AuditTemplateGroups["Erp"]);
        Assert.Single(rename.AuditTemplateGroups);
        Assert.True(ConnectionGroupRename.Plan(All, "ERP", " ERP", SettingsMap.Empty).IsUnchanged);
    }

    [Fact]
    public void The_audit_template_of_the_group_goes_with_it()
    {
        var templates = SettingsMap.From([new("erp ", @"C:\erp.sql"), new("Kasse", @"C:\kasse.sql")]);

        var rename = ConnectionGroupRename.Plan(All, "ERP", "Warenwirtschaft", templates);

        Assert.Equal(@"C:\erp.sql", rename.AuditTemplateGroups["Warenwirtschaft"]);
        Assert.Equal(@"C:\kasse.sql", rename.AuditTemplateGroups["Kasse"]);
        Assert.Equal(2, rename.AuditTemplateGroups.Count);
        Assert.Null(rename.DroppedAuditTemplate);
    }

    [Fact]
    public void A_name_another_group_has_merges_into_its_spelling_and_template()
    {
        var templates = SettingsMap.From([new("ERP", @"C:\erp.sql"), new("Kasse", @"C:\kasse.sql")]);

        var rename = ConnectionGroupRename.Plan(All, "ERP", "KASSE", templates);

        Assert.True(rename.Merges);
        Assert.Equal("Kasse", rename.To);
        Assert.All(rename.Profiles, p => Assert.Equal("Kasse", p.Group));
        Assert.Equal(@"C:\erp.sql", rename.DroppedAuditTemplate);
        Assert.Equal([new("Kasse", @"C:\kasse.sql")], rename.AuditTemplateGroups);
    }

    [Fact]
    public void Merged_into_a_group_without_template_the_template_moves()
    {
        var rename = ConnectionGroupRename.Plan(All, "ERP", "Kasse", SettingsMap.From([new("ERP", @"C:\erp.sql")]));

        Assert.Null(rename.DroppedAuditTemplate);
        Assert.Equal([new("Kasse", @"C:\erp.sql")], rename.AuditTemplateGroups);
    }

    [Fact]
    public void A_template_left_for_the_new_name_after_an_earlier_rename_is_kept()
    {
        var templates = SettingsMap.From([new("ERP", @"C:\erp.sql"), new("Warenwirtschaft", @"C:\ww.sql")]);

        var rename = ConnectionGroupRename.Plan(All, "ERP", "Warenwirtschaft", templates);

        Assert.False(rename.Merges); // no connection has the name
        Assert.Equal(@"C:\erp.sql", rename.DroppedAuditTemplate);
        Assert.Equal([new("Warenwirtschaft", @"C:\ww.sql")], rename.AuditTemplateGroups);
    }

    [Theory]
    [InlineData("ERP", " ")]
    [InlineData("", "Kasse")]
    public void A_group_needs_a_name(string from, string to) =>
        Assert.Throws<ArgumentException>(() => ConnectionGroupRename.Plan(All, from, to, SettingsMap.Empty));
}
