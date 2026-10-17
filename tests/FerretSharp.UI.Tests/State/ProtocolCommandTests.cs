using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>R6b (review 3.40.0, C11): the registration's command line – which program links start, and whether it is ours.</summary>
public class ProtocolCommandTests
{
    private static readonly string Program = Path.Combine(Path.GetTempPath(), "FerretSharp", "current", "FerretSharp.exe");

    [Fact]
    public void The_command_written_reads_back_as_its_program()
    {
        Assert.Equal($"\"{Program}\" \"%1\"", ProtocolCommand.For(Program));
        Assert.Equal(Program, ProtocolCommand.ProgramOf(ProtocolCommand.For(Program)));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Ferret Sharp\FerretSharp.exe"" ""%1""", @"C:\Program Files\Ferret Sharp\FerretSharp.exe")]
    [InlineData(@"  ""C:\tools\FerretSharp.exe""  ", @"C:\tools\FerretSharp.exe")]
    [InlineData(@"C:\tools\FerretSharp.exe ""%1""", @"C:\tools\FerretSharp.exe")]
    [InlineData(@"C:\tools\FerretSharp.exe", @"C:\tools\FerretSharp.exe")]
    [InlineData(@"""""", null)]
    [InlineData(@"""C:\unterminated", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void The_program_is_the_quoted_or_first_part_of_the_command(string? command, string? program) =>
        Assert.Equal(program, ProtocolCommand.ProgramOf(command));

    [Fact]
    public void Only_this_program_counts_as_ours_whatever_its_letter_case_or_path_form()
    {
        Assert.True(ProtocolCommand.SameProgram(Program.ToUpperInvariant(), Program));
        Assert.True(ProtocolCommand.SameProgram(Path.Combine(Path.GetDirectoryName(Program)!, "..", "current", "FerretSharp.exe"), Program));
        Assert.False(ProtocolCommand.SameProgram(Path.Combine(Path.GetTempPath(), "portable", "FerretSharp.exe"), Program));
        Assert.False(ProtocolCommand.SameProgram(null, Program));
    }
}
