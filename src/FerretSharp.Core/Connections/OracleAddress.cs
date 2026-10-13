using System.Globalization;
using System.Text.RegularExpressions;

namespace FerretSharp.Core.Connections;

/// <summary>Reading an address from a data source text and comparing two addresses – one rule for all (R5b).</summary>
public abstract partial record OracleAddress
{
    /// <summary>
    /// The address in an ODP.NET data source: EZConnect (<c>[//]host[:port][/service][:server]</c>), a connect descriptor
    /// (<c>(DESCRIPTION=…(HOST=…)(PORT=…)…(SERVICE_NAME=…|SID=…))</c>) or a TNS alias (anything else); null for empty text
    /// or a descriptor without host. A JDBC URL has its own syntax (<c>host:port:SID</c>): <c>Transfer.JdbcUrl</c>.
    /// </summary>
    public static OracleAddress? Parse(string? dataSource)
    {
        var text = dataSource?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith('('))
        {
            return Descriptor("HOST", text) is { } host
                ? new HostPortAddress(host, int.TryParse(Descriptor("PORT", text), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : HostPortAddress.DefaultPort,
                    Descriptor("SERVICE_NAME", text), Descriptor("SID", text))
                : null;
        }

        if (EzConnect().Match(text) is { Success: true } ez && (ez.Groups["port"].Success || ez.Groups["service"].Success || text.StartsWith("//", StringComparison.Ordinal)))
        {
            var port = ez.Groups["port"].Success ? int.Parse(ez.Groups["port"].Value, CultureInfo.InvariantCulture) : HostPortAddress.DefaultPort;
            return new HostPortAddress(ez.Groups["host"].Value, port, ez.Groups["service"].Success ? ez.Groups["service"].Value : null, null);
        }

        return new TnsAliasAddress(text, null);
    }

    /// <summary>
    /// The same database (import duplicates, the connection of a traced app): host, service, SID and alias without letter
    /// case (Oracle and DNS ignore it) and surrounding blanks, an empty service or SID as none; the port exactly. Where
    /// <c>tnsnames.ora</c> lies does not count. R5b: before, two rules – the app match ignored the SID beside a service.
    /// </summary>
    public bool SameAs(OracleAddress other) => (this, other) switch
    {
        (HostPortAddress x, HostPortAddress y) =>
            x.Port == y.Port
            && Same(x.Host, y.Host)
            && Same(x.ServiceName, y.ServiceName)
            && Same(x.Sid, y.Sid),
        (TnsAliasAddress x, TnsAliasAddress y) => Same(x.Alias, y.Alias),
        _ => false,
    };

    private static bool Same(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Descriptor(string key, string text) =>
        Regex.Match(text, $@"\(\s*{key}\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^(//)?(?<host>[^:/\s()]+)(:(?<port>\d+))?(/(?<service>[^:/\s()]+))?(:\w+)?$")]
    private static partial Regex EzConnect();
}
