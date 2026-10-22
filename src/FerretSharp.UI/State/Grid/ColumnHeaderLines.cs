using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>
/// The last line of a column header – Oracle type and <c>NOT NULL</c> – as the settings show it (table grid and SQL result).
/// Each part can be hidden; with both hidden the header has no such line and is lower.
/// </summary>
public sealed record ColumnHeaderLines(bool Types, bool NotNull)
{
    /// <summary>Header height with the name alone (no C# line, no type line).</summary>
    public const int NameOnlyHeight = 30;

    /// <summary>The mark of a column that must have a value, in the header and in the setting's label.</summary>
    public const string NotNullText = "NOT NULL";

    public static readonly ColumnHeaderLines All = new(true, true);

    public static ColumnHeaderLines Of(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings.ShowColumnTypes, settings.ShowNotNull);
    }

    /// <summary>The header has the line at all (empty for a nullable column with the types hidden).</summary>
    public bool TypeLine => Types || NotNull;

    /// <summary>What the line shows for a column: <c>NUMBER(10) · NOT NULL</c>, a part of it, or nothing.</summary>
    public string TextOf(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return TextOf(column.DisplayType, column.Nullable);
    }

    public string TextOf(string type, bool nullable) =>
        string.Join(" · ", new[] { Types ? type : null, NotNull && !nullable ? NotNullText : null }.Where(p => p is not null));

    /// <summary>
    /// The whole line for the header's tooltip when the line hides part of it (<c>NUMBER(10) · NOT NULL</c>); null when the
    /// header shows it all.
    /// </summary>
    public string? HintOf(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var all = All.TextOf(column);
        return all == TextOf(column) ? null : all;
    }

    /// <summary>The header height for grid.js: null for the theme's 46 px (name and one more line).</summary>
    public int? HeightWith(bool clrLine) => (clrLine, TypeLine) switch
    {
        (true, true) => GridOptions.ClrHeaderHeight,
        (false, false) => NameOnlyHeight,
        _ => null,
    };
}
