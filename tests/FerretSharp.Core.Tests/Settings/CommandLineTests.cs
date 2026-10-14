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

    /// <summary>WP-37: a link starts FerretSharp as an argument of its own; a restart (update, graphics) must not open it again.</summary>
    [Fact]
    public void A_link_is_found_among_the_arguments_and_left_out_of_a_restart()
    {
        string[] args = ["--data-dir=daten", "ferretsharp://open?v=1&s=ERP&t=AUFTRAG"];

        Assert.Equal("ferretsharp://open?v=1&s=ERP&t=AUFTRAG", CommandLine.LinkOf(args));
        Assert.Null(CommandLine.LinkOf(["--lang=de"]));
        Assert.Equal([CommandLine.DataDir + Path.GetFullPath("daten")], CommandLine.Restart(args));
    }

    [Fact]
    public void The_path_options_are_the_ones_an_update_makes_absolute() =>
        Assert.Equal([CommandLine.DataDir, CommandLine.ImportAppData, CommandLine.UpdateSource], CommandLine.PathOptions);
}
