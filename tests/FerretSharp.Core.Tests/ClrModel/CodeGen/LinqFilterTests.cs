using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using NSubstitute;

namespace FerretSharp.Core.Tests.ClrModel;

public sealed class LinqFilterTests
{
    private static async Task<string> WhereAsync(params FilterCondition[] filters) =>
        LinqFilter.Build(await CodeGenerationModel.PresentationAsync(), filters).Text;

    [Fact]
    public async Task Filters_become_a_where_over_properties_with_the_grids_semantics()
    {
        var code = await WhereAsync(
            FilterCondition.Of("NAME", FilterOperator.Contains, "Meier"),
            FilterCondition.Of("ERSTELLT_AM", FilterOperator.Equals, "01.10.2026"),
            FilterCondition.Of("KUNDENART", FilterOperator.Equals, "2"),
            FilterCondition.Of("GESPERRT", FilterOperator.Equals, "N"));

        Assert.Equal(
            ".Where(x => x.Name.ToUpper().Contains(\"MEIER\")\r\n" +
            "         && x.ErstelltAm >= new DateTime(2026, 10, 1) && x.ErstelltAm < new DateTime(2026, 10, 2)\r\n" +
            "         && x.Kundenart == Kundenart.Gewerbe\r\n" +
            "         && !x.Gesperrt)",
            code);
    }

    [Theory]
    [InlineData("KUNDE_ID", FilterOperator.NotEquals, new[] { "4711" }, "x.KundeId != 4711")]
    [InlineData("KUNDE_ID", FilterOperator.Between, new[] { "1", "1000" }, "x.KundeId >= 1 && x.KundeId <= 1000")]
    [InlineData("UMSATZ", FilterOperator.Gt, new[] { "1.234,5" }, "x.Umsatz > 1234.5m")]
    [InlineData("UMSATZ", FilterOperator.IsNull, new string[0], "x.Umsatz == null")]
    [InlineData("NAME", FilterOperator.Equals, new[] { "Meier" }, "x.Name == \"Meier\"")]
    [InlineData("NAME", FilterOperator.StartsWith, new[] { "me" }, "x.Name.ToUpper().StartsWith(\"ME\")")]
    [InlineData("NAME", FilterOperator.Between, new[] { "A", "M" }, "string.Compare(x.Name, \"A\") >= 0 && string.Compare(x.Name, \"M\") <= 0")]
    [InlineData("NAME", FilterOperator.In, new[] { "A", "B" }, "new string[] { \"A\", \"B\" }.Contains(x.Name)")]
    [InlineData("KUNDENART", FilterOperator.In, new[] { "1", "3" }, "new Kundenart[] { Kundenart.Privat, Kundenart.Behoerde }.Contains(x.Kundenart)")]
    [InlineData("STATUS", FilterOperator.NotEquals, new[] { "AKTIV" }, "x.Status != KundeStatus.Aktiv")]
    [InlineData("GESPERRT", FilterOperator.NotEquals, new[] { "N" }, "x.Gesperrt")]
    [InlineData("GEPRUEFT", FilterOperator.Equals, new[] { "J" }, "x.Geprueft == true")]
    [InlineData("ERSTELLT_AM", FilterOperator.Equals, new[] { "2026-10-01 08:30" }, "x.ErstelltAm == new DateTime(2026, 10, 1, 8, 30, 0)")]
    [InlineData("ERSTELLT_AM", FilterOperator.Lte, new[] { "01.10.2026" }, "x.ErstelltAm < new DateTime(2026, 10, 2)")]
    [InlineData("ERSTELLT_AM", FilterOperator.Gt, new[] { "01.10.2026" }, "x.ErstelltAm >= new DateTime(2026, 10, 2)")]
    [InlineData("ERSTELLT_AM", FilterOperator.NotEquals, new[] { "01.10.2026" }, "(x.ErstelltAm < new DateTime(2026, 10, 1) || x.ErstelltAm >= new DateTime(2026, 10, 2))")]
    [InlineData("ERSTELLT_AM", FilterOperator.Between, new[] { "01.10.2026", "31.10.2026" }, "x.ErstelltAm >= new DateTime(2026, 10, 1) && x.ErstelltAm < new DateTime(2026, 11, 1)")]
    [InlineData("ERSTELLT_AM", FilterOperator.In, new[] { "01.10.2026", "2026-10-03 12:00" }, "((x.ErstelltAm >= new DateTime(2026, 10, 1) && x.ErstelltAm < new DateTime(2026, 10, 2)) || x.ErstelltAm == new DateTime(2026, 10, 3, 12, 0, 0))")]
    [InlineData("MANDANT_ID", FilterOperator.Equals, new[] { "3" }, "EF.Property<int?>(x, \"MandantId\") == 3")]
    [InlineData("EXTERN_ID", FilterOperator.Equals, new[] { "5BAD8F0FCBD99F46A16570867728950E" }, "x.ExternId == new Guid(\"0f8fad5b-d9cb-469f-a165-70867728950e\")")]
    public async Task Each_operator(string column, FilterOperator op, string[] values, string expected) =>
        Assert.Equal($".Where(x => {expected})", await WhereAsync(new FilterCondition(column, op, values)));

    [Fact]
    public async Task Untranslatable_filters_stay_as_comments_disabled_ones_are_ignored()
    {
        var result = LinqFilter.Build(await CodeGenerationModel.PresentationAsync(),
        [
            FilterCondition.Of("LEGACY_CODE", FilterOperator.Equals, "X1"),
            FilterCondition.Of("STATUS", FilterOperator.Equals, "WEG"),
            FilterCondition.Of("KUNDENART", FilterOperator.Contains, "2"),
            FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "4711"),
            FilterCondition.Of("NAME", FilterOperator.Equals, "aus") with { Enabled = false },
        ]);

