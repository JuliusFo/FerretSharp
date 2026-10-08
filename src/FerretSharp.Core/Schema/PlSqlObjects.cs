using System.Text.RegularExpressions;

namespace FerretSharp.Core.Schema;

/// <summary>Stored PL/SQL units shown by FerretSharp (WP-28, view only). Object types (<c>TYPE</c>) are not among them.</summary>
public enum PlSqlKind
{
    Package,
    Procedure,
    Function,
    Trigger,
}

/// <summary>Specification and body of a package; procedures, functions and triggers only have <see cref="Spec"/>.</summary>
public enum PlSqlPart
{
    Spec,
    Body,
}

/// <summary>
/// A PL/SQL unit by owner, name and kind. Triggers have a namespace of their own (a trigger may be named like a
/// table), so the kind is part of the identity.
/// </summary>
public sealed record PlSqlRef(string Owner, string Name, PlSqlKind Kind)
{
    public override string ToString() => $"{Owner}.{Name}";
}

/// <summary>
/// Loaded with the object list on connect. <see cref="Owner"/>/<see cref="Name"/> denote the real object; one of another
/// schema reached through a synonym carries it in <see cref="Synonym"/>.
/// </summary>
/// <param name="Status"><c>ALL_OBJECTS.STATUS</c> of the unit (of a package: of its specification).</param>
/// <param name="BodyStatus">Status of a package body; null if the package has none or the session cannot see it.</param>
public sealed record PlSqlObjectSummary(string Owner, string Name, PlSqlKind Kind, string Status, string? BodyStatus = null, SynonymInfo? Synonym = null)
{
    public PlSqlRef Ref => new(Owner, Name, Kind);

    /// <summary>The name users know the object by: the synonym if there is one.</summary>
    public string DisplayName => Synonym?.Name ?? Name;

    /// <summary>Invalid specification or body: recompiled on the next call, which fails if the code has errors.</summary>
    public bool IsInvalid => Status == "INVALID" || BodyStatus == "INVALID";

    /// <summary>A trigger switched off (<c>ALL_TRIGGERS.STATUS = 'DISABLED'</c>); false for everything else.</summary>
    public bool IsDisabled { get; init; }

    /// <summary>The <c>ALL_OBJECTS.OBJECT_TYPE</c> of a part, e.g. <c>PACKAGE BODY</c>.</summary>
    public string ObjectType(PlSqlPart part = PlSqlPart.Spec) => PlSqlKinds.ObjectType(Kind, part);
}

public static class PlSqlKinds
{
    /// <summary><c>ALL_OBJECTS.OBJECT_TYPE</c> of a kind and part.</summary>
    public static string ObjectType(PlSqlKind kind, PlSqlPart part = PlSqlPart.Spec) => (kind, part) switch
    {
        (PlSqlKind.Package, PlSqlPart.Body) => "PACKAGE BODY",
        (PlSqlKind.Package, _) => "PACKAGE",
        (PlSqlKind.Procedure, _) => "PROCEDURE",
        (PlSqlKind.Function, _) => "FUNCTION",
        _ => "TRIGGER",
    };

    /// <summary>Kind and part of an <c>OBJECT_TYPE</c> (as in <c>ALL_DEPENDENCIES</c>); null for other objects.</summary>
    public static (PlSqlKind Kind, PlSqlPart Part)? Of(string objectType) => objectType switch
    {
        "PACKAGE" => (PlSqlKind.Package, PlSqlPart.Spec),
        "PACKAGE BODY" => (PlSqlKind.Package, PlSqlPart.Body),
        "PROCEDURE" => (PlSqlKind.Procedure, PlSqlPart.Spec),
        "FUNCTION" => (PlSqlKind.Function, PlSqlPart.Spec),
        "TRIGGER" => (PlSqlKind.Trigger, PlSqlPart.Spec),
        _ => null,
    };
}

/// <summary>Synonym targets of a schema: tables/views/materialized views and PL/SQL units, from one query.</summary>
public sealed record SynonymTargets(IReadOnlyList<TableSummary> Tables, IReadOnlyList<PlSqlObjectSummary> PlSql)
{
    public static readonly SynonymTargets None = new([], []);
}

/// <summary>Header of a PL/SQL tab.</summary>
/// <param name="Status">Status of the unit (package: specification); "N/A" if the session cannot see it.</param>
/// <param name="BodyStatus">Status of a package body; null without (visible) body.</param>
/// <param name="AuthId">DEFINER or CURRENT_USER (<c>ALL_PROCEDURES.AUTHID</c>); null for triggers.</param>
/// <param name="Trigger">Event, timing and table of a trigger; null for other units.</param>
public sealed record PlSqlObjectInfo(
    string Status,
    string? BodyStatus,
    DateTime? Created,
    DateTime? LastDdl,
    DateTime? BodyLastDdl,
    string? AuthId,
    TriggerInfo? Trigger);

/// <param name="Timing"><c>ALL_TRIGGERS.TRIGGER_TYPE</c>, e.g. BEFORE EACH ROW, AFTER STATEMENT, COMPOUND, INSTEAD OF.</param>
/// <param name="Event"><c>TRIGGERING_EVENT</c>, e.g. INSERT OR UPDATE, LOGON.</param>
/// <param name="Table">Table or view the trigger is on; null for schema and database triggers.</param>
/// <param name="BaseObjectType">TABLE, VIEW, SCHEMA, DATABASE …</param>
/// <param name="When"><c>WHEN</c> condition of a row trigger.</param>
public sealed record TriggerInfo(string Timing, string Event, TableRef? Table, string BaseObjectType, string? When, bool Enabled);

