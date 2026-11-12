using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FerretSharp.Testing;

/// <summary>
/// Checks the localized texts of an assembly (WP-29, ADR 0017): every <c>Resources/&lt;Area&gt;Text.resx</c> has the same keys
/// in English and German, no empty text, and both languages use the same placeholders.
/// </summary>
internal static partial class ResourceChecks
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    /// <summary>The generated text classes of the assembly (namespace <c>….Resources</c>, name <c>…Text</c>).</summary>
    public static IReadOnlyList<Type> TextClasses(Assembly assembly) =>
        [.. assembly.GetTypes()
            .Where(t => t.Namespace?.EndsWith(".Resources", StringComparison.Ordinal) == true
                && t.Name.EndsWith("Text", StringComparison.Ordinal)
                && t.GetProperty("ResourceManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>
    /// The English texts of a project's <c>Resources/*.resx</c> with a placeholder but no comment that says what it stands for
    /// (review 3.70.0, D11: 44 such texts, after D14 of 3.54 the second time). The translator sees only the text.
    /// </summary>
    public static IReadOnlyList<string> UncommentedPlaceholders(string project) =>
        [.. Directory.GetFiles(Path.Combine(Repository.Root, "src", project, "Resources"), "*.resx")
            .Where(f => !f.EndsWith(".de.resx", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => XDocument.Load(f).Root!.Elements("data")
                .Where(d => Placeholder().IsMatch((string?)d.Element("value") ?? "") && string.IsNullOrWhiteSpace((string?)d.Element("comment")))
                .Select(d => $"{Path.GetFileName(f)}: {(string?)d.Attribute("name")}"))
            .Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{\d")]
    private static partial Regex Placeholder();

    /// <summary>Everything wrong with the texts of one class, as readable lines; empty if all is well.</summary>
    public static IReadOnlyList<string> Problems(Type textClass)
    {
        var manager = (ResourceManager)textClass
            .GetProperty("ResourceManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var english = Read(manager, CultureInfo.InvariantCulture);
        var german = Read(manager, German);
        var problems = new List<string>();
        if (german is null)
        {
            return [$"{textClass.Name}: no German resources ({textClass.Name}.de.resx missing or not built)"];
        }

        problems.AddRange(english!.Keys.Except(german.Keys).Order(StringComparer.Ordinal).Select(k => $"{textClass.Name}.{k}: missing in German"));
        problems.AddRange(german.Keys.Except(english.Keys).Order(StringComparer.Ordinal).Select(k => $"{textClass.Name}.{k}: only in German"));
        foreach (var key in english.Keys.Intersect(german.Keys).Order(StringComparer.Ordinal))
        {
            var (en, de) = (english[key], german[key]);
            if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(de))
            {
                problems.Add($"{textClass.Name}.{key}: empty text");
                continue;
            }

            var (enHoles, deHoles) = (Placeholders(en), Placeholders(de));
            if (enHoles is null || deHoles is null)
            {
                problems.Add($"{textClass.Name}.{key}: braces do not form valid placeholders ({{0}}; literal braces as {{{{ and }}}})");
            }
            else if (!enHoles.SetEquals(deHoles))
            {
                problems.Add($"{textClass.Name}.{key}: placeholders differ (English {{{string.Join("},{", enHoles.Order())}}}, German {{{string.Join("},{", deHoles.Order())}}})");
            }
        }

        return problems;
    }

    private static Dictionary<string, string>? Read(ResourceManager manager, CultureInfo culture)
    {
        // tryParents: false – only what the resource for exactly this culture contains, no fallback to English.
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        return set?.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => e.Value as string ?? "", StringComparer.Ordinal);
    }

    /// <summary>The placeholder indexes of a text, null if a brace is not part of a valid placeholder.</summary>
    private static HashSet<int>? Placeholders(string text)
    {
        var holes = new HashSet<int>();
        var rest = Token().Replace(text, m =>
        {
            if (m.Groups[1].Success)
            {
                holes.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            }

            return "";
        });
        return rest.Contains('{', StringComparison.Ordinal) || rest.Contains('}', StringComparison.Ordinal) ? null : holes;
    }

    [GeneratedRegex(@"\{\{|\}\}|\{(\d+)(?:,-?\d+)?(?::[^{}]*)?\}")]
    private static partial Regex Token();
}
