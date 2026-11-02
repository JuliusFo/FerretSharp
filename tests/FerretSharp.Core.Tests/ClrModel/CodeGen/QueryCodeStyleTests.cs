using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.ClrModel;

public class QueryCodeStyleTests
{
    [Fact]
    public void The_style_is_kept_per_project_and_a_template_without_a_line_is_none()
    {
        var style = new QueryCodeStyle("Shop.Queries", WithDto: false, DtoAsRecord: false, LinqKit: false,
            new QueryLineTemplate("new Builder<{Entity}, {Dto}>()", ".Bind({Expression}, d => d.{Property})", ".Build();"));

        var map = style.Into(SettingsMap.Empty, @"C:\a\A.csproj");
        map = (QueryCodeStyle.Default with { Template = new QueryLineTemplate("head", "", "foot") }).Into(map, @"C:\b\B.csproj");

        Assert.Equal(style, QueryCodeStyle.Of(map, @"C:\a\A.csproj"));
        Assert.Equal(QueryCodeStyle.Default, QueryCodeStyle.Of(map, @"C:\b\B.csproj"));
        Assert.Equal(QueryCodeStyle.Default, QueryCodeStyle.Of(map, @"C:\c\C.csproj"));
        Assert.Equal(style, QueryCodeStyle.Of(QueryCodeStyle.Default.Into(map, @"C:\b\B.csproj"), @"C:\a\A.csproj")); // others stay
    }
}