        Assert.Equal(
            "// Nicht übernommen: LEGACY_CODE = X1: keine Property\r\n" +
            "// Nicht übernommen: STATUS = WEG: 'WEG' ist kein Wert von KundeStatus\r\n" +
            "// Nicht übernommen: KUNDENART enthält 2: „enthält“ nur für string-Properties (Kundenart)\r\n" +
            ".Where(x => x.KundeId == 4711)",
            result.Text);
        Assert.Equal(1, result.Rows);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public async Task Filter_groups_become_or_and_not_with_the_grids_meaning()
    {
        var any = new FilterGroup(1, FilterGroupKind.Any);
        var none = new FilterGroup(2, FilterGroupKind.None);
        var code = await WhereAsync(
            FilterCondition.Of("KUNDE_ID", FilterOperator.Gt, "1"),
            FilterCondition.Of("NAME", FilterOperator.StartsWith, "me") with { Group = any },
            FilterCondition.Of("ERSTELLT_AM", FilterOperator.Equals, "01.10.2026") with { Group = any },
            FilterCondition.Of("ERSTELLT_AM", FilterOperator.NotEquals, "01.10.2026") with { Group = any },
            FilterCondition.Of("KUNDENART", FilterOperator.Equals, "2") with { Group = none });

        Assert.Equal(
            ".Where(x => x.KundeId > 1\r\n" +
            "         && (x.Name.ToUpper().StartsWith(\"ME\")" +
            " || (x.ErstelltAm >= new DateTime(2026, 10, 1) && x.ErstelltAm < new DateTime(2026, 10, 2))" +
            " || (x.ErstelltAm < new DateTime(2026, 10, 1) || x.ErstelltAm >= new DateTime(2026, 10, 2)))\r\n" +
            "         && !(x.Kundenart == Kundenart.Gewerbe))",
            code);
    }

    [Fact]
    public async Task In_a_not_group_nullable_members_are_guarded_so_ef_keeps_their_null_rows()
    {
        // EF Core turns !(x.Umsatz > 1) into NOT ("Umsatz" > 1), which drops the NULL rows the grid keeps; with the guard
        // it writes "Umsatz" IS NULL OR "Umsatz" <= 1. "≠" needs none: EF and the grid count NULL as "not equal".
        var none = new FilterGroup(1, FilterGroupKind.None);
        var code = await WhereAsync(
            FilterCondition.Of("UMSATZ", FilterOperator.Gt, "1") with { Group = none },
            FilterCondition.Of("NAME", FilterOperator.NotEquals, "x") with { Group = none },
            FilterCondition.Of("NAME", FilterOperator.Contains, "a") with { Group = none });

        Assert.Equal(
            ".Where(x => !((x.Umsatz != null && x.Umsatz > 1m) || x.Name != \"x\" || (x.Name != null && x.Name.ToUpper().Contains(\"A\"))))",
            code);
        Assert.Equal(".Where(x => x.Umsatz > 1m)", await WhereAsync(FilterCondition.Of("UMSATZ", FilterOperator.Gt, "1") with { Group = none with { Kind = FilterGroupKind.Any } }));
    }

    [Fact]
    public async Task A_group_with_a_row_without_csharp_form_stays_out_as_a_whole()
    {
        var any = new FilterGroup(1, FilterGroupKind.Any);
        var result = LinqFilter.Build(await CodeGenerationModel.PresentationAsync(),
        [
            FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "4711"),
            FilterCondition.Of("NAME", FilterOperator.Equals, "Meier") with { Group = any },
            FilterCondition.Of("LEGACY_CODE", FilterOperator.Equals, "X1") with { Group = any },
            FilterCondition.Of("NAME", FilterOperator.Equals, "aus") with { Group = new FilterGroup(2, FilterGroupKind.None, Enabled: false) },
        ]);

        Assert.Equal(
            "// Nicht übernommen: ODER-Gruppe (NAME = Meier | LEGACY_CODE = X1) – LEGACY_CODE = X1: keine Property\r\n" +
            ".Where(x => x.KundeId == 4711)",
            result.Text);
        Assert.Equal(1, result.Rows);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task With_a_source_it_is_a_query_for_the_console()
    {
        var presentation = await CodeGenerationModel.PresentationAsync();
        var filters = new[] { FilterCondition.Of("KUNDE_ID", FilterOperator.Gt, "1"), FilterCondition.Of("KUNDENART", FilterOperator.Equals, "1") };

        Assert.Equal("db.Kunden", LinqFilter.Source(presentation));
        Assert.Equal(
            "db.Kunden\r\n" +
            "    .Where(x => x.KundeId > 1\r\n" +
            "             && x.Kundenart == Kundenart.Privat)",
            LinqFilter.Build(presentation, filters, LinqFilter.Source(presentation)).Text);
        Assert.Equal("db.Kunden", LinqFilter.Build(presentation, [], "db.Kunden").Text);
        Assert.Equal("db.Set<Kunde>()", LinqFilter.Source(await CodeGenerationModel.PresentationAsync(dbSet: null)));
    }

    [Fact]
    public void Without_an_entity_there_is_nothing_to_generate() =>
        Assert.NotNull(LinqFilter.Unavailable(TablePresentation.Plain(CodeGenerationModel.Kunden)));
}
