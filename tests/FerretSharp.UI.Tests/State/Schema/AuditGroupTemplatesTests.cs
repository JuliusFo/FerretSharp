using FerretSharp.Core.Audit;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-51: the audit templates of connection groups in the settings, which template applies, and the connection's own in the dialog.</summary>
public sealed class AuditGroupTemplatesTests
{
    private static ConnectionProfile Profile(string name, string? group, string? template = null) =>
        new(Guid.NewGuid(), name, ConnectionKind.Dev, new HostPortAddress("db01", 1521, "ERP", null), "erp", null, false, group, AuditTemplatePath: template);

    [Fact]
    public void Every_group_of_the_connections_is_listed_with_its_template_and_orphans_stay()
    {
        var profiles = new[] { Profile("DEV", "ERP"), Profile("TST", " erp "), Profile("DEV", "Kasse"), Profile("Lokal", null) };
        var entries = SettingsMap.From([new("erp", @"C:\erp.sql"), new("Lager", @"C:\lager.sql"), new("Leer", " ")]);

        var rows = AuditGroupTemplates.Of(profiles, entries);

        Assert.Equal(
            [new AuditGroupTemplate("ERP", @"C:\erp.sql", false), new AuditGroupTemplate("Kasse", null, false), new AuditGroupTemplate("Lager", @"C:\lager.sql", true)],
            rows);
    }

    /// <summary>Review of WP-66: the groups follow the user's order like the connection list; entries without a group after them.</summary>
    [Fact]
    public void Groups_follow_the_users_order_and_orphans_come_last()
    {
        var profiles = new[] { Profile("DEV", "ERP"), Profile("DEV", "Kasse"), Profile("DEV", "Zoll") };
        var entries = SettingsMap.From([new("Alt", @"C:\alt.sql")]);

        var rows = AuditGroupTemplates.Of(profiles, entries, ["Zoll"]);

        Assert.Equal(["Zoll", "ERP", "Kasse", "Alt"], rows.Select(r => r.Group));
    }

    [Fact]
    public void Setting_a_template_keeps_one_entry_per_group_and_empty_removes_it()
    {
        var entries = SettingsMap.From([new("erp", @"C:\alt.sql"), new("Kasse", @"C:\kasse.sql")]);

        var set = AuditGroupTemplates.With(entries, " ERP ", "\"C:\\neu.sql\"");
        var removed = AuditGroupTemplates.With(set, "Erp", "  ");

        Assert.Equal(SettingsMap.From([new("ERP", @"C:\neu.sql"), new("Kasse", @"C:\kasse.sql")]), set);
        Assert.Equal(SettingsMap.From([new("Kasse", @"C:\kasse.sql")]), removed);
    }

    [Fact]
    public void The_label_says_where_the_template_is_set()
    {
        using (UiCulture.Use("en"))
        {
            Assert.Equal(@"Template of the group ERP: C:\erp.sql",
                AuditTemplateLabel.Of(new AuditTemplateLoad(null, @"C:\erp.sql", []) { Origin = AuditTemplateOrigin.Group, Group = "ERP" }));
            Assert.Equal(@"Template of this connection: C:\own.sql",
                AuditTemplateLabel.Of(new AuditTemplateLoad(null, @"C:\own.sql", []) { Origin = AuditTemplateOrigin.Connection }));
            Assert.Equal(@"Template of the settings: C:\all.sql",
                AuditTemplateLabel.Of(new AuditTemplateLoad(null, @"C:\all.sql", []) { Origin = AuditTemplateOrigin.Global }));
            Assert.Equal("Built-in template", AuditTemplateLabel.Of(new AuditTemplateLoad(AuditTemplates.Default, null, [])));
        }
    }

    [Fact]
    public void The_connection_dialog_keeps_the_own_template()
    {
        var form = ConnectionForm.FromProfile(Profile("DEV", "ERP", @"C:\own.sql"));
        Assert.Equal(@"C:\own.sql", form.AuditTemplatePath);

        form.AuditTemplatePath = "  \"C:\\other.sql\" ";
        Assert.Equal(@"C:\other.sql", form.ToProfile().Profile.AuditTemplatePath);

        form.AuditTemplatePath = " ";
        Assert.Null(form.ToProfile().Profile.AuditTemplatePath);
    }
}
