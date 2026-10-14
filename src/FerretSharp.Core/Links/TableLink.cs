using System.Globalization;
using System.Text;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Links;

/// <summary>
/// A link to a table view (WP-37, ADR 0026): connection, table, filters, sort order and optionally the view of the tab –
/// never SQL, only the filter model, which the receiver checks against its schema like any filter. It opens one tab
/// for reading, after a confirmation. Format (each part percent-encoded on its own, so commas inside values survive):
/// <c>ferretsharp://open?v=1&amp;c=ERP%20Test&amp;a=db01%3A1521%2FERP&amp;s=ERP&amp;t=AUFTRAG&amp;f=STATUS,in,OFFEN,NEU&amp;o=ERSTELLT,desc&amp;m=data</c>.
/// </summary>
/// <param name="Connection">The sender's name of the connection; the receiver looks it up (<see cref="LinkConnectionMatch"/>).</param>
/// <param name="Address">The sender's address of the connection; null if the link has none.</param>
/// <param name="Mode">The view of the tab; null for the data view.</param>
public sealed record TableLink(
    string? Connection, OracleAddress? Address, string Owner, string Table,
    IReadOnlyList<FilterCondition> Filters, IReadOnlyList<SortSpec> Sorts, TabMode? Mode = null)
{
    public const string Scheme = "ferretsharp";

    /// <summary>The format this FerretSharp writes and reads; a link of a higher version comes from a newer one.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Limits for a link from outside: longer than a browser or mail client keeps reliably anyway, and the IN list of a
    /// link to selected rows (<see cref="FkNavigation.MaxValues"/>) has room.
    /// </summary>
    public const int MaxLength = 32 * 1024;

    public const int MaxFilters = 50;

    public const int MaxValues = FkNavigation.MaxValues;

    private const int MaxConnectionName = 200;

    /// <summary>
    /// The link to a view of <paramref name="table"/> on <paramref name="profile"/>. Only enabled filter rows go in: a
    /// disabled row filters nothing.
    /// </summary>
    public static TableLink For(ConnectionProfile profile, TableRef table, IEnumerable<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, TabMode mode) =>
        new(profile.Name, profile.Address, table.Owner, table.Name, filters.Where(f => f.Enabled).ToList(), sorts, mode == TabMode.Data ? null : mode);

    /// <summary>
    /// The link to <paramref name="rows"/> of <paramref name="table"/>: a filter on the primary key – equality for one row,
    /// an IN list for several (single-column keys), with the rules of the FK jump of several rows. ROWIDs are no key for a
    /// link: they change with a move or an export/import, and another database has others.
    /// </summary>
    public static (TableLink? Link, string? Unavailable) ForRows(ConnectionProfile profile, TableDetails table, IReadOnlyList<RowData> rows)
    {
        if (!table.PrimaryKeyEnforced || table.PrimaryKey.Count == 0)
        {
            return (null, QueryText.LinkRowsNeedPrimaryKey);
        }

        var key = FkNavigation.KeyFilters(table, rows, table.PrimaryKey, table.PrimaryKey);
        return key.Unavailable is { } reason
            ? (null, reason)
            : (new TableLink(profile.Name, profile.Address, table.Table.Owner, table.Table.Name, key.Filters, []), null);
    }

    /// <summary>Whether the text is meant to be a link (a start argument, a pipe message) – not whether it is valid.</summary>
    public static bool IsLink(string? text) => text is not null && text.TrimStart().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public string ToUri()
    {
        var uri = new StringBuilder(Scheme).Append("://open?v=").Append(CurrentVersion);
        if (Connection is { Length: > 0 } connection)
        {
            Add(uri, "c", connection);
        }

        if (Address is { } address)
        {
            Add(uri, "a", AddressText(address));
        }

        Add(uri, "s", Owner);
        Add(uri, "t", Table);
        foreach (var filter in Filters)
        {
            Add(uri, "f", [filter.Column, Name(filter.Op), .. filter.Values]);
        }

        foreach (var sort in Sorts)
        {
            Add(uri, "o", [sort.Column, sort.Descending ? "desc" : "asc"]);
        }

        if (Mode is { } mode)
        {
            Add(uri, "m", Name(mode));
        }

        return uri.ToString();
    }

    /// <summary>
    /// Reads a link from outside – a start argument, the pipe of a running instance –, checking everything that can be
    /// checked without a schema. Never throws.
    /// </summary>
    public static TableLinkRead Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length > MaxLength)
        {
            return Failed(TextFormat.Format(QueryText.LinkTooLong, MaxLength));
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "open", StringComparison.OrdinalIgnoreCase))
        {
            return Failed(QueryText.LinkNotFerretSharp);
        }

        // The query as written, not Uri.Query: each part is unescaped once, after splitting at the separators.
        var query = trimmed.IndexOf('?', StringComparison.Ordinal) is var at and >= 0 ? trimmed[(at + 1)..] : "";
        query = query.IndexOf('#', StringComparison.Ordinal) is var hash and >= 0 ? query[..hash] : query;
        var parameters = new List<(string Key, string Raw)>();
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            parameters.Add(equals < 0 ? (part, "") : (part[..equals], part[(equals + 1)..]));
        }

        string? Single(string key) => parameters.LastOrDefault(p => p.Key == key) is { Key: not null } found ? Unescape(found.Raw) : null;

        if (!int.TryParse(Single("v"), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            return Failed(QueryText.LinkVersionMissing);
        }

        if (version > CurrentVersion)
        {
            return Failed(TextFormat.Format(QueryText.LinkFromNewerVersion, version));
        }

        var owner = Single("s");
        var table = Single("t");
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(table))
        {
            return Failed(QueryText.LinkTableMissing);
        }

        if ((OracleIdentifier.Problem(owner) ?? OracleIdentifier.Problem(table)) is { } nameProblem)
        {
            return Failed(nameProblem);
        }

        var connection = Single("c")?.Trim();
        if (connection?.Length > MaxConnectionName)
        {
            return Failed(QueryText.LinkConnectionNameTooLong);
        }

        var filters = new List<FilterCondition>();
        var values = 0;
        foreach (var (_, raw) in parameters.Where(p => p.Key == "f"))
        {
            if (filters.Count == MaxFilters)
            {
                return Failed(TextFormat.Format(QueryText.LinkTooManyFilters, MaxFilters));
            }

            var parts = raw.Split(',').Select(Unescape).ToArray();
            if (parts.Length < 2 || OracleIdentifier.Problem(parts[0]) is not null || parts[0].Length == 0
                || !TryEnum<FilterOperator>(parts[1], out var op))
            {
                return Failed(TextFormat.Format(QueryText.LinkFilterInvalid, parts[0]));
            }

            var filterValues = parts[2..];
            var expected = FilterRules.ValueCount(op);
            if (expected >= 0 && filterValues.Length != expected || expected < 0 && filterValues.Length == 0
                || op == FilterOperator.In && filterValues.Any(v => v.Contains(FilterCondition.ListSeparator, StringComparison.Ordinal)))
            {
                return Failed(TextFormat.Format(QueryText.LinkFilterInvalid, parts[0]));
            }

            values += filterValues.Length;
            if (values > MaxValues)
            {
                return Failed(TextFormat.Format(QueryText.LinkTooManyValues, MaxValues));
            }

            filters.Add(new FilterCondition(parts[0], op, filterValues));
        }

        var sorts = new List<SortSpec>();
        foreach (var (_, raw) in parameters.Where(p => p.Key == "o"))
        {
            var parts = raw.Split(',').Select(Unescape).ToArray();
            if (parts.Length != 2 || parts[0].Length == 0 || OracleIdentifier.Problem(parts[0]) is not null || parts[1] is not ("asc" or "desc")
                || sorts.Count == MaxFilters)
            {
                return Failed(TextFormat.Format(QueryText.LinkSortInvalid, parts[0]));
            }

            if (sorts.All(s => s.Column != parts[0]))
            {
                sorts.Add(new SortSpec(parts[0], parts[1] == "desc"));
            }
        }

        TabMode? mode = null;
        if (Single("m") is { } modeText)
        {
            if (!TryEnum<TabMode>(modeText, out var parsed))
            {
                return Failed(QueryText.LinkViewInvalid);
            }

            mode = parsed == TabMode.Data ? null : parsed;
        }

        return new(new TableLink(string.IsNullOrEmpty(connection) ? null : connection, OracleAddress.Parse(Single("a")), owner, table, filters, sorts, mode), null);
    }

    /// <summary>
    /// The address as text <see cref="OracleAddress.Parse"/> reads back: EZConnect with a service, a descriptor with a
    /// SID (EZConnect has no SID), else the TNS alias.
    /// </summary>
    private static string AddressText(OracleAddress address) => address switch
    {
        HostPortAddress { ServiceName.Length: > 0 } a => $"{a.Host}:{a.Port.ToString(CultureInfo.InvariantCulture)}/{a.ServiceName}",
        HostPortAddress a => $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={a.Host})(PORT={a.Port.ToString(CultureInfo.InvariantCulture)}))(CONNECT_DATA=(SID={a.Sid})))",
        TnsAliasAddress a => a.Alias,
        _ => address.Display,
    };

    private static void Add(StringBuilder uri, string key, string value) => Add(uri, key, [value]);

    private static void Add(StringBuilder uri, string key, IEnumerable<string> parts) =>
        uri.Append('&').Append(key).Append('=').AppendJoin(',', parts.Select(Uri.EscapeDataString));

    /// <summary>camelCase like in FerretSharp's JSON files: <c>startsWith</c>, <c>isNull</c>, <c>ddl</c>.</summary>
    private static string Name<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Names only (no numbers, which <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would take), any letter case.</summary>
    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum
    {
        value = default;
        return text.Length > 0 && text.All(char.IsAsciiLetter) && Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static string Unescape(string raw)
    {
        try
        {
            return Uri.UnescapeDataString(raw.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return raw;
        }
    }

    private static TableLinkRead Failed(string error) => new(null, error);
}

/// <summary>A link read from outside: the link, or why it cannot be opened.</summary>
public sealed record TableLinkRead(TableLink? Link, string? Error);
