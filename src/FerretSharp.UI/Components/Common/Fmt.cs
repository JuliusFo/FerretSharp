using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace FerretSharp.UI.Components;

/// <summary>
/// A localized text whose placeholders <c>{0}</c> … <c>{3}</c> are markup (WP-29, ADR 0017): the text stays one resource,
/// so a translation may put the placeholders in any order, and the markup stays in Razor.
/// <code>
/// &lt;Fmt Text="@ShellText.Settings_LockWaitHint"&gt;&lt;Arg0&gt;&lt;code&gt;SELECT … FOR UPDATE WAIT n&lt;/code&gt;&lt;/Arg0&gt;&lt;/Fmt&gt;
/// </code>
/// Texts without markup use <c>TextFormat.Format</c> instead. <c>{{</c> and <c>}}</c> are literal braces.
/// </summary>
public sealed partial class Fmt : ComponentBase
{
    [Parameter, EditorRequired]
    public string Text { get; set; } = "";

    [Parameter]
    public RenderFragment? Arg0 { get; set; }

    [Parameter]
    public RenderFragment? Arg1 { get; set; }

    [Parameter]
    public RenderFragment? Arg2 { get; set; }

    [Parameter]
    public RenderFragment? Arg3 { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        foreach (var (literal, index) in Split(Text))
        {
            if (index is { } i)
            {
                builder.AddContent(1, Arg(i));
            }
            else
            {
                builder.AddContent(0, literal);
            }
        }
    }

    /// <summary>The text as literal parts and placeholder indexes, in order.</summary>
    public static IEnumerable<(string? Literal, int? Index)> Split(string text)
    {
        var position = 0;
        var literal = new System.Text.StringBuilder();
        foreach (Match match in Token().Matches(text))
        {
            literal.Append(text, position, match.Index - position);
            position = match.Index + match.Length;
            if (match.Groups[1].Success)
            {
                if (literal.Length > 0)
                {
                    yield return (literal.ToString(), null);
                    literal.Clear();
                }

                yield return (null, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                literal.Append(match.Value[0]);
            }
        }

        literal.Append(text, position, text.Length - position);
        if (literal.Length > 0)
        {
            yield return (literal.ToString(), null);
        }
    }

    private RenderFragment? Arg(int index) => index switch
    {
        0 => Arg0,
        1 => Arg1,
        2 => Arg2,
        3 => Arg3,
        _ => throw new FormatException($"Fmt supports the placeholders {{0}} to {{3}}, not {{{index}}}."),
    };

    [GeneratedRegex(@"\{\{|\}\}|\{(\d+)\}")]
    private static partial Regex Token();
}
