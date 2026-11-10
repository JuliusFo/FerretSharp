using System.Text.RegularExpressions;
using FerretSharp.Testing;

namespace FerretSharp.UI.Tests.Components.Common;

/// <summary>
/// The general rules of every dialog (<c>.modal</c>, <c>.dialog</c>, <c>.dialog header</c>, <c>.dialog footer</c> …)
/// stand far down in ferretsharp.css. A rule for one dialog's class with the same specificity that stands before them
/// loses silently: <c>.sql-dialog</c> was never 900 px wide, its header never <c>flex-start</c> (review
/// small-compare-ddl-dialog-header). Limit: properties are compared by name, a longhand before its shorthand
/// (<c>padding-top</c> before <c>padding</c>) is not found.
/// </summary>
public sealed partial class DialogStyleTests
{
    [Fact]
    public void No_rule_for_a_dialog_s_class_is_overridden_by_the_later_general_dialog_rules()
    {
        var css = Comment().Replace(File.ReadAllText(Path.Combine(Repository.Root, "src", "FerretSharp.UI", "wwwroot", "css", "ferretsharp.css")), "");
        var rules = Rule().Matches(css)
            .Select(m => (Position: m.Index,
                          Selectors: m.Groups[1].Value.Split(',').Select(s => Spaces().Replace(s.Trim(), " ")).ToList(),
                          Properties: Properties(m.Groups[2].Value)))
            .ToList();

        // Every selector of the general rules, with the class it starts with: ".dialog footer" → (".dialog", " footer").
        var general = rules.SelectMany(r => r.Selectors)
            .Select(s => GeneralSelector().Match(s))
            .Where(m => m.Success)
            .Select(m => (Selector: m.Value, Descendants: m.Groups[2].Value))
            .Distinct()
            .ToList();
        Assert.Contains(general, g => g.Selector == ".dialog");
        Assert.Contains(general, g => g.Selector == ".dialog header");

        var problems = new List<string>();
        foreach (var dialogClass in DialogClasses())
        {
            foreach (var (selector, descendants) in general)
            {
                var own = "." + dialogClass + descendants;
                var generalRules = rules.Where(r => r.Selectors.Contains(selector)).ToList();
                foreach (var rule in rules.Where(r => r.Selectors.Contains(own)))
                {
                    problems.AddRange(generalRules
                        .Where(g => g.Position > rule.Position)
                        .SelectMany(g => rule.Properties.Intersect(g.Properties))
                        .Distinct()
                        .Select(property => $"{own} {{ {property} }} loses to the later {selector}"));
                }
            }
        }

        Assert.Empty(problems);
    }

    /// <summary>The classes the dialogs give their <c>ModalFrame</c>.</summary>
    private static IEnumerable<string> DialogClasses()
    {
        var classes = Directory.EnumerateFiles(Path.Combine(Repository.Root, "src", "FerretSharp.UI"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => FrameClass().Matches(File.ReadAllText(file)))
            .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct()
            .ToList();
        Assert.Contains("sql-dialog", classes);
        return classes;
    }

    private static List<string> Properties(string block) =>
        block.Split(';').Select(d => d.Split(':')[0].Trim()).Where(p => p.Length > 0).ToList();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>A rule without nested braces; the rules inside an @media block match on their own.</summary>
    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex Rule();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>A selector of the general dialog rules: <c>.modal</c> or <c>.dialog</c> alone or with descendants (not <c>.dialog.x</c>, <c>.dialog-body</c>).</summary>
    [GeneratedRegex(@"^(\.dialog|\.modal)((?: .+)?)$")]
    private static partial Regex GeneralSelector();

    [GeneratedRegex(@"<ModalFrame\b[^>]*?\bClass=""([^""@]*)""")]
    private static partial Regex FrameClass();
}
