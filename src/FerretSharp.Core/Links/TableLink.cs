using System.Globalization;
using System.Text;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Links;

/// <summary>
/// A link to a table view (WP-37, ADR 0026): connection, table, filters, sort order and optionally the view of the tab –
/// never SQL, only the filter model, which the receiver checks against its schema like any filter. It opens one tab
/// for reading, after a confirmation. Format (each part percent-encoded on its own, so commas inside values survive):
/// <c>ferretsharp://open?v=1&amp;c=ERP%20Test&amp;a=db01%3A1521%2FERP&amp;s=ERP&amp;t=AUFTRAG&amp;f=STATUS,in,OFFEN,NEU&amp;o=ERSTELLT,desc&amp;m=data</c>.
/// A row of a filter group (WP-50) is a <c>g</c> instead of an <c>f</c>, with the group's id and kind in front:
/// <c>g=1,any,KBEZ,equals,NA</c>. Such a link is version 2, so an older FerretSharp refuses it instead of reading the
/// group's rows as AND.
/// </summary>
/// <param name="Connection">The sender's name of the connection; the receiver looks it up (<see cref="LinkConnectionMatch"/>).</param>
/// <param name="Address">The sender's address of the connection; null if the link has none.</param>
/// <param name="Mode">The view of the tab; null for the data view.</param>
public sealed record TableLink(
    string? Connection, OracleAddress? Address, string Owner, string Table,
    IReadOnlyList<FilterCondition> Filters, IReadOnlyList<SortSpec> Sorts, TabMode? Mode = null)
{
    public const string Scheme = "ferretsharp";

    /// <summary>The newest format this FerretSharp writes and reads; a link of a higher version comes from a newer one.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The version of a link without filter groups: what FerretSharp before WP-50 reads.</summary>
    private const int VersionWithoutGroups = 1;

    /// <summary>
    /// The page that turns <see cref="ToWebUri"/> back into a <c>ferretsharp://</c> link in the browser (ADR 0026,
    /// addendum): Teams, Azure DevOps and most chats only make https addresses clickable. A static page on GitHub Pages
    /// (<c>pages/open/index.html</c>); it reads the part after <c>#</c>, which the browser never sends to a server.
    /// </summary>
    public const string WebPage = ProjectSite.Pages + "open/";

    /// <summary>What <see cref="ToUri"/> starts with and the redirect page puts in front of the part after <c>#</c>.</summary>
    public const string UriStart = Scheme + "://open?";

    /// <summary>
    /// Limits for a link from outside: longer than a browser or mail client keeps reliably anyway, and the IN list of a
    /// link to selected rows (<see cref="FkNavigation.MaxValues"/>) has room.
    /// </summary>
    public const int MaxLength = 32 * 1024;

    public const int MaxFilters = 50;

    public const int MaxValues = FkNavigation.MaxValues;

    private const int MaxConnectionName = 200;

    /// <summary>
    /// The link to a view of <paramref name="table"/> on <paramref name="profile"/>. Only the rows that filter go in: a
    /// disabled row, or one of a disabled group, filters nothing.
    /// </summary>
    public static TableLink For(ConnectionProfile profile, TableRef table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, TabMode mode) =>
        new(profile.Name, profile.Address, table.Owner, table.Name, FilterGroups.Normalize(FilterGroups.Active(filters).ToList()), sorts, mode == TabMode.Data ? null : mode);

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

    /// <summary>
    /// The text a second start hands over (the pipe): at most one character more than a link may have, so a link that
    /// was too long is still refused as such – cut to <see cref="MaxLength"/>, it could read as another, valid link.
    /// </summary>
    public static string ForTransfer(string text) => text.Length > MaxLength ? text[..(MaxLength + 1)] : text;

    /// <summary>
    /// The link as an https address that chats and tickets make clickable: <see cref="WebPage"/>, then after <c>#</c> the
    /// same parameters as <see cref="ToUri"/>. The page opens <see cref="UriStart"/> plus that part; the values stay in the
    /// browser (a fragment is never sent to the server).
    /// </summary>
    public string ToWebUri() => WebPage + "#" + ToUri()[UriStart.Length..];

    public string ToUri()
    {
        var version = Filters.Any(f => f.Group is not null) ? CurrentVersion : VersionWithoutGroups;
        var parts = new StringBuilder();
        var count = AddParts(parts);

        // Review 3.40.0, A26: "n" up front counts what follows, "e" ends the link – one cut off on the way (a mail in plain
        // text, a ticket field) is told from a whole one. Older versions ignore both.
        return new StringBuilder(UriStart).Append("v=").Append(version).Append("&n=").Append(count + 1).Append(parts).Append("&e=1").ToString();
    }

    /// <summary>The parameters after the version; returns how many.</summary>
    private int AddParts(StringBuilder uri)
    {
        var count = 0;
        void Add(string key, IEnumerable<string> values)
        {
            TableLink.Add(uri, key, values);
            count++;
        }

        if (Connection is { Length: > 0 } connection)
        {
            Add("c", [connection]);
        }

        if (Address is { } address)
        {
            Add("a", [AddressText(address)]);
        }

        Add("s", [Owner]);
        Add("t", [Table]);
        foreach (var filter in Filters)
        {
            if (filter.Group is { } group)
            {
                Add("g", [group.Id.ToString(CultureInfo.InvariantCulture), Name(group.Kind), filter.Column, Name(filter.Op), .. filter.Values]);
            }
            else
            {
                Add("f", [filter.Column, Name(filter.Op), .. filter.Values]);
            }
        }

        foreach (var sort in Sorts)
        {
            Add("o", [sort.Column, sort.Descending ? "desc" : "asc"]);
        }

        if (Mode is { } mode)
        {
            Add("m", [Name(mode)]);
        }

        return count;
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

        // A link written since 3.40.1 says up front how many parameters follow and ends with "e" (A26); one without "n" is
        // older and opens as before.
        if (parameters.FindIndex(p => p.Key == "n") is var counted and >= 0
            && (!int.TryParse(Unescape(parameters[counted].Raw), NumberStyles.None, CultureInfo.InvariantCulture, out var following)
                || following != parameters.Count - counted - 1 || parameters[^1] is not ("e", "1")))
        {
            return Failed(QueryText.LinkIncomplete);
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
        var groups = new Dictionary<int, FilterGroup>();
        var values = 0;
        foreach (var (key, raw) in parameters.Where(p => p.Key is "f" or "g"))
        {
            if (filters.Count == MaxFilters)
            {
                return Failed(TextFormat.Format(QueryText.LinkTooManyFilters, MaxFilters));
            }

            var parts = raw.Split(',').Select(Unescape).ToArray();
            FilterGroup? group = null;
            if (key == "g")
            {
                // The group's id and kind, then the row as in "f"; the first row of a group decides its kind.
                // Ids are numbered from 1 (FilterGroups.Normalize): at most one group per row.
                if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id is < 1 or > MaxFilters
                    || !TryEnum<FilterGroupKind>(parts[1], out var kind))
                {
                    return Failed(TextFormat.Format(QueryText.LinkFilterInvalid, parts.ElementAtOrDefault(2) ?? parts[0]));
                }

                if (!groups.TryGetValue(id, out group))
                {
                    groups[id] = group = new FilterGroup(id, kind);
                }

                parts = parts[2..];
            }

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

            filters.Add(new FilterCondition(parts[0], op, filterValues) { Group = group });
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

    /// <summary>The address as text <see cref="OracleAddress.Parse"/> reads back (<see cref="OracleAddress.ToDataSource"/>).</summary>
    private static string AddressText(OracleAddress address) => address.ToDataSource() ?? address.Display;

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
