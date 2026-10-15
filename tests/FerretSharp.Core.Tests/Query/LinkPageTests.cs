using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Links;
using FerretSharp.Core.Query;
using FerretSharp.Core.Workspaces;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.Query;

/// <summary>
/// The redirect page for links on GitHub Pages (ADR 0026, addendum): it must take every link FerretSharp writes, open
/// exactly <see cref="TableLink.UriStart"/> plus the part after <c>#</c>, and keep its strict CSP in step with its code.
/// </summary>
public sealed class LinkPageTests
{
    private static string Page(string path) => File.ReadAllText(Path.Combine(Repository.Root, "pages", path));

    private static string Hash(string text) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The check the page runs on the part after <c>#</c>, taken from the page itself (JS and .NET read it alike).</summary>
    private static Regex PageCheck()
    {
        var literal = Regex.Match(Page("open/index.html"), @"&& /(?<pattern>\^v=.*?\$)/\.test\(parameters\)").Groups["pattern"].Value;
        Assert.NotEmpty(literal);
        return new Regex(literal, RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_page_is_where_links_point_and_opens_the_same_link()
    {
        var page = Page("open/index.html");

        Assert.EndsWith("/open/", TableLink.WebPage, StringComparison.Ordinal); // pages/open/index.html
        Assert.Contains($"const link = \"{TableLink.UriStart}\" + parameters;", page, StringComparison.Ordinal);
        Assert.Contains($"parameters.length <= {TableLink.MaxLength}", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("open/index.html")]
    [InlineData("index.html")]
    public void The_strict_policy_names_the_pages_own_script_and_style(string path)
    {
        var page = Page(path);
        var policy = Regex.Match(page, "Content-Security-Policy\" content=\"(?<policy>[^\"]+)\"").Groups["policy"].Value;

        Assert.StartsWith("default-src 'none';", policy, StringComparison.Ordinal);
        foreach (Match block in Regex.Matches(page, "<(?<tag>script|style)>(?<code>.*?)</\\k<tag>>", RegexOptions.Singleline))
        {
            Assert.Contains($"{block.Groups["tag"].Value}-src '{Hash(block.Groups["code"].Value)}'", policy, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch("<(script|link|img|iframe)[^>]+(src|href)=", page); // nothing loaded from anywhere
    }

    [Fact]
    public void The_page_takes_every_link_FerretSharp_writes_and_nothing_else()
    {
        var check = PageCheck();
        var profile = new ConnectionProfile(Guid.NewGuid(), "ERP Test (lesend) ä", ConnectionKind.Test,
            new HostPortAddress("db01.firma.local", 1521, "ERP_TEST", null), "reader", null, true);
        var link = TableLink.For(profile, new("ERP", "AUFTRAG"), [
            FilterCondition.Of("NOTIZ", FilterOperator.Contains, "100% #1 & a,b \"x\" <y> 'z' ü€😀"),
            FilterCondition.Of("STATUS", FilterOperator.In, "OFFEN", "NEU"),
            FilterCondition.Of("ERSTELLT", FilterOperator.Between, "1.10.2026", "31.10.2026"),
            FilterCondition.Of("ERLEDIGT", FilterOperator.IsNull),
        ], [new SortSpec("ERSTELLT", true)], TabMode.Constraints);
        var sid = TableLink.For(profile with { Address = new HostPortAddress("db01", 1521, null, "ERP") }, new("ERP", "AUFTRAG"), [], [], TabMode.Data);

        Assert.Matches(check, new Uri(link.ToWebUri()).Fragment[1..]);
        Assert.Matches(check, new Uri(sid.ToWebUri()).Fragment[1..]); // a descriptor with parentheses and =, all escaped
        Assert.DoesNotMatch(check, "v=1&s=ERP&t=A\"><script>");
        Assert.DoesNotMatch(check, "v=1&s=ERP&t=A#more");
        Assert.DoesNotMatch(check, "javascript:alert(1)");
        Assert.DoesNotMatch(check, "");
    }
}
