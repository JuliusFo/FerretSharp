namespace FerretSharp.Core.Schema;

/// <summary>One row of <c>ALL_ARGUMENTS</c> (only <c>DATA_LEVEL = 0</c>: components of composite types are not parameters).</summary>
/// <param name="Subprogram"><c>OBJECT_NAME</c>: the procedure or function (in a package: the subprogram's name).</param>
/// <param name="Position">0 for the return value of a function, then 1, 2 … for the parameters.</param>
/// <param name="TypeOwner">With <paramref name="TypeName"/>/<paramref name="TypeSubname"/>: a user-defined, record or <c>%ROWTYPE</c> type.</param>
public sealed record ArgumentRow(
    string Subprogram,
    string? Overload,
    int SubprogramId,
    string? Name,
    int Position,
    int Sequence,
    string? InOut,
    string? DataType,
    string? PlsType,
    string? TypeOwner,
    string? TypeName,
    string? TypeSubname,
    bool Defaulted);

/// <summary>A subprogram as <c>ALL_PROCEDURES</c> declares it (a standalone unit: under its own name).</summary>
public sealed record SubprogramRow(string Name, string? Overload, int SubprogramId);

/// <summary>Turns the rows of <c>ALL_PROCEDURES</c> and <c>ALL_ARGUMENTS</c> into subprograms with their parameters.</summary>
public static class PlSqlArguments
{
    /// <summary>
    /// One entry per subprogram and overload, in declaration order. The subprograms come from
    /// <paramref name="declared"/>: Oracle 23 has no <c>ALL_ARGUMENTS</c> row at all for a procedure without parameters
    /// (older versions have a placeholder row without name and type, which is skipped). Position 0 is a function's
    /// return value.
    /// </summary>
    /// <param name="owner">Owner of the unit: types of the same schema are shown without owner.</param>
    public static IReadOnlyList<PlSqlSubprogram> Group(IEnumerable<SubprogramRow> declared, IEnumerable<ArgumentRow> rows, string owner)
    {
        var arguments = rows.ToLookup(r => (r.Subprogram, r.Overload));
        var keys = declared.Select(d => (d.Name, d.Overload, d.SubprogramId))
            .Concat(arguments.Select(g => (g.Key.Subprogram, g.Key.Overload, g.Min(r => r.SubprogramId))))
            .DistinctBy(k => (k.Item1, k.Item2));

        return keys
            .Select(key =>
            {
                var ordered = arguments[(key.Item1, key.Item2)].OrderBy(r => r.Sequence).ThenBy(r => r.Position).ToList();
                var returns = ordered.FirstOrDefault(r => r.Position == 0 && r.Name is null && r.DataType is not null);
                var parameters = ordered
                    .Where(r => r.Position > 0 && (r.Name is not null || r.DataType is not null))
                    .Select(r => new PlSqlParameter(r.Name ?? "", DirectionOf(r.InOut), TypeText(r, owner), r.Defaulted))
                    .ToList();
                return new PlSqlSubprogram(
                    key.Item1,
                    int.TryParse(key.Item2, out var overload) ? overload : null,
                    key.Item3,
                    returns is null ? null : TypeText(returns, owner),
                    parameters);
            })
            .OrderBy(s => s.SubprogramId)
            .ThenBy(s => s.Overload ?? 0)
            .ToList();
    }

    private static PlSqlDirection DirectionOf(string? inOut) => inOut switch
    {
        "OUT" => PlSqlDirection.Out,
        "IN/OUT" => PlSqlDirection.InOut,
        _ => PlSqlDirection.In,
    };

    /// <summary>
    /// The type as shown: a named type (object, collection, package record) by its name, a table's row type as
    /// <c>TABLE%ROWTYPE</c>, scalars by their PL/SQL name (PLS_INTEGER, BOOLEAN, INTEGER) where it says more than the
    /// SQL type.
    /// </summary>
    public static string TypeText(ArgumentRow row, string owner)
    {
        if (row.TypeName is { } typeName)
        {
            var prefix = row.TypeOwner is { } typeOwner && typeOwner != owner ? typeOwner + "." : "";
            if (row.TypeSubname is { } subname)
            {
                return $"{prefix}{typeName}.{subname}";
            }

            return row.DataType == "PL/SQL RECORD" ? $"{prefix}{typeName}%ROWTYPE" : prefix + typeName;
        }

        return row.PlsType ?? row.DataType switch
        {
            "REF CURSOR" => "SYS_REFCURSOR",
            null => "",
            var dataType => dataType,
        };
    }
}
