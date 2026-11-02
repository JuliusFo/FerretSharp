using FerretSharp.Core.Settings;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// How a project writes the C# of a query from the diagram (WP-69): the last choice per linked project, kept in the
/// settings (<see cref="AppSettings.QueryCodeStyles"/>) as flat entries <c>project file + '|' + field</c> – a settings map
/// stays a value, and the file stays readable.
/// </summary>
/// <param name="Template">
/// The project's own mapping lines; null for the projection. Kept as typed, also without a line (review: a head typed
/// first was lost); the code takes it only with one (<see cref="Options"/>).
/// </param>
public sealed record QueryCodeStyle(string? Namespace = null, bool WithDto = true, bool DtoAsRecord = true, bool LinqKit = true, QueryLineTemplate? Template = null)
{
    public static readonly QueryCodeStyle Default = new();

    private const char Separator = '|';

    /// <summary>The style last used for <paramref name="projectFile"/>; the default without one.</summary>
    public static QueryCodeStyle Of(SettingsMap styles, string projectFile)
    {
        string? Get(string field) => styles.TryGetValue(projectFile + Separator + field, out var value) ? value : null;
        bool Flag(string field, bool fallback) => Get(field) is { } value ? value == "true" : fallback;

        var template = new QueryLineTemplate(Get("head") ?? "", Get("line") ?? "", Get("foot") ?? "");
        return new QueryCodeStyle(
            Get("namespace"),
            Flag("dto", Default.WithDto),
            Flag("record", Default.DtoAsRecord),
            Flag("linqkit", Default.LinqKit),
            template is { Head.Length: 0, Line.Length: 0, Foot.Length: 0 } ? null : template);
    }

    /// <summary>The settings map with this style for <paramref name="projectFile"/>.</summary>
    public SettingsMap Into(SettingsMap styles, string projectFile)
    {
        var map = styles;
        void Set(string field, string? value) =>
            map = value is null ? map.Without(projectFile + Separator + field) : map.With(projectFile + Separator + field, value);

        Set("namespace", Namespace is { Length: > 0 } ? Namespace : null);
        Set("dto", WithDto ? "true" : "false");
        Set("record", DtoAsRecord ? "true" : "false");
        Set("linqkit", LinqKit ? "true" : "false");
        Set("head", Template?.Head is { Length: > 0 } head ? head : null);
        Set("line", Template?.Line is { Length: > 0 } line ? line : null);
        Set("foot", Template?.Foot is { Length: > 0 } foot ? foot : null);
        return map;
    }

    /// <summary>The options of one query: this style with the query's names; the template only with a line for the values.</summary>
    public QueryCodeOptions Options(QueryCodeOptions names) =>
        names with
        {
            Namespace = Namespace, WithDto = WithDto, DtoAsRecord = DtoAsRecord, LinqKit = LinqKit,
            Template = Template is { Line.Length: > 0 } ? Template : null,
        };
}
