using FerretSharp.Core.Compare;

namespace FerretSharp.Core.Tests.Compare;

public sealed class CompareExportTests
{
    private static CompareCell Same(string definition) => new(CellState.Same, 0, definition);

    private static CompareCell Different(string definition, int group) => new(CellState.Different, group, definition);

    private static readonly CompareCell Missing = new(CellState.Missing, -1, null);

    private static SchemaComparison Comparison(int? reference = null) => new([], new CompareOptions(reference),
    [
        new CompareRow("AUFTRAG", CompareKind.Table, "AUFTRAG", [Same("TABLE"), Same("TABLE"), Same("TABLE")],
        [
            new CompareRow("AUFTRAG/col/ID", CompareKind.Column, "ID", [Same("NUMBER(10) NOT NULL"), Same("NUMBER(10) NOT NULL"), Same("NUMBER(10) NOT NULL")], []),
        ]),
        new CompareRow("KUNDEN", CompareKind.Table, "KUNDEN", [Same("TABLE"), Same("TABLE"), Same("TABLE")],
        [
            new CompareRow("KUNDEN/col/EMAIL", CompareKind.Column, "EMAIL",
                [Different("VARCHAR2(200 CHAR) NULL", 0), Different("VARCHAR2(200 CHAR) NULL", 0), Different("VARCHAR2(100 CHAR) NULL", 1)], []),
            new CompareRow("KUNDEN/con/CK", CompareKind.Check, "CHECK (A | B)", [Same("CHECK (A | B)"), Missing, Same("CHECK (A | B)")], []),
        ]),
        new CompareRow("Notiz", CompareKind.Table, "Notiz", [Same("TABLE"), new CompareCell(CellState.OtherCase, 0, "TABLE") { Name = "NOTIZ" }, Missing], []),
    ]);

    [Fact]
    public void Only_differences_with_header_bold_differences_and_dashes()
    {
        var text = CompareExport.Markdown(Comparison(), ["Dev", "Test", "Prod"], onlyDifferences: true);

        Assert.Equal(
            """
            | Objekt | Dev | Test | Prod |
            |---|---|---|---|
            | **KUNDEN** | TABLE | TABLE | TABLE |
            | KUNDEN · EMAIL | **VARCHAR2(200 CHAR) NULL** | **VARCHAR2(200 CHAR) NULL** | **VARCHAR2(100 CHAR) NULL** |
            | KUNDEN · CHECK (A \| B) | CHECK (A \| B) | – | CHECK (A \| B) |
            | **Notiz** | TABLE | TABLE (als „NOTIZ“) | – |

            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Everything_with_the_reference_marked()
    {
        var text = CompareExport.Markdown(Comparison(reference: 0), ["Dev", "Test", "Prod"], onlyDifferences: false);

        Assert.Contains("| **AUFTRAG** | TABLE (Referenz) | TABLE | TABLE |", text);
        Assert.Contains("| AUFTRAG · ID | NUMBER(10) NOT NULL (Referenz) |", text);
    }

    [Fact]
    public void Kinds_filter_keeps_objects_with_a_matching_child()
    {
        var text = CompareExport.Markdown(Comparison(), ["Dev", "Test", "Prod"], onlyDifferences: true, kinds: [CompareKind.Check]);

        Assert.Contains("KUNDEN · CHECK", text);
        Assert.DoesNotContain("EMAIL", text);
        Assert.DoesNotContain("Notiz", text);
    }
}
