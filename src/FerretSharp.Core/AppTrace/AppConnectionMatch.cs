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
    /// descriptor, <see cref="OracleAddress.SameAs"/>) – only if exactly one connection fits; several that fit become the candidates. The user name is not
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

        if (OracleAddress.Parse(dataSource) is { } address)
        {
            var bySource = profiles.Where(p => p.Address.SameAs(address)).ToList();
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
