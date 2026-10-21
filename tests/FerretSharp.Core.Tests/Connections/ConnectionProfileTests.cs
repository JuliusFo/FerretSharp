using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

public sealed class ConnectionProfileTests
{
    /// <summary>R5b: the rule that keeps values of Prod out of log, history and error dialogs, in one place.</summary>
    [Theory]
    [InlineData(ConnectionKind.Prod, true)]
    [InlineData(ConnectionKind.Test, false)]
    [InlineData(ConnectionKind.Dev, false)]
    [InlineData(ConnectionKind.Other, false)]
    public void Only_prod_masks_values(ConnectionKind kind, bool masks) =>
        Assert.Equal(masks, (TestProfiles.HostPort() with { Kind = kind }).MasksValues);

    /// <summary>WP-51: the connection's audit template is a path of this computer – an export leaves it out unless asked.</summary>
    [Fact]
    public void The_audit_template_is_a_local_path()
    {
        var profile = TestProfiles.HostPort() with { AuditTemplatePath = @"C:\repo\audit.sql" };

        Assert.Contains(@"C:\repo\audit.sql", profile.LocalPaths());
        Assert.Null(profile.WithoutLocalPaths().AuditTemplatePath);
    }
}
