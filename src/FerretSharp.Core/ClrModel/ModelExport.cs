// The model of a user's EF Core project as FerretSharp.ModelHost writes it (ADR 0009). This file is compiled into both
// FerretSharp.Core and FerretSharp.ModelHost (net8.0, C# 12), so it stays plain: records and System.Text.Json only.
using System.Text.Json;

namespace FerretSharp.Core.ClrModel;

/// <summary>Result of one ModelHost run: the model or what went wrong.</summary>
public sealed record ModelHostResult(ModelExport? Model, ModelHostError? Error)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Prefix of a progress line on the host's stdout (<c>##ferretsharp-progress Baue das Modell</c>); other output (the
    /// project's own console writes) is ignored.
    /// </summary>
    public const string ProgressPrefix = "##ferretsharp-progress ";
}

/// <param name="Kind">One of <see cref="ModelHostErrorKind"/>.</param>
/// <param name="Detail">Technical detail (exception type and stack, inner exceptions) for the log and "Details".</param>
public sealed record ModelHostError(string Kind, string Message, string? Detail = null);

public static class ModelHostErrorKind
{
    public const string Arguments = "Arguments";
    public const string AssemblyNotLoadable = "AssemblyNotLoadable";
    public const string NoContext = "NoContext";
    public const string SeveralContexts = "SeveralContexts";
    public const string NoProvider = "NoProvider";
    public const string ConstructorNeedsServices = "ConstructorNeedsServices";
    public const string ContextCreationFailed = "ContextCreationFailed";
    public const string ModelBuildFailed = "ModelBuildFailed";
    public const string Unexpected = "Unexpected";
}

/// <param name="EfVersion">Informational version of Microsoft.EntityFrameworkCore as loaded, e.g. <c>8.0.11</c>.</param>
/// <param name="CreatedBy">How the context was created: <c>factory</c>, <c>options</c> or <c>parameterless</c>.</param>
/// <param name="DefaultSchema">The model's default schema (<c>HasDefaultSchema</c>); null = the connection's schema.</param>
public sealed record ModelExport(
    int FormatVersion,
    string EfVersion,
    string ContextType,
    string CreatedBy,
    string? DefaultSchema,
    IReadOnlyList<EntityExport> Entities)
{
    /// <summary>2: column facets of properties (3.10) – a cached model of version 1 is read again from the project.</summary>
    public const int CurrentFormatVersion = 2;
}

/// <param name="Name">Entity name as EF knows it (for shared-type entities not the CLR name).</param>
/// <param name="ClrType">Full CLR type name, e.g. <c>Shop.Entities.Kunde</c>.</param>
/// <param name="Table">Mapped table; null if mapped to a view only or not at all (keyless query types).</param>
/// <param name="DbSet">Name of the context's <c>DbSet</c> property for the entity (<c>Kunden</c>); null without one (optional, added in 3.0).</param>
public sealed record EntityExport(
    string Name,
    string ClrType,
    bool IsOwned,
    string? BaseType,
    string? Table,
    string? Schema,
    string? View,
    string? ViewSchema,
    IReadOnlyList<PropertyExport> Properties,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKeyExport> ForeignKeys,
    string? DbSet = null);

/// <param name="ClrType">C# type as written, without nullability: <c>int</c>, <c>string</c>, <c>AuftragStatus</c>.</param>
/// <param name="ClrTypeFull">Full CLR name, e.g. <c>Shop.Entities.AuftragStatus</c>.</param>
/// <param name="Column">Column in the entity's table (in its view if it has no table).</param>
/// <param name="Converter">Value converter type, e.g. <c>JaNeinConverter</c>; null without one.</param>
/// <param name="ProviderClrType">Type the converter stores, e.g. <c>string</c>.</param>
/// <param name="Values">For enums and converted bools: every C# value with its database value.</param>
/// <param name="ViewColumn">Column in the entity's view, if it is mapped to one (queries use the view, SaveChanges the table).</param>
/// <param name="MaxLength">
/// Configured maximum length (<c>HasMaxLength</c>, <c>[MaxLength]</c>, a length in <c>HasColumnType</c>); null if not
/// configured – the provider's default store type says nothing about the project's intent.
/// </param>
/// <param name="Precision">Configured precision (<c>HasPrecision</c>, <c>[Precision]</c>, <c>HasColumnType("NUMBER(12,2)")</c>).</param>
/// <param name="Scale">Configured scale, as <paramref name="Precision"/>.</param>
/// <param name="ColumnNullable">
/// Whether EF treats <see cref="Column"/> as nullable – unlike <see cref="Nullable"/> this includes table sharing
/// (properties of derived types in a TPH table, optional owned types). Null if unknown.
/// </param>
public sealed record PropertyExport(
    string Name,
    string ClrType,
    string ClrTypeFull,
    bool Nullable,
    bool IsShadow,
    string? Column,
    string? ColumnType,
    string? Converter,
    string? ProviderClrType,
    bool IsFlagsEnum,
    IReadOnlyList<ValueMapping>? Values,
    string? ViewColumn = null,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    bool? ColumnNullable = null);

/// <param name="Name">Enum member (<c>Offen</c>) or <c>true</c>/<c>false</c>.</param>
/// <param name="ClrValue">The C# value, invariant: the enum's number, <c>True</c>/<c>False</c>.</param>
/// <param name="ProviderValue">What is stored in the database, invariant text (<c>OFFEN</c>, <c>J</c>, <c>2</c>); null for NULL.</param>
/// <param name="DisplayName">
/// The member's <c>[Display(Name = …)]</c>, resolved through its <c>ResourceType</c> in the host's UI culture; null without
/// one (or if it could not be resolved).
/// </param>
public sealed record ValueMapping(string Name, string ClrValue, string? ProviderValue, string? DisplayName = null);

/// <param name="Properties">The dependent's FK properties.</param>
/// <param name="Navigation">Navigation on the dependent (<c>Auftrag.Kunde</c>); null without one.</param>
/// <param name="InverseNavigation">Navigation on the principal (<c>Kunde.Auftraege</c>); null without one.</param>
public sealed record ForeignKeyExport(
    IReadOnlyList<string> Properties,
    string PrincipalEntity,
    IReadOnlyList<string> PrincipalProperties,
    string? Navigation,
    string? InverseNavigation,
    bool IsUnique);
