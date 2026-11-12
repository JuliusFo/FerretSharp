using System.Text;
using FerretSharp.Core.ClrModel;
using FerretSharp.Testing;
using Microsoft.CodeAnalysis.Text;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// The guards against writing broken code into the work clone (WP-41; review 3.70.0, C1): without them a wrong span writes
/// invalid C# into the user's project.
/// </summary>
public sealed class CodeChangeGuardTests : IDisposable
{
    private readonly TestFolder _clone = new();

    public void Dispose() => _clone.Dispose();

    private ScannedFile Scanned(string text)
    {
        File.WriteAllText(_clone.Combine("Kunde.cs"), text);
        return Assert.Single(CodeScan.Read(_clone.Path, ["Kunde"], TestContext.Current.CancellationToken).Files);
    }

    [Fact]
    public void Overlapping_changes_of_a_file_are_a_problem_and_the_file_is_not_written()
    {
        var file = Scanned("class Kunde { int Id; }");
        var edits = new FileEdits(_clone.Path);
        edits.Replace(file, new TextSpan(14, 6), "long Id;");
        edits.Replace(file, new TextSpan(18, 4), "Nr;");

        var (files, problems) = edits.Build();

        Assert.Empty(files);
        Assert.Contains("Kunde.cs", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_that_leaves_more_syntax_errors_than_before_is_a_problem()
    {
        var file = Scanned("class Kunde { int Id; }");
        var edits = new FileEdits(_clone.Path);
        edits.Insert(file, 14, "int (");

        var (files, problems) = edits.Build();

        Assert.Empty(files);
        Assert.Single(problems);
    }

    [Fact]
    public void A_new_file_with_syntax_errors_is_a_problem()
    {
        var edits = new FileEdits(_clone.Path);
        edits.Create(new FileChange(_clone.Combine("Neu.cs"), "Neu.cs", null, "class Neu {", CodeFileFormat.Default));

        Assert.Single(edits.Build().Problems);
    }

    [Fact]
    public async Task A_plan_with_problems_is_never_written()
    {
        var plan = new CodeChangePlan(_clone.Path, [new FileChange(_clone.Combine("Neu.cs"), "Neu.cs", null, "class Neu {}", CodeFileFormat.Default)], [], ["kaputt"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CodeChangeWriter.WriteAsync(plan, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(_clone.Combine("Neu.cs")));
    }

    [Fact]
    public async Task A_file_that_is_itself_a_link_is_not_written_through()
    {
        var outside = _clone.Combine("..", Path.GetFileName(_clone.Path) + "-outside.cs");
        File.WriteAllText(outside, "class Kunde {}");
        try
        {
            try
            {
                File.CreateSymbolicLink(_clone.Combine("Kunde.cs"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("Creating a symbolic link needs developer mode or admin rights on Windows.");
            }

            var file = CodeFile.Read(_clone.Combine("Kunde.cs"))!;
            var plan = new CodeChangePlan(_clone.Path, [new FileChange(_clone.Combine("Kunde.cs"), "Kunde.cs", file, "class Kunde { int Id; }", file.Format)], [], []);

            var result = Assert.Single(await CodeChangeWriter.WriteAsync(plan, TestContext.Current.CancellationToken));

            Assert.Equal(FileWriteOutcome.Failed, result.Outcome);
            Assert.Equal("class Kunde {}", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void A_utf16_file_is_written_back_byte_for_byte_and_binary_or_too_large_files_are_not_read()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("class Kunde\r\n{\r\n}\r\n")).ToArray();
        var file = CodeFile.FromBytes("Kunde.cs", utf16)!;
        Assert.Equal(CodeFileBom.Utf16LittleEndian, file.Format.Bom);
        Assert.Equal(utf16, file.Format.Encode(file.Text));

        Assert.Null(CodeFile.FromBytes("Bild.cs", [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0]));

        var large = _clone.Combine("Gross.cs");
        using (var stream = File.Create(large))
        {
            stream.SetLength(CodeFile.MaxBytes + 1);
        }

        Assert.Null(CodeFile.Read(large));
    }
}
