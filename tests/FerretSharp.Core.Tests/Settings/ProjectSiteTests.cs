using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Settings;

/// <summary>R6b (review 3.40.0, B11): the project's addresses from one owner and repository.</summary>
public class ProjectSiteTests
{
    [Fact]
    public void Every_address_names_the_same_owner_and_repository()
    {
        Assert.Equal("https://github.com/JuliusFo/FerretSharp", ProjectSite.Url);
        Assert.Equal("https://github.com/JuliusFo/FerretSharp/releases", ProjectSite.Releases);
        Assert.Equal("/JuliusFo/FerretSharp/", ProjectSite.RawPath);
        Assert.Equal(ProjectSite.Pages, $"https://{ProjectSite.Owner.ToLowerInvariant()}.github.io/{ProjectSite.Repository}/");
    }
}