/// <summary>Source text of one part, one entry per <c>ALL_SOURCE.LINE</c> without its line break.</summary>
public sealed record PlSqlSource(IReadOnlyList<string> Lines)
{
    /// <summary>
    /// No line at all: the part does not exist or the session may not see it (the body of another schema's package
    /// needs the DEBUG privilege on it, or ownership).
    /// </summary>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Obfuscated with the PL/SQL wrap utility: the text is unreadable and cannot be turned back.</summary>
    public bool IsWrapped => PlSqlSourceText.IsWrapped(Lines);

    /// <summary>
    /// The lines joined by '\n'. Line n of the text is <c>LINE</c> n of the dictionary, so compile errors point to the
    /// right place.
    /// </summary>
    public string Text => string.Join('\n', Lines);
}

public static partial class PlSqlSourceText
{
    /// <summary>The <c>TEXT</c> of a line without the line break it ends with (\n, \r\n or \r).</summary>
    public static string LineOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var end = text.Length;
        if (end > 0 && text[end - 1] == '\n')
        {
            end--;
        }

        if (end > 0 && text[end - 1] == '\r')
        {
            end--;
        }

        return text[..end];
    }

    /// <summary>
    /// Wrapped units start with "PACKAGE BODY name wrapped" and the line "a000000"; their dictionary lines hold many text
    /// lines each, so the first two lines are read from the joined text.
    /// </summary>
    public static bool IsWrapped(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return false;
        }

        var head = string.Join('\n', lines.Take(2)).Split('\n', 3);
        return head.Length >= 2 && WrappedHeader().IsMatch(head[0]) && head[1].Trim() == "a000000";
    }

    [GeneratedRegex(@"\A\s*(?:(?:NON)?EDITIONABLE\s+)?(?:PACKAGE|PROCEDURE|FUNCTION|TYPE|LIBRARY)\b.*\bwrapped\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WrappedHeader();
}

public enum PlSqlDirection
{
    In,
    Out,
    InOut,
}

/// <param name="Name">Parameter name as declared (upper case unless quoted).</param>
/// <param name="Type">Type as the dictionary knows it: <c>%TYPE</c> is already resolved, e.g. NUMBER, VARCHAR2, KUNDEN%ROWTYPE.</param>
/// <param name="HasDefault">Declared with DEFAULT (or :=); the dictionary does not keep the expression, the source shows it.</param>
public sealed record PlSqlParameter(string Name, PlSqlDirection Direction, string Type, bool HasDefault);

/// <summary>A procedure or function: standalone, or declared in a package specification.</summary>
/// <param name="Overload">1, 2 … for overloaded names in a package; null if the name is not overloaded.</param>
/// <param name="SubprogramId">Position of the declaration in the package (orders the list).</param>
/// <param name="ReturnType">Return type of a function; null for a procedure.</param>
public sealed record PlSqlSubprogram(string Name, int? Overload, int SubprogramId, string? ReturnType, IReadOnlyList<PlSqlParameter> Parameters)
{
    public bool IsFunction => ReturnType is not null;
}

/// <summary>An entry of <c>ALL_ERRORS</c>: a compile error or (with PLSQL_WARNINGS) a warning.</summary>
/// <param name="Line">Line in the source of <see cref="Part"/>, from 1.</param>
/// <param name="Column">Character position in the line, from 1.</param>
/// <param name="Text">Message with its code, e.g. "PLS-00201: identifier 'X' must be declared".</param>
public sealed record PlSqlError(PlSqlPart Part, int Line, int Column, string Text, bool IsWarning);

/// <summary>A line of the schema's PL/SQL source that contains the searched text.</summary>
public sealed record SourceHit(PlSqlRef Object, PlSqlPart Part, int Line, string Text);

/// <summary>A row of the object list: <c>OBJECT_NAME</c>, <c>OBJECT_TYPE</c>, <c>STATUS</c>.</summary>
public sealed record PlSqlObjectRow(string Name, string ObjectType, string Status);

public static class PlSqlObjectList
{
    /// <summary>
    /// One entry per unit, sorted by name: a package's specification and body become one entry with both statuses.
    /// A body without visible specification is left out.
    /// </summary>
    /// <param name="disabledTriggers">Names of switched-off triggers.</param>
    public static IReadOnlyList<PlSqlObjectSummary> Combine(string owner, IEnumerable<PlSqlObjectRow> rows, IReadOnlySet<string> disabledTriggers)
    {
        var list = rows.ToList();
        var bodies = list.Where(r => r.ObjectType == "PACKAGE BODY").ToDictionary(r => r.Name, r => r.Status, StringComparer.Ordinal);
        return list
            .Select(r => (Row: r, Kind: PlSqlKinds.Of(r.ObjectType)))
            .Where(x => x.Kind is { Part: PlSqlPart.Spec })
            .Select(x => new PlSqlObjectSummary(owner, x.Row.Name, x.Kind!.Value.Kind, x.Row.Status,
                x.Kind.Value.Kind == PlSqlKind.Package ? bodies.GetValueOrDefault(x.Row.Name) : null)
            {
                IsDisabled = x.Kind.Value.Kind == PlSqlKind.Trigger && disabledTriggers.Contains(x.Row.Name),
            })
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .ThenBy(o => o.Kind)
            .ToList();
    }
}
