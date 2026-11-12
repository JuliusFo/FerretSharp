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
