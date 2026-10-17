using FerretSharp.Core.Settings;
using System.Text;
using System.Text.RegularExpressions;

namespace FerretSharp.UI.State;

public enum NoteBlockKind
{
    Heading,
    Paragraph,
    Bullet,

    /// <summary>An image of the project's own (WP-49); its alt text is the block's text.</summary>
    Image,
}

/// <param name="Image">The address of an <see cref="NoteBlockKind.Image"/> block, otherwise null.</param>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteSpan> Spans, Uri? Image = null);

/// <param name="Code">An inline code span (<c>`…`</c>).</param>
public sealed record NoteSpan(string Text, bool Code);

/// <summary>
/// The release notes of an update (WP-32) as plain blocks, for the update dialog. They are the version's section of
/// CHANGELOG.md, so only what it uses is understood: headings, bullets (with continuation lines), paragraphs, inline code;
/// links show their text, emphasis markers are dropped. No HTML is rendered: the notes come from the internet.
/// An image (WP-49) is a line of its own, <c>![alt](url)</c>, also below a bullet; it shows only from the project's
/// repository (<see cref="IsOwnImage"/>, the release workflow rewrites the changelog's relative paths to it), any other
/// address shows the alt text.
/// </summary>
public static partial class ReleaseNotes
{
    /// <summary>Where the release workflow puts the images of a release: the files of its tag.</summary>
    public const string ImageHost = "raw.githubusercontent.com";

    public const string ImagePath = ProjectSite.RawPath;

    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        NoteBlockKind? kind = null;
        var text = new StringBuilder();

        void Flush()
        {
            if (kind is { } k && text.Length > 0)
            {
                blocks.Add(new NoteBlock(k, Inline(text.ToString())));
            }

            kind = null;
            text.Clear();
        }

        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || IsRule(trimmed))
            {
                Flush();
            }
            else if (ImageLine().Match(trimmed) is { Success: true } image)
            {
                // Its own block even when indented below a bullet: an image in the middle of a sentence is not meant.
                Flush();
                var alt = image.Groups["alt"].Value.Trim();
                if (IsOwnImage(image.Groups["url"].Value) is { } url)
                {
                    blocks.Add(new NoteBlock(NoteBlockKind.Image, [new NoteSpan(alt, Code: false)], url));
                }
                else if (alt.Length > 0)
                {
                    blocks.Add(new NoteBlock(NoteBlockKind.Paragraph, [new NoteSpan(alt, Code: false)]));
                }
            }
            else if (trimmed.StartsWith('#'))
            {
                Flush();
                blocks.Add(new NoteBlock(NoteBlockKind.Heading, Inline(trimmed.TrimStart('#').Trim())));
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                Flush();
                kind = NoteBlockKind.Bullet;
                text.Append(trimmed[2..].Trim());
            }
            else
            {
                // A continuation of the bullet or paragraph before, or a new paragraph.
                kind ??= NoteBlockKind.Paragraph;
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(trimmed);
            }
        }

        Flush();
        return blocks;
    }

    /// <summary>
    /// The address if it is an image of the project's repository: https, exactly <see cref="ImageHost"/>, below
    /// <see cref="ImagePath"/>, and plain path segments only (no <c>..</c>, no escapes, no query) – the dialog loads it
    /// from the internet, so nothing but the project's own files.
    /// </summary>
    public static Uri? IsOwnImage(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Host, ImageHost, StringComparison.OrdinalIgnoreCase)
            || !url.IsDefaultPort
            || url.UserInfo.Length > 0
            || url.Query.Length > 0
            || url.Fragment.Length > 0
            || !address.StartsWith("https://" + ImageHost + ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = address[("https://" + ImageHost + ImagePath).Length..].Split('/');
        return segments.Length >= 2 && segments.All(s => SafeSegment().IsMatch(s) && s is not ("." or "..")) ? url : null;
    }

    [GeneratedRegex(@"^!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]+)\)$")]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeSegment();

    private static bool IsRule(string line) => line.Length >= 3 && line.All(c => c is '-' or '*' or '_');

    private static List<NoteSpan> Inline(string text)
    {
        var spans = new List<NoteSpan>();
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is var end and > 0)
            {
                AddPlain();
                spans.Add(new NoteSpan(text[(i + 1)..end], Code: true));
                i = end + 1;
            }
            else if (text[i] == '[' && LinkEnd(text, i) is var (label, after))
            {
                plain.Append(label);
                i = after;
            }
            else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
            }
            else
            {
                plain.Append(text[i]);
                i++;
            }
        }

        AddPlain();
        return spans;

        void AddPlain()
        {
            if (plain.Length > 0)
            {
                spans.Add(new NoteSpan(plain.ToString(), Code: false));
                plain.Clear();
            }
        }
    }

    /// <summary><c>[label](url)</c> at <paramref name="start"/>: the label and the index after the closing parenthesis.</summary>
    private static (string Label, int After)? LinkEnd(string text, int start)
    {
        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return null;
        }

        var paren = text.IndexOf(')', close + 2);
        return paren < 0 ? null : (text[(start + 1)..close], paren + 1);
    }
}
