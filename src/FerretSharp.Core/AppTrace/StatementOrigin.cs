namespace FerretSharp.Core.AppTrace;

/// <summary>Where a statement of a traced app comes from (WP-33).</summary>
public enum StatementOrigin
{
    /// <summary>A LINQ query (<c>CommandSource.LinqQuery</c>), also <c>FromSql</c> with LINQ composed on top.</summary>
    Linq,

    /// <summary>A LINQ query that loads a navigation on first access (lazy-loading proxies).</summary>
    LazyLoading,

    /// <summary><c>FromSql</c>/<c>FromSqlRaw</c> without anything composed on top.</summary>
    FromSql,

    /// <summary><c>Database.ExecuteSql</c>/<c>ExecuteSqlRaw</c>.</summary>
    ExecuteSql,

    SaveChanges,

    /// <summary><c>ExecuteUpdate</c>/<c>ExecuteDelete</c> (EF calls it <c>BulkUpdate</c>).</summary>
    ExecuteUpdate,

    /// <summary>Migrations, value generators, scaffolding.</summary>
    Other,

    /// <summary>EF did not say (EF Core before 7 has no <c>CommandSource</c>).</summary>
    Unknown,
}

public static class StatementOrigins
{
    /// <summary>The origin of an EF <c>CommandSource</c> value (its name as text).</summary>
    public static StatementOrigin FromCommandSource(string? source) => source switch
    {
        "LinqQuery" => StatementOrigin.Linq,
        "FromSqlQuery" => StatementOrigin.FromSql,
        "ExecuteSqlRaw" => StatementOrigin.ExecuteSql,
        "SaveChanges" => StatementOrigin.SaveChanges,
        "BulkUpdate" => StatementOrigin.ExecuteUpdate,
        "Migrations" or "ValueGenerator" or "Scaffolding" => StatementOrigin.Other,
        _ => StatementOrigin.Unknown,
    };
}
