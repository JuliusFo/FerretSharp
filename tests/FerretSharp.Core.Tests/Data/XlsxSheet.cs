using System.IO.Compression;
using System.Xml.Linq;

namespace FerretSharp.Core.Tests.Data;

/// <summary>A cell as written: its type (<c>n</c>, <c>b</c>, <c>s</c>/<c>inlineStr</c>/<c>str</c> for text), value and number format.</summary>
internal sealed record XlsxCell(string Type, string? Value, string? Format)
{
    public bool IsText => Type is "inlineStr" or "s" or "str";
}

/// <summary>Reads back the first sheet of an exported file: cells by A1 reference, frozen rows, auto filter, column widths.</summary>
internal sealed class XlsxSheet
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private XlsxSheet(Dictionary<string, XlsxCell> cells, XElement sheet, string name)
    {
        Cells = cells;
        Sheet = sheet;
        Name = name;
    }

    public Dictionary<string, XlsxCell> Cells { get; }

    public XElement Sheet { get; }

    public string Name { get; }

    public XlsxCell this[string reference] => Cells[reference];

    public string? AutoFilter => Sheet.Element(Main + "autoFilter")?.Attribute("ref")?.Value;

    public string? FrozenAt => Sheet.Descendants(Main + "pane").SingleOrDefault()?.Attribute("topLeftCell")?.Value;

    public IReadOnlyList<double> Widths =>
        [.. Sheet.Element(Main + "cols")?.Elements(Main + "col").Select(c => double.Parse(c.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture)) ?? []];

    public static XlsxSheet Read(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content));
        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).Root!;
        var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open()).Root!;
        var styles = XDocument.Load(zip.GetEntry("xl/styles.xml")!.Open()).Root!;
        var shared = zip.GetEntry("xl/sharedStrings.xml") is { } entry
            ? XDocument.Load(entry.Open()).Root!.Elements(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList()
            : [];
        var formats = styles.Element(Main + "numFmts")?.Elements(Main + "numFmt")
            .ToDictionary(f => f.Attribute("numFmtId")!.Value, f => f.Attribute("formatCode")!.Value) ?? [];
        var xfs = styles.Element(Main + "cellXfs")!.Elements(Main + "xf").Select(xf => xf.Attribute("numFmtId")?.Value ?? "0").ToList();

        var cells = new Dictionary<string, XlsxCell>();
        var rowNumber = 0;
        foreach (var row in sheet.Element(Main + "sheetData")!.Elements(Main + "row"))
        {
            rowNumber = row.Attribute("r") is { } r ? int.Parse(r.Value, System.Globalization.CultureInfo.InvariantCulture) : rowNumber + 1;
            var column = 0;
            foreach (var c in row.Elements(Main + "c"))
            {
                column = c.Attribute("r") is { } cr ? ColumnOf(cr.Value) : column + 1;
                var type = c.Attribute("t")?.Value ?? "n";
                var value = type switch
                {
                    "inlineStr" => string.Concat(c.Descendants(Main + "t").Select(t => t.Value)),
                    "s" => shared[int.Parse(c.Element(Main + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture)],
                    _ => c.Element(Main + "v")?.Value,
                };
                var style = c.Attribute("s") is { } s ? xfs[int.Parse(s.Value, System.Globalization.CultureInfo.InvariantCulture)] : "0";
                cells[Letters(column) + rowNumber] = new XlsxCell(type, value, formats.GetValueOrDefault(style) ?? (style == "0" ? null : style));
            }
        }

        var name = workbook.Descendants(Main + "sheet").First().Attribute("name")!.Value;
        return new XlsxSheet(cells, sheet, name);
    }

    private static int ColumnOf(string reference) =>
        reference.TakeWhile(char.IsAsciiLetter).Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);

    private static string Letters(int column) => column <= 26 ? ((char)('A' + column - 1)).ToString() : Letters((column - 1) / 26) + (char)('A' + (column - 1) % 26);
}
