using System.Text.Json;
using System.Text.Json.Serialization;

namespace FerretSharp.Core.IO;

/// <summary>
/// How FerretSharp's own JSON files are written (connections, settings, workspaces, recent connections, saved comparisons):
/// indented, camelCase, enums as text, nulls left out. One place, so the stores cannot drift apart.
/// </summary>
internal static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
