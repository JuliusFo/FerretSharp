namespace FerretSharp.Core.Compare;

/// <summary>A statement of the DDL proposal; never run by FerretSharp before WP-22.</summary>
/// <param name="Object">The table, view or materialized view it changes.</param>
/// <param name="Sql">One statement without trailing semicolon; commented out (<c>-- …</c>) when only a hint (e.g. DROP).</param>
/// <param name="Warning">Why this step needs care (data loss, may fail on existing data, …); null if none.</param>
public sealed record DdlStep(string Object, string Sql, string? Warning = null);

/// <summary>The DDL that makes <see cref="Target"/> look like <see cref="Reference"/> (indexes into the comparison's sides).</summary>
public sealed record DdlProposal(int Reference, int Target, IReadOnlyList<DdlStep> Steps)
{
    /// <summary>The steps as a script: statements ending with <c>;</c>, warnings as comments above them.</summary>
    public string Script => throw new NotImplementedException();
}

/// <summary>Builds the DDL proposal from a comparison (WP-20). Placeholder of the contract – replaced by the implementation.</summary>
public static class SchemaDdl
{
    /// <param name="rows">Object rows to cover; null = all differing objects.</param>
    public static DdlProposal Align(SchemaComparison comparison, int reference, int target, IReadOnlyList<CompareRow>? rows = null) =>
        throw new NotImplementedException();
}
