namespace FerretSharp.Core.Query;

/// <summary>
/// One INSERT for many rows at once (WP-42, ADR 0032): every bind variable holds a value per row, and ODP.NET sends them in
/// one round trip (array binding) – a copy over the VPN would otherwise take one round trip per row. Still a single
/// statement on the write path (ADR 0006).
/// </summary>
/// <param name="Rows">Number of rows; every parameter has that many values.</param>
public sealed record BatchSpec(string Sql, IReadOnlyList<BatchParameter> Parameters, int Rows)
{
    /// <summary>The statement once per row with that row's values, for the overview of open changes (WP-30) and the SQL view.</summary>
    public IEnumerable<QuerySpec> Expand() =>
        Enumerable.Range(0, Rows).Select(row => new QuerySpec(Sql, [.. Parameters.Select(p => new QueryParameter(p.Name, p.Values[row], p.Type))]));
}

/// <summary>A bind variable of a <see cref="BatchSpec"/>: one value per row, all of one type.</summary>
public sealed record BatchParameter(string Name, IReadOnlyList<object?> Values, OracleTypeHint Type);
