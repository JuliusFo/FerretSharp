using System.Text.RegularExpressions;
using FerretSharp.Core.Connections;

namespace FerretSharp.Core.AppTrace;

/// <summary>How a traced app was matched to a connection.</summary>
public enum MatchKind
{
    /// <summary>No connection fits; the user picks one.</summary>
    None,

    /// <summary>The app's environment name is the connection's <see cref="ConnectionProfile.AppEnvironment"/>.</summary>
    Environment,

    /// <summary>The app's data source names the connection's address.</summary>
    DataSource,

    /// <summary>Chosen by the user.</summary>
    Chosen,
}

/// <summary>The connection of a traced app; <paramref name="Candidates"/> are the ones that fit equally well when no single one does.</summary>
public sealed record AppConnectionMatch(ConnectionProfile? Profile, MatchKind Kind, IReadOnlyList<ConnectionProfile> Candidates)
{
    public static readonly AppConnectionMatch NoMatch = new(null, MatchKind.None, []);

    /// <summary>
    /// The connection of an app (WP-33): by its environment name first (<see cref="ConnectionProfile.AppEnvironment"/>,
    /// any letter case), else by the data source of its commands (<c>host:port/service</c>, a TNS alias or a connect
    /// descriptor) – only if exactly one connection fits; several that fit become the candidates. The user name is not
    /// compared: the connection string is never read (<see cref="EfEvents"/>).
    /// </summary>
    public static AppConnectionMatch Find(IReadOnlyList<ConnectionProfile> profiles, string? environment, string? dataSource)
    {
        if (environment is { Length: > 0 })
        {
            var byEnvironment = profiles.Where(p => string.Equals(p.AppEnvironment?.Trim(), environment.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (byEnvironment.Count > 0)
            {
                return Of(byEnvironment, MatchKind.Environment);
            }
        }

        if (DataSourceAddress.Parse(dataSource) is { } address)
        {
            var bySource = profiles.Where(p => address.Matches(p.Address)).ToList();
            if (bySource.Count > 0)
            {
                return Of(bySource, MatchKind.DataSource);
            }
        }

        return NoMatch;
    }

    private static AppConnectionMatch Of(List<ConnectionProfile> fitting, MatchKind kind) =>
        fitting.Count == 1 ? new(fitting[0], kind, []) : new(null, MatchKind.None, fitting);
}

/// <summary>
/// The address in an ODP.NET data source: EZConnect (<c>[//]host[:port][/service]</c>), a connect descriptor
/// (<c>(DESCRIPTION=…(HOST=…)(PORT=…)…(SERVICE_NAME=…|SID=…))</c>) or a TNS alias (anything else).
/// </summary>
public sealed partial record DataSourceAddress(string? Host, int Port, string? Service, string? Sid, string? Alias)
{
    public static DataSourceAddress? Parse(string? dataSource)
    {
        var text = dataSource?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith('('))
        {
            var host = Descriptor("HOST", text);
            return host is null ? null : new(host, int.TryParse(Descriptor("PORT", text), out var p) ? p : HostPortAddress.DefaultPort, Descriptor("SERVICE_NAME", text), Descriptor("SID", text), null);
        }

        if (EzConnect().Match(text) is { Success: true } ez && (ez.Groups["port"].Success || ez.Groups["service"].Success || text.StartsWith("//", StringComparison.Ordinal)))
        {
            var port = ez.Groups["port"].Success ? int.Parse(ez.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture) : HostPortAddress.DefaultPort;
            return new(ez.Groups["host"].Value, port, ez.Groups["service"].Success ? ez.Groups["service"].Value : null, null, null);
        }

        return new(null, 0, null, null, text);
    }

    public bool Matches(OracleAddress address) => address switch
    {
        TnsAliasAddress tns => Alias is not null && string.Equals(tns.Alias.Trim(), Alias, StringComparison.OrdinalIgnoreCase),
        HostPortAddress hp => Host is not null
            && string.Equals(hp.Host.Trim(), Host, StringComparison.OrdinalIgnoreCase)
            && hp.Port == Port
            && (hp.ServiceName is { Length: > 0 } service
                ? string.Equals(service.Trim(), Service, StringComparison.OrdinalIgnoreCase)
                : string.Equals(hp.Sid?.Trim(), Sid, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static string? Descriptor(string key, string text) =>
        Regex.Match(text, $@"\(\s*{key}\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^(//)?(?<host>[^:/\s()]+)(:(?<port>\d+))?(/(?<service>[^:/\s()]+))?(:\w+)?$")]
    private static partial Regex EzConnect();
}
