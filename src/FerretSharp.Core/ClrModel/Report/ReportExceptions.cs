using System.Text.Json;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// A table that is meant to have no entity (WP-72): by name without owner (<c>IMPORT_TMP</c>) or by a pattern with
/// <c>*</c> (<c>AUD_*</c>, <c>*_BAK</c>), with the reason someone gave.
/// </summary>
public sealed record ReportException(string Table, string Reason)
{
    public bool IsPattern => Table.Contains('*', StringComparison.Ordinal);
}

/// <summary>
/// The deliberate exceptions of the report (WP-72, ADR 0042): which tables the list "tables without entity" leaves out.
/// Matched by table name without owner – the schema is the same on Prod, Test and Dev, its name may differ (as the display
/// columns, ADR 0022) – and ignoring case, so a typed <c>aud_*</c> means the same. The file is shared through a
/// repository and so foreign input: names are only compared, never part of SQL or a path, and a pattern is matched without
/// backtracking.
/// </summary>
public sealed class ReportExceptions
{
    public static readonly ReportExceptions Empty = new(new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private readonly SortedDictionary<string, string> _tables;
    private readonly List<ReportException> _patterns;

    private ReportExceptions(SortedDictionary<string, string> tables, int ignored = 0)
    {
        _tables = tables;
        Ignored = ignored;
        _patterns = [.. tables.Where(t => t.Key.Contains('*', StringComparison.Ordinal)).Select(t => new ReportException(t.Key, t.Value))];
    }

    /// <summary>Every entry, sorted by name (as written to the file).</summary>
    public IReadOnlyList<ReportException> Tables => [.. _tables.Select(t => new ReportException(t.Key, t.Value))];

    public int Count => _tables.Count;

    /// <summary>Entries of the file left out: blank or too long, a pattern with too many <c>*</c>, beyond <see cref="MaxEntries"/>.</summary>
    public int Ignored { get; }

    /// <summary>The entry that leaves the table out: its own name first, otherwise the first pattern that fits; null if none.</summary>
    public ReportException? Match(string table)
    {
        if (_tables.TryGetValue(table, out var reason) && !table.Contains('*', StringComparison.Ordinal))
        {
            return new ReportException(table, reason);
        }

        return _patterns.FirstOrDefault(p => Fits(p.Table, table));
    }

    /// <summary>The entry written exactly so (name or pattern, case ignored); null without one.</summary>
    public ReportException? Entry(string tableOrPattern) =>
        _tables.Where(t => string.Equals(t.Key, tableOrPattern.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(t => new ReportException(t.Key, t.Value))
            .FirstOrDefault();

    /// <summary>A copy with the entry set (an entry in another letter case is replaced); the name is trimmed.</summary>
    public ReportExceptions With(string tableOrPattern, string? reason)
    {
        var name = Clean(tableOrPattern) ?? throw new ArgumentException("An exception needs a table name or pattern.", nameof(tableOrPattern));
        var copy = new SortedDictionary<string, string>(_tables, StringComparer.OrdinalIgnoreCase);
        copy.Remove(name);
        copy[name] = reason?.Trim() ?? "";
        return new ReportExceptions(copy);
    }

    /// <summary>A copy without the entry (case ignored).</summary>
    public ReportExceptions Without(string tableOrPattern)
    {
        var copy = new SortedDictionary<string, string>(_tables, StringComparer.OrdinalIgnoreCase);
        copy.Remove(tableOrPattern.Trim());
        return new ReportExceptions(copy);
    }

    /// <summary>The same entries in the same spelling (to skip a reload that changed nothing).</summary>
    public bool SameAs(ReportExceptions other) => _tables.SequenceEqual(other._tables);

    /// <summary>A name or pattern as it is kept: trimmed; null if blank or longer than any Oracle name could be.</summary>
    public static string? Clean(string? tableOrPattern) =>
        tableOrPattern?.Trim() is { Length: > 0 and <= MaxNameLength } name ? name : null;

    /// <summary>Longer entries are ignored: 128 bytes of an Oracle name, with room for a few <c>*</c>.</summary>
    public const int MaxNameLength = 160;

    /// <summary>A reason is cut to this length for the report (it is shown in a list); the file keeps it whole.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Entries read at most: every table without entity is matched against every pattern while the report renders, so a
    /// huge file from a shared repository (foreign input) must not stall the UI thread.
    /// </summary>
    public const int MaxEntries = 2000;

    /// <summary>A pattern with more <c>*</c> is ignored (keeps matching one name cheap; <c>*_TMP_*</c> needs two).</summary>
    public const int MaxStars = 4;

    internal static ReportExceptions From(IReadOnlyDictionary<string, string?>? tables)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ignored = 0;
        foreach (var (table, reason) in tables ?? new Dictionary<string, string?>())
        {
            if (Clean(table) is not { } name || name.Count(c => c == '*') > MaxStars || (sorted.Count >= MaxEntries && !sorted.ContainsKey(name)))
            {
                ignored++;
                continue;
            }

            var text = reason?.Trim() ?? "";
            sorted[name] = text.Length > MaxReasonLength ? text[..MaxReasonLength] : text;
        }

        return new ReportExceptions(sorted, ignored);
    }

    /// <summary>
    /// <paramref name="name"/> fits <paramref name="pattern"/>, where <c>*</c> stands for any characters (none too), case
    /// ignored. Greedy with one fallback point – linear in practice, never exponential (a pattern is foreign input).
    /// </summary>
    internal static bool Fits(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (p < pattern.Length && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n]))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

/// <summary>
/// The file of the exceptions as it is written: a version and one line per table or pattern, sorted, so it diffs and merges
/// cleanly in Git. Properties a newer FerretSharp adds (exceptions for columns or relationships) are kept when this one
/// writes (<see cref="Other"/>).
/// </summary>
internal sealed class ReportExceptionDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Table name or pattern → why it has no entity.</summary>
    public SortedDictionary<string, string?> TablesWithoutEntity { get; set; } = new(StringComparer.Ordinal);

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }

    public static readonly JsonSerializerOptions Options = JsonFiles.Options;

    public ReportExceptions Exceptions() => ReportExceptions.From(TablesWithoutEntity);

    /// <summary>
    /// Sets one entry in the file as read – an entry in another letter case is replaced; every other line stays as it is,
    /// also one this FerretSharp ignores (a colleague's long reason, an entry of a newer version).
    /// </summary>
    public void Set(string tableOrPattern, string? reason)
    {
        var name = ReportExceptions.Clean(tableOrPattern) ?? throw new ArgumentException("An exception needs a table name or pattern.", nameof(tableOrPattern));
        var text = reason?.Trim() ?? "";
        Remove(name);
        TablesWithoutEntity[name] = text.Length > ReportExceptions.MaxReasonLength ? text[..ReportExceptions.MaxReasonLength] : text;
    }

    /// <summary>Removes the entry written so (case ignored); the other lines stay as they are.</summary>
    public void Remove(string tableOrPattern)
    {
        var name = tableOrPattern.Trim();
        // Sorted ordinally as written, whatever comparer the reader gave the dictionary.
        TablesWithoutEntity = new(
            (TablesWithoutEntity ?? []).Where(e => !string.Equals(e.Key?.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToDictionary(e => e.Key, e => e.Value),
            StringComparer.Ordinal);
    }
}
