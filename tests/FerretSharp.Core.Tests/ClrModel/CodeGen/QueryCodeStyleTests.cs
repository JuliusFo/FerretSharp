using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.ClrModel;

public class QueryCodeStyleTests
{
    [Fact]
    public void The_style_is_kept_per_project_and_a_template_without_a_line_is_kept_but_not_used()
    {
        var style = new QueryCodeStyle("Shop.Queries", WithDto: false, DtoAsRecord: false, LinqKit: false,
            new QueryLineTemplate("new Builder<{Entity}, {Dto}>()", ".Bind({Expression}, d => d.{Property})", ".Build();"));
        var headOnly = QueryCodeStyle.Default with { Template = new QueryLineTemplate("head", "", "") };

        var map = style.Into(SettingsMap.Empty, @"C:\a\A.csproj");
        map = headOnly.Into(map, @"C:\b\B.csproj");

        Assert.Equal(style, QueryCodeStyle.Of(map, @"C:\a\A.csproj"));
        // Review: a head typed before the line was lost; it stays, the code takes the projection until a line comes.
        Assert.Equal(headOnly, QueryCodeStyle.Of(map, @"C:\b\B.csproj"));
        Assert.Null(headOnly.Options(QueryCodeOptions.For("Auftrag")).Template);
        Assert.Equal(QueryCodeStyle.Default, QueryCodeStyle.Of(map, @"C:\c\C.csproj"));
        Assert.Equal(style, QueryCodeStyle.Of(QueryCodeStyle.Default.Into(map, @"C:\b\B.csproj"), @"C:\a\A.csproj")); // others stay
        Assert.Equal(QueryCodeStyle.Default, QueryCodeStyle.Of(QueryCodeStyle.Default.Into(map, @"C:\b\B.csproj"), @"C:\b\B.csproj"));
    }
}
