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
    /// The address as an ODP.NET data source, as <see cref="Parse"/> reads it back: EZConnect with a service, a connect
    /// descriptor with a SID (EZConnect has none), else the TNS alias; null without service and SID. The parts go in as
    /// they are – whoever connects checks them first (<see cref="IsPlainHost"/>, review 3.40.0, A7). R6b (B11): was
    /// written in the connection string and the link.
    /// </summary>
    public string? ToDataSource() => this switch
    {
        HostPortAddress { ServiceName: { } service } hp when !string.IsNullOrWhiteSpace(service) =>
            $"{hp.Host.Trim()}:{hp.Port.ToString(CultureInfo.InvariantCulture)}/{service.Trim()}",
        HostPortAddress { Sid: { } sid } hp when !string.IsNullOrWhiteSpace(sid) =>
            $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={hp.Host.Trim()})(PORT={hp.Port.ToString(CultureInfo.InvariantCulture)}))(CONNECT_DATA=(SID={sid.Trim()})))",
        TnsAliasAddress tns => tns.Alias.Trim(),
        _ => null,
    };

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

    /// <summary>
    /// A host FerretSharp puts into a connect descriptor or EZConnect text (review 3.40.0, A7): a name or IPv4 address, or an
    /// IPv6 address in brackets. A value from an imported file such as <c>db01)(PORT=1))(ADDRESS=(PROTOCOL=TCP)(HOST=evil</c>
    /// would otherwise add an address of its own – Oracle fails over to it, and the saved password with it.
    /// </summary>
    public static bool IsPlainHost(string host) => PlainHost().IsMatch(host.Trim());

    /// <summary>A service name or SID: letters, digits and <c>. _ $ # -</c> (see <see cref="IsPlainHost"/>).</summary>
    public static bool IsPlainName(string name) => PlainName().IsMatch(name.Trim());

    /// <summary>A TNS alias: no blanks and none of <c>( ) =</c> – those would make it a descriptor of its own.</summary>
    public static bool IsPlainAlias(string alias) => alias.Trim() is { Length: > 0 } text && !text.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '=');

    [GeneratedRegex(@"^(\[[0-9A-Fa-f:.%]+\]|[\p{L}\p{N}._-]+)$")]
    private static partial Regex PlainHost();

    [GeneratedRegex(@"^[\p{L}\p{N}._$#-]+$")]
    private static partial Regex PlainName();

    private static bool Same(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Descriptor(string key, string text) =>
        Regex.Match(text, $@"\(\s*{key}\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^(//)?(?<host>[^:/\s()]+)(:(?<port>\d+))?(/(?<service>[^:/\s()]+))?(:\w+)?$")]
    private static partial Regex EzConnect();
}
