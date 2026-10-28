namespace FerretSharp.Core.ClrModel;

/// <summary>How code touches a member (WP-40).</summary>
public enum CodeUseKind
{
    /// <summary>A getter call: <c>kunde.Email</c>.</summary>
    Read,

    /// <summary>
    /// A setter call, also an object initializer: <c>kunde.Email = …</c>, <c>new Kunde { Email = … }</c>; and the property an
    /// <c>ExecuteUpdate</c> sets: <c>s.SetProperty(k =&gt; k.Email, …)</c>.
    /// </summary>
    Write,

    /// <summary>A getter in an expression tree (<c>ldtoken</c>): LINQ to Entities, <c>Include</c>, <c>builder.Property(x => x.Email)</c>.</summary>
    Expression,

    /// <summary><c>context.Set&lt;Kunde&gt;()</c>; the member is empty.</summary>
    Query,

    /// <summary>
    /// A getter in the condition of a query: <c>Where(k =&gt; k.Email == x)</c>, <c>Any</c>, <c>FirstOrDefaultAsync</c> … – an
    /// <see cref="Expression"/> the query filters by. Without an index on the column, Oracle may read the whole table.
    /// </summary>
    Filter,
}

/// <summary>One place in compiled code that touches a member of a type of the solution (WP-40, ADR 0030).</summary>
/// <param name="Type">Full CLR name of the type the IL names (<c>Shop.Entities.Kunde</c>, nested with <c>+</c>, generic with <c>`1</c>).</param>
/// <param name="Member">Property name (<c>Email</c>); empty for <see cref="CodeUseKind.Query"/>.</param>
/// <param name="CallerType">The type the use is written in, compiler-generated types (lambdas, async) resolved to it.</param>
/// <param name="CallerMethod">The method as written (<c>GetOffene</c>, also for code in its lambdas and async state machine).</param>
/// <param name="Document">Source file as the PDB has it (the build's path); null without a PDB.</param>
/// <param name="Line">1-based line; null without a PDB or for hidden code.</param>
public sealed record CodeUse(string Type, string Member, CodeUseKind Kind, string CallerType, string CallerMethod, string? Document, int? Line);

/// <summary>A type defined in a scanned assembly and what it derives from, by full name (inheritance across assemblies).</summary>
public sealed record TypeLink(string Type, string? BaseType, IReadOnlyList<string> Interfaces);

/// <summary>What <see cref="IlUsageScanner"/> found in one assembly; cached by <see cref="Mvid"/>.</summary>
/// <param name="SolutionAssemblies">The assembly names uses were kept for (sorted); a cached result for another set is read again.</param>
/// <param name="IsTest">The assembly references a test framework (xunit, NUnit, MSTest, Microsoft.Testing.Platform, TUnit).</param>
/// <param name="HasPdb">A portable PDB was found (next to it or embedded): uses carry file and line.</param>
public sealed record AssemblyUses(
    int FormatVersion,
    Guid Mvid,
    string AssemblyName,
    IReadOnlyList<string> SolutionAssemblies,
    bool IsTest,
    bool HasPdb,
    IReadOnlyList<TypeLink> Types,
    IReadOnlyList<CodeUse> Uses)
{
    /// <summary>
    /// 2: the property an <c>ExecuteUpdate</c> sets is a write, no longer an expression. 3: conditions of <c>Where</c>,
    /// <c>Any</c>, <c>First</c> … are filters. 4: no filter from a value the condition compares with, a tree kept in a
    /// variable or passed in a <c>params</c> array, or <c>All</c>.
    /// </summary>
    public const int CurrentFormatVersion = 4;
}
