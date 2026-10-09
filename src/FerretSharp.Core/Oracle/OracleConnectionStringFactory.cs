using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

public sealed class ConnectionConfigurationException(string message) : Exception(message);

public static class OracleConnectionStringFactory
{
    public const int DefaultConnectTimeoutSeconds = 15;
    private const string TnsAdminVariable = "TNS_ADMIN";

    /// <summary>
    /// Builds an ODP.NET connection string. Pooling is always off: sessions are long-lived and must never
    /// return a connection with an open transaction to a pool.
    /// </summary>
    /// <param name="getEnvironmentVariable">Injectable for tests; defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</param>
    public static string Create(
        ConnectionProfile profile,
        string password,
        int connectTimeoutSeconds = DefaultConnectTimeoutSeconds,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var builder = new OracleConnectionStringBuilder
        {
            UserID = profile.User,
            Password = password,
            DataSource = DataSourceFor(profile.Address),
            Pooling = false,
            ConnectionTimeout = connectTimeoutSeconds,
        };

        if (profile.Address is TnsAliasAddress tns)
        {
            var tnsAdmin = string.IsNullOrWhiteSpace(tns.TnsAdminPath)
                ? (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(TnsAdminVariable)
                : tns.TnsAdminPath;

            if (string.IsNullOrWhiteSpace(tnsAdmin))
            {
                throw new ConnectionConfigurationException(OracleText.TnsAdminMissing);
            }

            builder["Tns_Admin"] = tnsAdmin;
        }

        return builder.ConnectionString;
    }

    internal static string DataSourceFor(OracleAddress address) => address switch
    {
        HostPortAddress { ServiceName: { Length: > 0 } service } hp => $"{hp.Host}:{hp.Port}/{service}",
        HostPortAddress { Sid: { Length: > 0 } sid } hp =>
            $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={hp.Host})(PORT={hp.Port}))(CONNECT_DATA=(SID={sid})))",
        TnsAliasAddress tns => tns.Alias,
        _ => throw new ConnectionConfigurationException(OracleText.AddressIncomplete),
    };
}
