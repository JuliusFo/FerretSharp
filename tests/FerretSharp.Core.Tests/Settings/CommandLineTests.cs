using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Tests.Settings;

/// <summary>R5b (review 3.31.0, B12): the options of the command line, read one way.</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void The_first_option_counts_and_its_name_ignores_case()
    {
        string[] args = ["--DATA-DIR=daten", "--lang=de", "--lang=en"];

        Assert.Equal("daten", CommandLine.ValueOf(args, CommandLine.DataDir));
        Assert.Equal(UiLanguage.German, UiLanguages.FromArguments(args)); // R5b: the last one counted for the language only
        Assert.Null(CommandLine.ValueOf(args, CommandLine.Theme));
        Assert.Equal("", CommandLine.ValueOf(["--theme="], CommandLine.Theme));
    }

    [Fact]
    public void A_restart_takes_the_paths_as_full_paths_and_waits_for_the_old_instance()
    {
        string[] args = ["--data-dir=daten", "--lang=de", "--wait-for=17"];

        var again = CommandLine.Restart(args, waitFor: 4711);

        Assert.Equal([CommandLine.DataDir + Path.GetFullPath("daten"), "--lang=de", "--wait-for=4711"], again);
        Assert.Equal([CommandLine.DataDir + Path.GetFullPath("daten"), "--lang=de"], CommandLine.Restart(args));
    }

    [Fact]
    public void The_path_options_are_the_ones_an_update_makes_absolute() =>
        Assert.Equal([CommandLine.DataDir, CommandLine.ImportAppData, CommandLine.UpdateSource], CommandLine.PathOptions);
}
