using System.Text;

using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Compare;

/// <summary>
/// The one-line definitions <see cref="SchemaDiff"/> compares (<see cref="CompareCell.Definition"/>). Two sides agree
/// exactly when their texts are equal, so everything that does not count (storage, tablespace, statistics, comments,
/// index status, generated names, the schema of a foreign key into the own schema) stays out of the text.
/// </summary>
internal static class CompareDefinitions
{
    /// <summary><c>TABLE</c>, <c>GLOBAL TEMPORARY TABLE</c>, <c>TABLE (IOT, PARTITIONED)</c>, <c>VIEW</c>, <c>MATERIALIZED VIEW</c>.</summary>
    public static string Object(ObjectSnapshot o)
    {
        var kind = o.Kind switch
        {
            TableKind.View => "VIEW",
            TableKind.MaterializedView => "MATERIALIZED VIEW",
            _ => "TABLE",
        };

        var flags = new List<string>();
        if (o.IsIndexOrganized)
        {
            flags.Add("IOT");
        }

        if (o.Partitioned)
        {
            flags.Add("PARTITIONED");
        }

        var text = o.Temporary ? "GLOBAL TEMPORARY " + kind : kind;
        return flags.Count == 0 ? text : $"{text} ({string.Join(", ", flags)})";
    }

    /// <summary>
    /// <c>VARCHAR2(200 CHAR) NOT NULL DEFAULT 'x'</c>, <c>NUMBER(12) NOT NULL IDENTITY</c>,
    /// <c>NUMBER NULL VIRTUAL AS (A + B)</c>; views and materialized views only type and nullability. With
    /// <paramref name="position"/> (column order counts) <c> · #3</c> is appended.
    /// </summary>
    /// <param name="position">1-based place in the side's visible columns (not <see cref="ColumnInfo.Position"/>, which may have gaps).</param>
    public static string Column(ColumnInfo c, TableKind kind, int? position)
    {
        var text = new StringBuilder(c.DisplayType).Append(c.Nullable ? " NULL" : " NOT NULL");
        if (kind == TableKind.Table)
        {
            if (c.IsVirtual)
            {
                text.Append(" VIRTUAL AS (").Append(Normalize(c.Default ?? "")).Append(')');
            }
            else if (c.IsIdentity)
            {
                // The default of an identity column is the NEXTVAL of its sequence ISEQ$$_<object id>: differs on every
                // database and says nothing.
                text.Append(c.DefaultOnNull ? " IDENTITY ON NULL" : " IDENTITY");
            }
            else if (Default(c.Default) is { } expression)
            {
                text.Append(c.DefaultOnNull ? " DEFAULT ON NULL " : " DEFAULT ").Append(expression);
            }
        }

        if (position is { } p)
        {
            text.Append(" · #").Append(p);
        }

        return text.ToString();
    }

    /// <summary>
    /// The default expression normalized; null without one. <c>DEFAULT NULL</c> is no default: Oracle keeps the text
    /// <c>NULL</c> after <c>MODIFY … DEFAULT NULL</c>, the usual way to remove a default.
    /// </summary>
    private static string? Default(string? text)
    {
        var expression = text is null ? "" : Normalize(text);
        return expression.Length == 0 || expression.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : expression;
    }

    /// <summary>
    /// What identifies a constraint whose name Oracle generated: <c>PRIMARY KEY (A, B)</c>, <c>UNIQUE (A)</c>,
    /// <c>FOREIGN KEY (X) REFERENCES T (Y)</c>, <c>CHECK (MENGE &gt; 0)</c>. Also the shown name of such a row.
    /// </summary>
    /// <param name="owner">The side's schema: references into it are written without owner, so <c>ERP</c> and <c>ERP_TEST</c> agree.</param>
    public static string ConstraintSignature(ConstraintInfo c, string owner) => c.Type switch
    {
        ConstraintType.PrimaryKey => $"PRIMARY KEY ({List(c.Columns)})",
        ConstraintType.Unique => $"UNIQUE ({List(c.Columns)})",
        ConstraintType.ForeignKey => $"FOREIGN KEY ({List(c.Columns)}) REFERENCES {Referenced(c.References, owner)} ({List(c.ReferencedColumns)})",
        ConstraintType.Check => $"CHECK ({Normalize(c.Condition ?? "")})",
        _ => c.Type.ToString().ToUpperInvariant(),
    };

