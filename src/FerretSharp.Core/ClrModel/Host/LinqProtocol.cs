// Messages between FerretSharp and FerretSharp.ModelHost in console mode (LINQ console, WP-13, ADR 0011): one JSON object
// per line over a named pipe. Compiled into both FerretSharp.Core and FerretSharp.ModelHost (net8.0, C# 12), so it stays
// plain: records and System.Text.Json only.
using System.Text.Json;

namespace FerretSharp.Core.ClrModel;

public static class LinqProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Request kinds.</summary>
    public const string Run = "run";
    public const string Complete = "complete";
    public const string Shutdown = "shutdown";

    /// <summary>Kinds of completion items (the editor picks the icon).</summary>
    public const string Property = "Property";
    public const string Field = "Field";
    public const string Method = "Method";
    public const string ExtensionMethod = "ExtensionMethod";
    public const string Class = "Class";
    public const string Struct = "Struct";
    public const string Interface = "Interface";
    public const string Enum = "Enum";
    public const string EnumMember = "EnumMember";
    public const string Variable = "Variable";
    public const string Namespace = "Namespace";
    public const string Event = "Event";
    public const string Keyword = "Keyword";

    /// <summary>Command kinds, as EF executes them.</summary>
    public const string Reader = "reader";
    public const string NonQuery = "nonquery";
    public const string Scalar = "scalar";

    /// <summary>Sections of the console the code comes from (diagnostics point into them).</summary>
    public const string CodeSection = "code";
    public const string VariablesSection = "variables";
}

/// <param name="Kind"><see cref="LinqProtocol.Run"/>, <see cref="LinqProtocol.Complete"/> or <see cref="LinqProtocol.Shutdown"/>.</param>
/// <param name="Variables">Declarations the user keeps apart from the query (<c>var customerId = 4711;</c>).</param>
/// <param name="Section">Completion: the section the cursor is in (<see cref="LinqProtocol.CodeSection"/> or <see cref="LinqProtocol.VariablesSection"/>).</param>
/// <param name="Offset">Completion: the cursor as offset (UTF-16) into that section's text.</param>
public sealed record LinqRequest(string Kind, int Id, string? Code = null, string? Variables = null, string? Section = null, int Offset = 0);

/// <summary>Answer to a request; <see cref="Id"/> 0 is sent once when the host is ready (or failed to start).</summary>
public sealed record LinqResponse(int Id, LinqRunResult? Run = null, ModelHostError? Error = null, IReadOnlyList<LinqCompletionItem>? Completion = null);

/// <param name="Label">What the list shows: <c>Kunden</c>, <c>Where</c>, <c>List&lt;T&gt;</c>.</param>
/// <param name="InsertText">What goes into the editor: the name (<c>@class</c> for a keyword used as name).</param>
/// <param name="Kind">One of the kinds in <see cref="LinqProtocol"/> (<see cref="LinqProtocol.Property"/> …).</param>
/// <param name="Detail">Type or signature: <c>DbSet&lt;Kunde&gt;</c>, <c>IQueryable&lt;Kunde&gt; Where(Expression&lt;…&gt; predicate) (+1)</c>.</param>
/// <param name="Rank">Lower comes first: members of the type itself, inherited ones, extension methods, those of <c>object</c>.</param>
public sealed record LinqCompletionItem(string Label, string InsertText, string Kind, string? Detail, int Rank);

/// <param name="Diagnostics">Compiler errors and warnings, positioned in the user's sections.</param>
/// <param name="UnknownNames">Names the code uses but nobody declares, with a suggested declaration; the code did not run.</param>
/// <param name="AutoDeclared">Names FerretSharp declared itself (the context, a cancellation token): <c>_context</c>, <c>ct</c>.</param>
/// <param name="Commands">The commands EF would have sent, in order; none ran.</param>
/// <param name="ResultType">C# type of what the code returned (<c>List&lt;Kunde&gt;</c>, <c>int</c>); null without a value.</param>
/// <param name="Exception">What the code threw; after captured commands usually only EF reacting to the empty result.</param>
public sealed record LinqRunResult(
    IReadOnlyList<LinqDiagnostic> Diagnostics,
    IReadOnlyList<LinqUnknownName> UnknownNames,
    IReadOnlyList<string> AutoDeclared,
    IReadOnlyList<CapturedCommand> Commands,
    string? ResultType,
    string? Exception,
    TimeSpan Elapsed)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == "error");
}

/// <param name="Section"><see cref="LinqProtocol.CodeSection"/> or <see cref="LinqProtocol.VariablesSection"/>.</param>
/// <param name="Line">1-based, within the section.</param>
/// <param name="Column">1-based.</param>
/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
public sealed record LinqDiagnostic(string Section, int Line, int Column, int EndLine, int EndColumn, string Severity, string Id, string Message);

/// <param name="Declaration">A declaration to add to the variables, e.g. <c>int customerId = 0;</c>.</param>
/// <param name="TypeKnown">Whether the type comes from how the name is used (otherwise the user has to fill it in).</param>
public sealed record LinqUnknownName(string Name, string Declaration, bool TypeKnown);

/// <param name="Kind"><see cref="LinqProtocol.Reader"/>, <see cref="LinqProtocol.NonQuery"/> or <see cref="LinqProtocol.Scalar"/>.</param>
public sealed record CapturedCommand(string Kind, string Sql, IReadOnlyList<CapturedParameter> Parameters);

/// <param name="Name">As EF names it, without the leading colon: <c>kundeId_0</c>.</param>
/// <param name="OracleType"><c>OracleDbType</c> name (<c>Int32</c>, <c>NVarchar2</c>); null if the provider did not set one.</param>
/// <param name="ClrType">Type of the value: <c>Int32</c>, <c>String</c>, <c>DateTime</c>, <c>Byte[]</c> …; null for NULL.</param>
/// <param name="Value">Invariant text (ISO dates with <c>O</c>, Base64 for bytes); null for NULL.</param>
public sealed record CapturedParameter(string Name, string? OracleType, string? ClrType, string? Value);
