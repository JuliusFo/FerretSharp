using System.Globalization;

namespace FerretSharp.Core.Data;

/// <summary>
/// The culture of values the user reads and types – cells, filters, edits, exports, counts in messages: German notation
/// ("1.234,5", <c>TT.MM.JJJJ</c>), whatever the language of the UI. One place (R5b: it stood in seven classes); a format
/// culture of its own is in the backlog.
/// </summary>
internal static class ValueCulture
{
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
}
