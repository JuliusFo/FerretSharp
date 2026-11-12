using System.Text;
using FerretSharp.Core.ClrModel;
using FerretSharp.Testing;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>Writing a plan into the work clone: whole or not at all, never outside it, never over a changed file (WP-41).</summary>
public sealed class CodeChangeWriterTests
{
    private static FileChange Changed(TestFolder clone, string name, string newText)
    {
        var path = clone.Combine(name);
        var file = CodeFile.Read(path)!;
        return new FileChange(path, name, file, newText, file.Format);
    }

    private static FileChange Created(TestFolder clone, string name, string text) =>
        new(clone.Combine(name), name, null, text, new CodeFileFormat(CodeFileBom.None, "\n", "    "));

    [Fact]
    public async Task A_changed_file_keeps_its_byte_order_mark_and_a_new_one_is_created()
    {
        using var clone = new TestFolder();
        File.WriteAllBytes(clone.Combine("Kunde.cs"), [0xEF, 0xBB, 0xBF, .. "class Kunde {}\r\n"u8]);
        var plan = new CodeChangePlan(clone.Path, [Changed(clone, "Kunde.cs", "class Kunde { int Größe; }\r\n"), Created(clone, "Neu.cs", "class Neu {}\n")], [], []);

        var results = await CodeChangeWriter.WriteAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal([FileWriteOutcome.Written, FileWriteOutcome.Created], results.Select(r => r.Outcome));
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class Kunde { int Größe; }\r\n")], File.ReadAllBytes(clone.Combine("Kunde.cs")));
        Assert.Equal("class Neu {}\n", File.ReadAllText(clone.Combine("Neu.cs")));
        Assert.Equal(2, Directory.GetFiles(clone.Path).Length); // no temporary file left
    }

    [Fact]
    public async Task A_file_changed_since_the_analysis_or_a_new_one_that_came_up_keeps_every_file_as_it_is()
    {
        using var clone = new TestFolder();
        File.WriteAllText(clone.Combine("A.cs"), "class A {}");
        File.WriteAllText(clone.Combine("B.cs"), "class B {}");
        var plan = new CodeChangePlan(clone.Path, [Changed(clone, "A.cs", "class A { }"), Changed(clone, "B.cs", "class B { }"), Created(clone, "C.cs", "class C {}")], [], []);
        File.WriteAllText(clone.Combine("B.cs"), "class B { int typed; }"); // the user typed meanwhile
        File.WriteAllText(clone.Combine("C.cs"), "// mine");

        var results = await CodeChangeWriter.WriteAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal([FileWriteOutcome.Skipped, FileWriteOutcome.Changed, FileWriteOutcome.Changed], results.Select(r => r.Outcome));
        Assert.Equal("class A {}", File.ReadAllText(clone.Combine("A.cs")));
        Assert.Equal("class B { int typed; }", File.ReadAllText(clone.Combine("B.cs")));
        Assert.Equal("// mine", File.ReadAllText(clone.Combine("C.cs")));
    }

    /// <summary>Review WP-41, K9: a read-only file showed only when it was its turn – the files before it were written.</summary>
    [Fact]
    public async Task A_read_only_file_keeps_every_file_of_the_plan_as_it_is()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Under Linux root writes read-only files (the test container).");
        }

        using var clone = new TestFolder();
        File.WriteAllText(clone.Combine("A.cs"), "class A {}");
        File.WriteAllText(clone.Combine("B.cs"), "class B {}");
        var plan = new CodeChangePlan(clone.Path, [Changed(clone, "A.cs", "class A { }"), Changed(clone, "B.cs", "class B { }")], [], []);
        File.SetAttributes(clone.Combine("B.cs"), FileAttributes.ReadOnly);
        try
        {
            var results = await CodeChangeWriter.WriteAsync(plan, TestContext.Current.CancellationToken);

            Assert.Equal([FileWriteOutcome.Skipped, FileWriteOutcome.Failed], results.Select(r => r.Outcome));
            Assert.Equal("class A {}", File.ReadAllText(clone.Combine("A.cs")));
        }
        finally
        {
            File.SetAttributes(clone.Combine("B.cs"), FileAttributes.Normal);
        }
    }

    /// <summary>Second review of WP-41, N3: open in a program without delete sharing – the move over it failed halfway.</summary>
    [Fact]
    public async Task A_file_held_open_without_delete_sharing_keeps_every_file_of_the_plan_as_it_is()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Sharing is only advisory under Linux.");
        }

        using var clone = new TestFolder();
        File.WriteAllText(clone.Combine("A.cs"), "class A {}");
        File.WriteAllText(clone.Combine("B.cs"), "class B {}");
        var plan = new CodeChangePlan(clone.Path, [Changed(clone, "A.cs", "class A { }"), Changed(clone, "B.cs", "class B { }")], [], []);

        IReadOnlyList<FileWriteResult> results;
        using (new FileStream(clone.Combine("B.cs"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            results = await CodeChangeWriter.WriteAsync(plan, TestContext.Current.CancellationToken);
        }

        Assert.Equal([FileWriteOutcome.Skipped, FileWriteOutcome.Failed], results.Select(r => r.Outcome));
        Assert.Equal("class A {}", File.ReadAllText(clone.Combine("A.cs")));
    }

    /// <summary>Review WP-41, M2: a junction in the work clone is neither read nor written through; a junction needs no rights.</summary>
    [Fact]
    public async Task A_junction_in_the_work_clone_is_not_scanned_and_not_written_through()
    {
        using var folder = new TestFolder();
        var outside = folder.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Kunde.cs"), "class Kunde {}");
        var clone = folder.Combine("clone");
        Directory.CreateDirectory(clone);
        TestFolder.Junction(Path.Combine(clone, "joined"), outside);

        var scan = CodeScan.Read(clone, ["Kunde"], TestContext.Current.CancellationToken);
        var plan = new CodeChangePlan(clone, [new FileChange(Path.Combine(clone, "joined", "Neu.cs"), Path.Combine("joined", "Neu.cs"), null, "class Neu {}", CodeFileFormat.Default)], [], []);
        var results = await CodeChangeWriter.WriteAsync(plan, TestContext.Current.CancellationToken);

        Assert.Empty(scan.Files);
        Assert.Equal(FileWriteOutcome.Failed, Assert.Single(results).Outcome);
        Assert.False(File.Exists(Path.Combine(outside, "Neu.cs")));
    }

    /// <summary>Review WP-41, M2: a drive (S:\) keeps its separator – every path below it was "outside".</summary>
    [Fact]
    public void A_drive_as_work_clone_has_its_files_below_it()
    {
        var drive = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.True(CodeChangeWriter.Below(drive, Path.Combine(drive, "no-such-folder-wp41", "A.cs")));
        Assert.False(CodeChangeWriter.Below(drive, drive));
    }

    [Theory]
    [InlineData("..")] // beside the work clone
    [InlineData("")] // the work clone itself
    public async Task Nothing_is_written_outside_the_work_clone(string up)
    {
        using var outer = new TestFolder();
        var clone = Path.Combine(outer.Path, "clone");
        Directory.CreateDirectory(clone);
        var target = Path.GetFullPath(Path.Combine(clone, up, "Evil.cs"));
        var plan = new CodeChangePlan(up.Length == 0 ? Path.Combine(clone, "Evil.cs") : clone,
            [new FileChange(target, "Evil.cs", null, "class Evil {}", CodeFileFormat.Default)], [], []);

        var results = await CodeChangeWriter.WriteAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteOutcome.Failed, Assert.Single(results).Outcome);
        Assert.False(File.Exists(target));
    }
}
