namespace FerretSharp.Core.IO;

/// <summary>
/// A path typed or pasted into a field – one rule for all of them (review 3.40.0, A19): without surrounding blanks and the
/// quotes Explorer's "Copy as path" adds. Before, the audit template of a group or connection dropped the quotes and the
/// global one did not: the same pasted path worked in one field and was "missing" in the other.
/// </summary>
public static class PathInput
{
    /// <returns>The path; null if nothing is left.</returns>
    public static string? Normalize(string? text) =>
        text?.Trim().Trim('"').Trim() is { Length: > 0 } path ? path : null;
}
