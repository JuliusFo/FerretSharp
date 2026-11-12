using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Design;
using static FerretSharp.Core.Tests.ClrModel.CodeGenerationModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// The attributes of a property when its column changes (WP-41; review 3.70.0, C2: these paths had no test – the fix of M4,
/// [Required] only when NULL changes, held only half).
/// </summary>
public sealed class EntityCodeChangeAttributeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "work-clone");

    private static string Entity(string name, string umsatz) => $$"""
        namespace Shop;

        public class Kunde
        {
            public int KundeId { get; set; }
        {{name}}
            public DateTime ErstelltAm { get; set; }
        {{umsatz}}
            public bool Gesperrt { get; set; }
            public KundeStatus? Status { get; set; }
            public string? Notiz { get; set; }
        }
        """.ReplaceLineEndings("\n");

    private static async Task<string> ChangedAsync(string name, string umsatz, Func<ColumnDraft, ColumnDraft> change)
    {
        var mapping = await MappingAsync();
        var draft = TableDraft.Of(Kunden);
        draft = draft with { Columns = [.. draft.Columns.Select(change)] };
        var scan = CodeScan.Of(Root, [(Path.Combine("Entities", "Kunde.cs"), Entity(name, umsatz))]);

        var plan = EntityCodeChanges.Plan(mapping, Kunden.Table.Ref, ColumnChanges.Of(Kunden, draft), scan);

        Assert.True(plan.CanWrite, string.Join("\n", plan.Problems));
        return Assert.Single(plan.Files).NewText;
    }

    private const string Umsatz = "    public decimal? Umsatz { get; set; }";

    /// <summary>NAME is NOT NULL and becomes NULL: [Required] goes – alone on its line, first or last in a list.</summary>
    [Theory]
    [InlineData("    [Required]\n    [MaxLength(100)]\n    public string Name { get; set; } = \"\";", "    [MaxLength(100)]\n    public string? Name { get; set; } = \"\";")]
    [InlineData("    [Required, MaxLength(100)]\n    public string Name { get; set; } = \"\";", "    [MaxLength(100)]\n    public string? Name { get; set; } = \"\";")]
    [InlineData("    [MaxLength(100), Required]\n    public string Name { get; set; } = \"\";", "    [MaxLength(100)]\n    public string? Name { get; set; } = \"\";")]
    [InlineData("    [Required] public string Name { get; set; } = \"\";", "    public string? Name { get; set; } = \"\";")]
    public async Task Required_goes_when_the_column_becomes_nullable(string before, string after)
    {
        var text = await ChangedAsync(before, Umsatz, c => c.Original == "NAME" ? c with { Column = c.Column with { Nullable = true } } : c);

        Assert.Equal(Entity(after, Umsatz), text);
    }

    [Fact]
    public async Task A_nullable_column_that_becomes_not_null_loses_the_question_mark()
    {
        const string name = "    public string Name { get; set; } = \"\";";

        var text = await ChangedAsync(name, Umsatz, c => c.Original == "UMSATZ" ? c with { Column = c.Column with { Nullable = false } } : c);

        Assert.Equal(Entity(name, "    public decimal Umsatz { get; set; }"), text);
    }

    [Theory]
    [InlineData("    [Precision(12, 2)]", "    [Precision(14, 3)]")]
    [InlineData("    [Column(TypeName = \"NUMBER(12,2)\")]", "    [Column(TypeName = \"NUMBER(14,3)\")]")]
    public async Task The_precision_of_an_attribute_follows_the_column(string before, string after)
    {
        const string name = "    public string Name { get; set; } = \"\";";

        var text = await ChangedAsync(name, before + "\n" + Umsatz, c => c.Original == "UMSATZ" ? c with { Column = c.Column with { Precision = 14, Scale = 3 } } : c);

        Assert.Equal(Entity(name, after + "\n" + Umsatz), text);
    }
}