    /// <summary>The signature plus delete rule and state: <c>… ON DELETE CASCADE</c>, <c>DISABLED</c>, <c>NOVALIDATE</c>, <c>DEFERRABLE INITIALLY DEFERRED</c>.</summary>
    public static string Constraint(ConstraintInfo c, string owner)
    {
        var text = new StringBuilder(ConstraintSignature(c, owner));
        if (c.Type == ConstraintType.ForeignKey && c.DeleteRule is { } rule && rule != "NO ACTION")
        {
            text.Append(" ON DELETE ").Append(rule);
        }

        if (!c.Enabled)
        {
            text.Append(" DISABLED");
        }

        if (!c.Validated)
        {
            text.Append(" NOVALIDATE");
        }

        if (c.Deferrable)
        {
            text.Append(c.InitiallyDeferred ? " DEFERRABLE INITIALLY DEFERRED" : " DEFERRABLE");
        }

        return text.ToString();
    }

    /// <summary>
    /// What identifies an index whose name Oracle generated: its columns, <c>(A, B DESC, UPPER("NAME"))</c>. Uniqueness
    /// and type stay out, so an index that became unique shows as a difference, not as two rows.
    /// </summary>
    public static string IndexSignature(IndexInfo index) =>
        "(" + string.Join(", ", index.Columns.Select(c => (c.IsExpression ? Normalize(c.Name) : c.Name) + (c.Descending ? " DESC" : ""))) + ")";

    /// <summary><c>UNIQUE INDEX (A)</c>, <c>BITMAP INDEX (A)</c>, <c>INDEX (A) REVERSE</c> – without <c>INVISIBLE</c>; also the shown name of a generated one.</summary>
    public static string IndexDescription(IndexInfo index)
    {
        var kind = index.IndexType.StartsWith("DOMAIN", StringComparison.Ordinal) ? "DOMAIN INDEX"
            : index.Unique ? "UNIQUE INDEX"
            : index.IndexType.Contains("BITMAP", StringComparison.Ordinal) ? "BITMAP INDEX"
            : "INDEX";
        var reverse = index.IndexType.EndsWith("/REV", StringComparison.Ordinal) ? " REVERSE" : "";
        return $"{kind} {IndexSignature(index)}{reverse}";
    }

    /// <summary>The description plus <c>INVISIBLE</c>. Tablespace, status (UNUSABLE is a state) and partitioning do not count.</summary>
    public static string Index(IndexInfo index) => index.Visible ? IndexDescription(index) : IndexDescription(index) + " INVISIBLE";

    /// <summary>
    /// An expression as Oracle stored it (default, virtual column, check condition, index expression), trimmed and with
    /// runs of whitespace outside string literals and quoted identifiers collapsed to one space: <c>DATA_DEFAULT</c>
    /// keeps whatever spaces and line breaks the DDL had.
    /// </summary>
    public static string Normalize(string expression)
    {
        var text = new StringBuilder(expression.Length);
        char? quote = null;
        var space = false;
        foreach (var ch in expression.AsSpan().Trim())
        {
            if (quote is { } open)
            {
                text.Append(ch);
                if (ch == open)
                {
                    quote = null; // 'it''s' closes and opens again: still right
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
            }

            text.Append(ch);
        }

        return text.ToString();
    }

    private static string List(IReadOnlyList<string> names) => string.Join(", ", names);

    private static string Referenced(TableRef? table, string owner) =>
        table is null ? "?" : table.Owner == owner ? table.Name : $"{table.Owner}.{table.Name}";
}
