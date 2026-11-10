using FerretSharp.Core.Compare;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// View definitions in the schema comparison (WP-82) against Oracle: the proposal for the views of
/// <see cref="SnapshotSchema.Owner"/> runs through the schema path in <see cref="SnapshotSchema.ViewTarget"/>, and a new
/// comparison then finds their definitions and status alike.
/// </summary>
public sealed class ViewCompareTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    /// <summary>The views to align; V_KAPUTT is invalid in the reference and its table missing in the target.</summary>
    private static readonly string[] Views = ["MV_MENGE", "V_KUNDE", "V_KUNDE_AKTIV", "V_KUNDE_NAMEN", "V_LANG"];

    private OracleSession? _reference;
    private OracleSession? _target;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var (profile, _) = oracle.RequireProfile();
        await SnapshotSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        Assert.SkipUnless(SnapshotSchema.Available, SnapshotSchema.UnavailableReason ?? "Snapshot schema not created.");

        _reference = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile with { User = SnapshotSchema.Owner }, SnapshotSchema.Password),
            new SessionContext("FerretSharp", "View compare tests"), Ct);
        _target = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile with { User = SnapshotSchema.ViewTarget }, SnapshotSchema.Password),
            new SessionContext("FerretSharp", "View compare tests"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_reference is not null)
        {
            await _reference.DisposeAsync();
        }

        if (_target is not null)
        {
            await _target.DisposeAsync();
        }
    }

    [Fact]
    public async Task Proposed_view_statements_run_and_align_the_definitions()
    {
        var comparison = await CompareAsync();
        var rows = comparison.Objects.Where(r => Views.Contains(r.Name)).ToList();
        Assert.Equal(Views.Length, rows.Count);
        Assert.True(rows.Single(r => r.Name == "V_KUNDE").Children.Single(c => c.Kind == CompareKind.Definition).Differs, "V_KUNDE lacks WITH READ ONLY");

        var proposal = SchemaDdl.Align(comparison, 0, 1, rows);
        var statements = proposal.Steps.Where(s => s.IsExecutable).ToList();
        Assert.Contains(statements, s => s.Sql.StartsWith("CREATE OR REPLACE VIEW", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Sql.StartsWith("CREATE MATERIALIZED VIEW", StringComparison.Ordinal));
        Assert.True(
            statements.FindIndex(s => s.Object == "V_KUNDE_AKTIV") < statements.FindIndex(s => s.Object == "V_KUNDE_NAMEN"),
            proposal.Script);
        foreach (var step in statements)
        {
            await _target!.ExecuteDdlAsync(step.Sql, Ct); // the schema path takes every one of them
        }

        var again = await CompareAsync();
        foreach (var row in again.Objects.Where(r => Views.Contains(r.Name)))
        {
            Assert.All(row.Children.Where(c => c.Kind is CompareKind.Definition or CompareKind.Status), c => Assert.False(c.Differs, $"{row.Name} {c.Name}: {c.Cells[1].Definition}"));
            Assert.All(row.Children.Where(c => c.Kind == CompareKind.Column), c => Assert.Equal(CellState.Same, c.Cells[1].State));
        }

        Assert.DoesNotContain(SchemaDdl.Align(again, 0, 1, again.Objects.Where(r => Views.Contains(r.Name)).ToList()).Steps, s => s.IsExecutable);
        var info = await new OracleSchemaReader(_target!).GetObjectInfoAsync(new TableSummary(SnapshotSchema.ViewTarget, "V_KUNDE_AKTIV", TableKind.View), Ct);
        Assert.Equal("Aktive Kunden", info.Comment);
    }

    [Fact]
    public async Task Invalid_view_shows_in_its_status_and_not_in_its_columns()
    {
        var comparison = await CompareAsync();
        var kaputt = comparison.Objects.Single(r => r.Name == "V_KAPUTT");

        var status = kaputt.Children.Single(c => c.Kind == CompareKind.Status);
        Assert.Equal("INVALID", status.Cells[0].Definition);
        Assert.Contains(kaputt.Children, c => c.Kind == CompareKind.Column && c.Cells[0].State == CellState.Unknown);
    }

    private async Task<SchemaComparison> CompareAsync()
    {
        var reference = await new OracleSchemaReader(_reference!).ReadSnapshotAsync(SnapshotSchema.Owner, null, Ct);
        var target = await new OracleSchemaReader(_target!).ReadSnapshotAsync(SnapshotSchema.ViewTarget, null, Ct);
        return SchemaDiff.Compare([reference, target], new CompareOptions(Reference: 0));
    }
}
