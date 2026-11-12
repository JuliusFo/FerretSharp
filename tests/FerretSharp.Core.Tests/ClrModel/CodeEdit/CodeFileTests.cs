using System.Text;
using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>How a source file of the user is read and written back (WP-41): its encoding, line ending and indentation stay.</summary>
public sealed class CodeFileTests
{
    [Theory]
    // indentation in steps of four with a "*" line of a block comment (one space)
    [InlineData("class A\r\n{\r\n    /*\r\n     * x\r\n     */\r\n    int B;\r\n}\r\n", CodeFileBom.None, "\r\n", "    ")]
    [InlineData("class A\n{\n  int B;\n  void C()\n  {\n    D();\n  }\n}\n", CodeFileBom.None, "\n", "  ")]
    [InlineData("﻿class A\n{\n\tint B;\n}\n", CodeFileBom.Utf8, "\n", "\t")]
    public void The_format_is_learned_from_the_file(string text, CodeFileBom bom, string newLine, string indent)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        var file = CodeFile.FromBytes("A.cs", bytes)!;

        Assert.Equal(new CodeFileFormat(bom, newLine, indent), file.Format);
        Assert.Equal(bytes, file.Format.Encode(file.Text)); // written back byte for byte
    }

    [Fact]
    public void A_file_that_is_not_utf8_is_not_read_written_back_it_would_change_every_umlaut()
    {
        var ansi = new byte[] { (byte)'/', (byte)'/', (byte)' ', 0xE4 }; // "// ä" in Windows-1252

        Assert.Null(CodeFile.FromBytes("A.cs", ansi));
    }

    [Fact]
    public void Generated_text_takes_the_files_indentation_and_line_ending()
    {
        var format = new CodeFileFormat(CodeFileBom.None, "\r\n", "\t");

        Assert.Equal("class A\r\n{\r\n\tint B;\r\n\t\tint  C;\r\n}", format.Apply("class A\n{\n    int B;\n        int  C;\n}"));
    }
}
