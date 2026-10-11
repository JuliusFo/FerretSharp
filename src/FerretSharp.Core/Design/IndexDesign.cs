using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Ddl;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Design;

/// <summary>A new index as the user puts it together (WP-23).</summary>
/// <param name="Name">The index name in dictionary form (as stored, quoted exactly so in the DDL).</param>
/// <param name="Columns">Column names of the table, in index order.</param>
public sealed record IndexDraft(string Name, IReadOnlyList<string> Columns, bool Unique = false);

/// <summary>What the designer found about a draft.</summary>
/// <param name="Problems">Reasons the DDL cannot run (or would certainly fail); empty if it can.</param>
/// <param name="Warnings">Worth knowing, but no reason to stop.</param>
public sealed record DraftCheck(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public bool CanRun => Problems.Count == 0;
}

/// <summary>
/// "Create index" in the Indexes view and for a foreign key without index (WP-23, stage 1): suggests a name, checks a
/// draft against the table and its indexes, and writes the <c>CREATE INDEX</c> – the same way the DDL proposal of the
/// schema comparison writes one (<see cref="OracleDdl.CreateIndex"/>). Pure logic, no database access.
/// </summary>
public static partial class IndexDesign
{
    /// <summary>Oracle's limit for names since 12.2 (in bytes, not characters).</summary>
    public const int MaxNameBytes = 128;

    /// <summary>
    /// <c>IX_&lt;TABLE&gt;_&lt;COLUMNS&gt;</c> like the DDL proposal (WP-20), as a plain name (upper case, other characters as
    /// <c>_</c>: <c>"Nummer"</c> → <c>NUMMER</c>), cut to <see cref="MaxNameBytes"/>; a name the table's indexes already use
    /// gets <c>_2</c>, <c>_3</c> ….
    /// </summary>
    public static string SuggestName(string table, IReadOnlyList<string> columns, IEnumerable<string> taken) =>
        PlainName($"IX_{table}" + string.Concat(columns.Select(c => "_" + c)), taken);

    /// <summary>
    /// <paramref name="stem"/> as a plain name (upper case, other characters as <c>_</c>), cut to <see cref="MaxNameBytes"/>;
    /// one in <paramref name="taken"/> gets <c>_2</c>, <c>_3</c> …. Shared by the names of indexes and constraints.
    /// </summary>
    internal static string PlainName(string stem, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.Ordinal);
        stem = NotPlain().Replace(stem.ToUpperInvariant(), "_");
        var name = Cut(stem, MaxNameBytes);
        for (var n = 2; used.Contains(name); n++)
        {
            var suffix = $"_{n}";
            name = Cut(stem, MaxNameBytes - suffix.Length) + suffix;
        }

        return name;
    }

    /// <summary>The draft for "create an index for this foreign key": its columns in constraint order.</summary>
    public static IndexDraft ForForeignKey(ForeignKeyInfo foreignKey, IReadOnlyList<IndexInfo> indexes)
    {
        ArgumentNullException.ThrowIfNull(foreignKey);
        return new IndexDraft(SuggestName(foreignKey.From.Name, foreignKey.FromColumns, indexes.Select(i => i.Name)), foreignKey.FromColumns);
    }

    /// <summary>Checks the draft against the table's columns and its existing indexes.</summary>
    public static DraftCheck Check(IndexDraft draft, TableDetails table, IReadOnlyList<IndexInfo> indexes)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(table);
        var problems = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            problems.Add(DesignText.IndexNameMissing);
        }
        else if (draft.Name.Contains('"', StringComparison.Ordinal))
        {
            problems.Add(DesignText.IndexNameInvalid);
        }
        else
        {
            if (Encoding.UTF8.GetByteCount(draft.Name) > MaxNameBytes)
            {
                problems.Add(TextFormat.Format(DesignText.IndexNameTooLong, MaxNameBytes));
            }

            if (indexes.Any(i => string.Equals(i.Name, draft.Name, StringComparison.Ordinal)))
            {
                problems.Add(TextFormat.Format(DesignText.IndexNameTaken, draft.Name));
            }
            else if (!PlainName().IsMatch(draft.Name))
            {
                warnings.Add(DesignText.IndexNameNeedsQuotes);
            }
        }

        if (draft.Columns.Count == 0)
        {
            problems.Add(DesignText.IndexNoColumns);
            return new DraftCheck(problems, warnings);
        }

        foreach (var name in draft.Columns.Distinct(StringComparer.Ordinal))
        {
            if (draft.Columns.Count(c => c == name) > 1)
            {
                problems.Add(TextFormat.Format(DesignText.IndexColumnTwice, name));
            }

            var index = table.IndexOf(name);
            if (index < 0)
            {
                problems.Add(TextFormat.Format(DesignText.IndexColumnUnknown, name));
            }
            else if (OracleTypes.IsLobOrLong(table.Columns[index].DataType))
            {
                problems.Add(TextFormat.Format(DesignText.IndexColumnLob, name, table.Columns[index].DataType));
            }
        }

        // Oracle refuses a second index on the same column list (ORA-01408); one starting with the same columns
        // already serves lookups by them – also for a foreign key (IndexAdvice counts it as covering).
        if (indexes.FirstOrDefault(i => SameColumns(i, draft.Columns)) is { } twin)
        {
            problems.Add(TextFormat.Format(DesignText.IndexSameColumns, twin.Name));
        }
        else if (!draft.Unique && indexes.FirstOrDefault(i => IndexAdvice.Covers(i, draft.Columns)) is { } covering)
        {
            warnings.Add(TextFormat.Format(DesignText.IndexCoveredBy, covering.Name));
        }

        return new DraftCheck(problems, warnings);
    }

    /// <summary><c>CREATE [UNIQUE ]INDEX "OWNER"."NAME" ON "OWNER"."TABLE" ("A", "B")</c> – the index in the table's schema.</summary>
    public static string Ddl(IndexDraft draft, TableRef table)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(table);
        var index = new IndexInfo(table.Owner, draft.Name, "NORMAL", draft.Unique, "VALID",
            draft.Columns.Select(c => new IndexColumn(c, IsExpression: false, Descending: false)).ToList(), null, false, Visible: true);
        return OracleDdl.CreateIndex(index, draft.Name, table.Owner, table.Name)
               ?? throw new InvalidOperationException("A normal index always has DDL.");
    }

    /// <summary>The <see cref="Ddl"/> as a step of a DDL script.</summary>
    public static DdlStep Step(IndexDraft draft, TableRef table) => new(table?.Name ?? throw new ArgumentNullException(nameof(table)), DdlStepKind.CreateIndex, Ddl(draft, table));

    private static bool SameColumns(IndexInfo index, IReadOnlyList<string> columns) =>
        index.Columns.Count == columns.Count && index.Columns.All(c => !c.IsExpression) && index.Columns.Select(c => c.Name).SequenceEqual(columns, StringComparer.Ordinal);

    /// <summary>The longest start of <paramref name="text"/> within <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    private static string Cut(string text, int maxBytes)
    {
        var length = text.Length;
        while (Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > maxBytes)
        {
            length--;
        }

        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--; // never half a character
        }

        return text[..length];
    }

    /// <summary>A name Oracle takes without quotes: a letter, then letters, digits, _, $ and #, upper case.</summary>
    [GeneratedRegex(@"^\p{Lu}[\p{Lu}\p{Nd}_$#]*$")]
    private static partial Regex PlainName();

    [GeneratedRegex(@"[^\p{Lu}\p{Nd}_$#]")]
    private static partial Regex NotPlain();
}
