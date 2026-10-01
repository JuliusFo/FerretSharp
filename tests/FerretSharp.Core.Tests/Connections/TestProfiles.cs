using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

internal static class TestProfiles
{
    public static ConnectionProfile HostPort(string name = "Test DB", string? service = "ORCLPDB", string? sid = null) => new(
        Guid.NewGuid(), name, ConnectionKind.Test, new HostPortAddress("db01.example.com", 1521, service, sid), "app_user", null, false);

    public static ConnectionProfile Tns(string alias = "ERP_PROD", string? tnsAdmin = @"C:\oracle\network\admin") => new(
        Guid.NewGuid(), "Prod ERP", ConnectionKind.Prod, new TnsAliasAddress(alias, tnsAdmin), "reader", "ERP", true, Group: "ERP");
}
