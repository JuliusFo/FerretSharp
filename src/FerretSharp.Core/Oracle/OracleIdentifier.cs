namespace FerretSharp.Core.Oracle;

public static class OracleIdentifier
{
    /// <summary>
    /// Quotes a name exactly as stored in the data dictionary, so mixed-case and special names work:
    /// <c>KUNDEN</c> → <c>"KUNDEN"</c>, <c>MixedCase</c> → <c>"MixedCase"</c>.
    /// </summary>
    public static string Quote(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('"', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Oracle identifiers cannot contain double quotes or NUL: {name}", nameof(name));
        }

        return $"\"{name}\"";
    }

    /// <summary><c>"OWNER"."NAME"</c>.</summary>
    public static string Qualify(string owner, string name) => $"{Quote(owner)}.{Quote(name)}";

    /// <summary>
    /// Converts a name as a user would type it into the dictionary form: unquoted names are upper-cased
    /// (<c>erp</c> → <c>ERP</c>), quoted names keep their case (<c>"Erp"</c> → <c>Erp</c>).
    /// </summary>
    public static string Normalize(string typed)
    {
        var trimmed = typed.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed.ToUpperInvariant();
    }
}
