using System.Globalization;
using System.Text.RegularExpressions;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>
/// The address in an Oracle thin JDBC URL, for imported connections that only have a URL (WP-35):
/// <c>jdbc:oracle:thin:@//host:port/service</c>, <c>jdbc:oracle:thin:@host:port/service</c> and
/// <c>jdbc:oracle:thin:@host:port:SID</c>. Anything else (a <c>(DESCRIPTION=…)</c>, a TNS alias, LDAP) is not read.
/// Credentials in the URL (<c>user/password@</c>) are skipped, never kept.
/// </summary>
public static partial class JdbcUrl
{
    public static HostPortAddress? TryParse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = ThinUrl().Match(url.Trim());
        if (!match.Success)
        {
            return null;
        }

        var port = HostPortAddress.DefaultPort;
        if (match.Groups["port"].Success
            && !int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            return null;
        }

        var host = match.Groups["host"].Value;
        return match.Groups["sep"].Value == ":"
            ? new HostPortAddress(host, port, null, match.Groups["name"].Value)
            : new HostPortAddress(host, port, match.Groups["name"].Value, null);
    }

    /// <summary>
    /// A URL as the preview may show it: anything between <c>jdbc:oracle:thin:</c> and <c>@</c> (<c>user/password</c>) is
    /// left out – also for URLs FerretSharp cannot read.
    /// </summary>
    public static string? ForDisplay(string? url)
    {
        if (url is null)
        {
            return null;
        }

        const string Thin = "jdbc:oracle:thin:";
        var at = url.IndexOf('@', StringComparison.Ordinal);
        return url.StartsWith(Thin, StringComparison.OrdinalIgnoreCase) && at > Thin.Length ? Thin + url[at..] : url;
    }

    // Port is optional only in the service form (a SID needs host:port:SID).
    [GeneratedRegex(@"^jdbc:oracle:thin:(?:[^@]*)@(?://)?(?<host>[A-Za-z0-9._-]+)(?::(?<port>\d{1,5}))?(?<sep>[/:])(?<name>[A-Za-z0-9._$#-]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThinUrl();
}
