using System.Text.Json;
using System.Text.Json.Serialization;

namespace FerretSharp.Core.IO;

/// <summary>
/// How FerretSharp's own JSON files are written (connections, settings, workspaces, recent connections, saved comparisons):
/// indented, camelCase, enums as text, nulls left out, umlauts and <c>+</c> as they are (files for people to read, not
/// HTML). One place, so the stores cannot drift apart.
/// </summary>
internal static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>As <see cref="Options"/>, without indentation: for caches nobody reads by hand (review 3.44.0, A11).</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
