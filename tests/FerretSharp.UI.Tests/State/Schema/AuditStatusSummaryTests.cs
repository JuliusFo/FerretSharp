using FerretSharp.Core.Audit;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// The outdated audit line counts what an audit table not kept up for long has many of, and folds the list away (found
/// by the user: one sentence with the same advice per column filled half the view).
/// </summary>
public sealed class AuditStatusSummaryTests
{
    private static readonly AuditNames Names = new(new TableRef("APP", "AUDIT_KUNDE"), "KUNDE_AUDIT");

    private static AuditColumnIssue Issue(string column, AuditColumnFit fit = AuditColumnFit.Incompatible) => new(column, fit, false, "VARCHAR2(20)", "VARCHAR2(50)");

    private static AuditStatus Outdated(IReadOnlyList<string> reasons, IReadOnlyList<string>? missing = null, IReadOnlyList<AuditColumnIssue>? issues = null) =>
        new(AuditHealth.Outdated, Names, reasons) { MissingColumns = missing ?? [], ColumnIssues = issues ?? [] };

    [Fact]
    public void Few_missing_columns_are_named_and_the_line_needs_no_details()
    {
        var status = Outdated(["Trigger KUNDE_AUDIT füllt nicht alle Spalten"], missing: ["FAX", "MAIL"]);

        Assert.Equal(["FAX, MAIL fehlen in der Audit-Tabelle", "Trigger KUNDE_AUDIT füllt nicht alle Spalten"], AuditStatusSummary.Parts(status));
        Assert.False(AuditStatusSummary.HasDetails(status));
        Assert.Equal(["FAX fehlt in der Audit-Tabelle"], AuditStatusSummary.Parts(Outdated([], missing: ["FAX"])));
    }

    [Fact]
    public void Many_missing_columns_and_column_issues_are_counted_and_listed_in_the_details()
    {
        var status = Outdated([], missing: ["A", "B", "C", "D"], issues: [Issue("X"), Issue("Y", AuditColumnFit.TooShort)]);

        Assert.Equal(["4 Spalten fehlen in der Audit-Tabelle", "2 Audit-Spalten können nicht jeden Wert aufnehmen"], AuditStatusSummary.Parts(status));
        Assert.True(AuditStatusSummary.HasDetails(status));
        Assert.Equal("anderer Typ – die DML scheitert", AuditStatusSummary.Consequence(status.ColumnIssues[0]));
        Assert.StartsWith("zu kurz", AuditStatusSummary.Consequence(status.ColumnIssues[1]));
    }

    [Fact]
    public void One_column_issue_is_counted_in_the_singular()
    {
        var status = Outdated([], issues: [Issue("X")]);

        using (UiCulture.Use("en"))
        {
            Assert.Equal(["1 audit column cannot take every value"], AuditStatusSummary.Parts(status));
        }

        Assert.True(AuditStatusSummary.HasDetails(status));
    }
}
