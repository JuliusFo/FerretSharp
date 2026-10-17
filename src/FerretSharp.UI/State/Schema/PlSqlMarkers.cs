using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>Compile errors of a PL/SQL part as editor markers (WP-28).</summary>
public static class PlSqlMarkers
{
    /// <summary>
    /// The errors of <paramref name="part"/>, each underlining the word it points at (at least one character).
    /// Positions outside the text (the source changed since compiling) are kept within it.
    /// </summary>
    public static IReadOnlyList<LinqDiagnostic> For(IEnumerable<PlSqlError> errors, PlSqlPart part, IReadOnlyList<string> lines) =>
        errors.Where(e => e.Part == part)
            .Select(e =>
            {
                var line = Math.Clamp(e.Line, 1, Math.Max(1, lines.Count));
                var text = lines.Count >= line ? lines[line - 1] : "";
                var column = Math.Clamp(e.Column, 1, text.Length + 1);
                var end = column - 1;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$' or '#'))
                {
                    end++;
                }

                return new LinqDiagnostic("", line, column, line, Math.Max(end + 1, column + 1), e.IsWarning ? "warning" : "error", "", e.Text);
            })
            .ToList();
}
