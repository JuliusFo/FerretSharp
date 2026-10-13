using FerretSharp.Core.AppTrace;

namespace FerretSharp.Core.Tests.AppTrace;

/// <summary>WP-33: the environment of a traced app from its command line.</summary>
public sealed class AppEnvironmentTests
{
    [Theory]
    [InlineData(@"C:\Apps\Erp.Api.exe -environment=erp-dev", "erp-dev")]
    [InlineData(@"C:\Apps\Erp.Api.exe --environment=erp-dev", "erp-dev")]
    [InlineData(@"C:\Apps\Erp.Api.exe /environment=erp-dev", "erp-dev")]
    [InlineData(@"C:\Apps\Erp.Api.exe --environment erp-dev", "erp-dev")]
    [InlineData(@"C:\Apps\Erp.Api.exe -Environment=Erp-Dev", "Erp-Dev")]
    [InlineData(@"""C:\Program Files\dotnet\dotnet.exe"" ""C:\My Apps\Erp.Api.dll"" --port 5000 -environment=""erp test""", "erp test")]
    [InlineData("/usr/bin/dotnet /srv/erp/Erp.Api.dll --environment Production", "Production")]
    public void The_environment_is_found(string commandLine, string expected) =>
        Assert.Equal(expected, AppEnvironment.FromCommandLine(commandLine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Apps\Erp.Api.exe")]
    [InlineData(@"C:\Apps\Erp.Api.exe --environment")] // no value
    [InlineData(@"C:\Apps\Erp.Api.exe --environment --port 5000")] // the next one is a switch
    [InlineData(@"C:\Apps\Erp.Api.exe -environment=")]
    [InlineData(@"C:\Apps\Erp.Api.exe --environments=x")] // another argument
    [InlineData(@"C:\Apps\environment=x.exe")] // the program itself
    public void Without_the_argument_there_is_none(string? commandLine) =>
        Assert.Null(AppEnvironment.FromCommandLine(commandLine));

    [Fact]
    public void The_first_one_counts() =>
        Assert.Equal("a", AppEnvironment.FromCommandLine("app.exe -environment=a --environment b"));

    [Fact]
    public void Quotes_group_and_are_removed() =>
        Assert.Equal(["C:\\My Apps\\app.exe", "-x=a b", "", "z"], AppEnvironment.Split("\"C:\\My Apps\\app.exe\"  -x=\"a b\" \"\" z"));

    [Theory]
    [InlineData("Erp.Api", @"C:\Apps\Erp.Api.exe", "Erp.Api")]
    [InlineData("dotnet", @"""C:\Program Files\dotnet\dotnet.exe"" exec --depsfile x.deps.json C:\Apps\Erp.Worker.dll -environment=a", "Erp.Worker")]
    [InlineData("dotnet", "/usr/bin/dotnet /srv/erp/Erp.Api.dll", "Erp.Api")]
    [InlineData("dotnet", "dotnet watch run", "dotnet")]
    [InlineData("dotnet", null, "dotnet")]
    public void Apps_started_by_the_dotnet_host_are_named_after_their_dll(string process, string? commandLine, string expected) =>
        Assert.Equal(expected, TraceableProcess.NameOf(process, commandLine));

    [Fact]
    public void A_process_knows_its_environment() =>
        Assert.Equal("erp-dev", new TraceableProcess(1, "Erp.Api", "Erp.Api.exe -environment=erp-dev", null).Environment);
}
